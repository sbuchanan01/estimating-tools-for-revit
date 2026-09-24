using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Single-source-of-truth for "how many labour-minutes does THIS part
    /// consume in each labour table?" Used by:
    ///   • AncillaryLabourPricingSource → sums values to compute the
    ///     part's total labour cost (E-Rate or F-Rate).
    ///   • LaborEstimateCommand → keeps the per-table breakdown so the
    ///     report can roll up across the project by labour table.
    ///
    /// Computation mirrors Fabrication's Cost Breakdown UI:
    ///   1. Walk part.GetPartAncillaryUsage()
    ///   2. cfg.GetAncillaryName(anc.AncillaryId) returns the table key
    ///      (e.g. "GRC_Soldered (ASME B16 22)") — exact match against the
    ///      etimes/ftimes parser. No CSV mapping needed.
    ///   3. Size source depends on UsageType:
    ///        Connector  → from part.ConnectorManager.Connectors[i].Radius
    ///                     (× 24 to go from Revit feet to inches)
    ///        Body/etc   → from anc.AncillaryWidthOrDiameter / Depth
    ///   4. parser.GetTable(name).GetMinutesForSize(size) → minutes
    ///   5. Multiplied by anc.Quantity (for connector-scoped, paired with
    ///      the first N connector sizes from ConnectorManager).
    /// </summary>
    /// <summary>
    /// Per-table labour minutes for a part, split by which rate they
    /// should be billed at.
    /// <list type="bullet">
    /// <item><c>Regular</c> — minutes charged at the section's labour
    /// rate ($/hr from cost.map, e.g. $30/hr Skilled). Covers both
    /// install AND fab for the primary part-table line (e.g. "90 Radius
    /// Bend") plus all install-side ancillary minutes (flange-bolt
    /// install on a valve, etc.).</item>
    /// <item><c>ConnectorMicroRate</c> — minutes charged at Fabrication
    /// ESTmep's internal $1/hr fab-ancillary micro-rate. Populated
    /// when <c>isFabrication=true</c> AND the source is an ancillary
    /// (any UsageType — Connector, Airturn, Seam, Stiffener, etc.).
    /// Materializes every row Fab UI groups under "Ancillary Fabrication
    /// Cost" with the "@ 1.00 $/(hrs)" suffix. Historical name kept for
    /// API stability — semantics broadened beyond UsageType=Connector
    /// when Airturn vanes were verified to share the same $1/hr rate.</item>
    /// </list>
    /// </summary>
    public sealed class LabourMinutesResult
    {
        public Dictionary<string, double> Regular { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> ConnectorMicroRate { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns every entry in both buckets — used by
        /// callers that don't care about the rate distinction.</summary>
        public IEnumerable<KeyValuePair<string, double>> AllEntries()
        {
            foreach (var kv in Regular) yield return kv;
            foreach (var kv in ConnectorMicroRate) yield return kv;
        }
    }

    public static class AncillaryLabourBreakdown
    {
        /// <summary>Fabrication ESTmep's internal $1/hr micro-rate used
        /// for ALL fab-side ancillary minutes (Connector, Airturn, Seam,
        /// Stiffener, …). Not present in cost.map — hardcoded in the
        /// cost engine itself. Verified against 90 Radius Bend ($0.02
        /// connector fab) and 90 Square Bend ($0.31 = $0.02 connector +
        /// $0.29 airturn fab).</summary>
        public const double ConnectorFabMicroRate = 1.0;

        /// <summary>
        /// Returns {tableName → totalMinutes for this part}. Caller passes
        /// the parser (etimes or ftimes) AND a flag indicating which side
        /// is being computed — Fabrication or Installation. This matters
        /// for pipes: installation tables vary by material+schedule+end
        /// type, while fabrication tables are constant ("Tube End - Cut
        /// Threads") across pipe materials in known configs.
        ///
        /// When <paramref name="itmIndex"/> is provided, fittings/valves
        /// also receive a per-part <b>install-table contribution</b> looked
        /// up from the part's .ITM file (the bracket-string under
        /// <c>[Mechanical Valves]</c> / <c>[Mechanical Fittings]</c> /
        /// <c>[Mechanical Joints]</c> etc.). This plugs the "Installation
        /// Table Cost" row that Fabrication's ESTmep Cost Breakdown shows
        /// at the top of the install section — it's NOT exposed via
        /// <c>part.GetPartAncillaryUsage()</c> at runtime (only the
        /// fixings/bolts/nuts are), so the ITM is the only source.
        /// Verified: 1550CB2 (FLG) 6" valve gains $3.30 (6.60 mins × $30/hr ÷ 60)
        /// to match Fab UI total of $9.30 install.
        /// </summary>
        public static LabourMinutesResult ComputeMinutesByTable(
            FabricationPart part, EtimesBreakpointParser parser,
            bool isFabrication, ItmFileIndex? itmIndex = null)
        {
            var result = new LabourMinutesResult();
            if (part == null || parser == null) return result;

            // LocationCurve parts (pipes AND straight ducts) don't
            // carry ancillaries — labour comes from a direct table
            // reference. ITM lookup is primary (works for both); pipe
            // fuzzy match is the fallback when ITM resolution misses.
            if (part.Location is LocationCurve)
                return ComputePipeMinutesByTable(part, parser, isFabrication, itmIndex);

            FabricationConfiguration? cfg = null;
            try { cfg = FabricationConfiguration.GetFabricationConfiguration(part.Document); } catch { }
            if (cfg == null) return result;

            IList<FabricationAncillaryUsage>? usages = null;
            try { usages = part.GetPartAncillaryUsage(); } catch { }
            if (usages == null || usages.Count == 0) return result;

            var connectorSizesInches = GetConnectorSizesInches(part);

            foreach (var anc in usages)
            {
                string ancName = "";
                try { ancName = cfg.GetAncillaryName(anc.AncillaryId) ?? ""; } catch { }

                double qty = 0;
                try { qty = anc.Quantity; } catch { }
                if (qty <= 0) qty = 1;

                // ── Path 1: breakpoint table (fittings, connector ancillaries).
                // Matches when GetAncillaryName resolves to an etimes/ftimes
                // table — e.g. "GRC_Soldered (ASME B16 22)" for a copper
                // soldered elbow connector.
                double partMinutes = 0;
                if (!string.IsNullOrEmpty(ancName))
                {
                    var table = parser.GetTable(ancName);
                    if (table != null)
                    {
                        if (IsConnectorScoped(anc) && connectorSizesInches.Count > 0)
                        {
                            int n = Math.Min((int)Math.Round(qty), connectorSizesInches.Count);
                            for (int i = 0; i < n; i++)
                            {
                                var m = table.GetMinutesForSize(connectorSizesInches[i]);
                                if (m.HasValue && m.Value > 0) partMinutes += m.Value;
                            }
                        }
                        else
                        {
                            double size = 0;
                            try { size = anc.AncillaryWidthOrDiameter; } catch { }
                            if (size <= 0)
                            {
                                try { size = anc.AncillaryDepth; } catch { }
                            }
                            if (size > 0)
                            {
                                var m = table.GetMinutesForSize(size);
                                if (m.HasValue && m.Value > 0) partMinutes = m.Value * qty;
                            }
                        }
                    }
                }

                // ── Path 2: per-ProductCode flat-list (fixings: bolts, nuts,
                // gaskets, washers). Used when path 1 didn't produce any
                // minutes — typically because the ancillary's GetAncillaryName
                // isn't a breakpoint-table name (it's the fixing's own
                // description, like "3/4'' Bolt (ASME B18.2.1)").
                if (partMinutes <= 0)
                {
                    string code = "";
                    try { code = anc.ProductCode ?? ""; } catch { }
                    var perQty = parser.GetAncillaryMinsForProductCode(code);
                    if (perQty.HasValue && perQty.Value > 0)
                        partMinutes = perQty.Value * qty;
                }

                if (partMinutes > 0)
                {
                    // Bucket by GetAncillaryName when we have one (gives the
                    // labor estimate report a meaningful per-row label, e.g.
                    // "3/4'' Bolt (ASME B18.2.1)"). Falls back to "Fixings"
                    // for unnamed ancillaries.
                    string bucket = !string.IsNullOrEmpty(ancName) ? ancName : "Fixings";
                    // ALL fabrication-side ancillary minutes charge at
                    // Fab's internal $1/hr micro-rate — that's the
                    // "Ancillary Fabrication Cost" group in ESTmep's
                    // Cost Breakdown, which encompasses every sub-rate
                    // section we've observed:
                    //   • Connector Fabrication Cost  (UsageType=Connector;
                    //     STANDING S, FLAT DRIVE on duct ends)
                    //   • Airturn Fabrication Cost    (UsageType=Airturn;
                    //     DM TURNING VANE, DM RAIL on square bends)
                    //   • Seam / Splitter / Stiffener fab costs (defensive;
                    //     same UI section, same $1/hr rate in every
                    //     sample we've seen)
                    // Only the primary fab table (per-part ITM bracket,
                    // e.g. "90 Square Bend" → $14.78) lives in Regular
                    // and bills at the section rate ($30/hr Skilled).
                    //
                    // Install side keeps section-rate for ancillary
                    // minutes — verified on the 6" 1550CB2 valve where
                    // flange-bolt install was 6.60 mins × $30/hr/60 =
                    // $3.30 (matching Fab UI exactly).
                    var sink = isFabrication
                        ? result.ConnectorMicroRate
                        : result.Regular;
                    if (sink.ContainsKey(bucket))
                        sink[bucket] += partMinutes;
                    else
                        sink[bucket]  = partMinutes;
                }
            }

            // ── Per-part install/fab table contribution (ITM-derived) ─────
            // Plugs the "Installation Table Cost" / "Fabrication Table Cost"
            // rows that ESTmep shows at the top of each section (e.g.
            // VAL_Flange-150# (ASTM 16.5) for a flanged valve install, or
            // the [Duct Rectangular] 90 Radius Bend bracket for a duct
            // fitting's fab+install). NOT exposed via GetPartAncillaryUsage();
            // the bracket-string in the part's .ITM is the only source.
            // Single contribution per part — one table lookup at the part's
            // nominal size, NOT per-connector.
            //
            // Side selection:
            //   • Install side reads entry.InstallTableKey
            //     (fittings/valves AND duct fittings).
            //   • Fab side reads entry.FabricationTableKey
            //     (primarily duct fittings — pipe fittings typically have
            //     no fab bracket in the configs we've seen, which simply
            //     skips this block).
            //
            // Lookup priority for the part → ITM entry resolution:
            //   1. FabricationPartType.ItemPath — authoritative.
            //   2. Family/type name → filename — stock Fab content.
            //   3. ProductCode fallback — first-wins, can collide across
            //      unrelated ITMs (e.g. ADSK_30111286 is BOTH a Milwaukee
            //      gate valve AND a strainer). Used only as last resort.
            //
            // Skipped:
            //   • no itmIndex passed in (back-compat callers)
            //   • no ITM entry resolves for the part
            //   • selected-side TableKey is null (e.g. install-only bracket
            //     when computing fab, or vice versa)
            //   • table key not in the parser (etimes for install, ftimes
            //     for fab)
            //   • nominal size unresolvable
            if (itmIndex != null)
            {
                var entry = ResolveItmEntry(part, itmIndex);
                string? tableKey = isFabrication
                    ? entry?.FabricationTableKey
                    : entry?.InstallTableKey;
                if (!string.IsNullOrEmpty(tableKey))
                {
                    var table = parser.GetTable(tableKey!);
                    if (table != null)
                    {
                        // Half-periphery (W+H) for rectangular duct
                        // connectors; falls back to max diameter from
                        // round/pipe connectors. This matches the same
                        // breakpoint-axis convention duct labour tables
                        // use ("Half Periphery <=").
                        double nominalSize = GetFittingLookupSize(part);
                        if (nominalSize <= 0 && connectorSizesInches.Count > 0)
                        {
                            // Older fallback: largest round-connector
                            // diameter. Kept for the pipe-fitting path
                            // since GetFittingLookupSize already covers
                            // it, but harmless as a safety net.
                            foreach (var s in connectorSizesInches)
                                if (s > nominalSize) nominalSize = s;
                        }
                        if (nominalSize > 0)
                        {
                            var m = table.GetMinutesForSize(nominalSize);
                            if (m.HasValue && m.Value > 0)
                            {
                                // Per-part install/fab table is the
                                // primary labour line for fittings (e.g.
                                // "90 Radius Bend" → $14.78 on a duct
                                // 90 elbow). Always charged at the
                                // section labour rate — micro-rate is
                                // only for connector-scoped ancillaries.
                                var sink = result.Regular;
                                if (sink.ContainsKey(tableKey!))
                                    sink[tableKey!] += m.Value;
                                else
                                    sink[tableKey!]  = m.Value;
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Resolves a part to its source <see cref="ItmFileParser.ItmEntry"/>
        /// via the three-step chain:
        /// <list type="number">
        /// <item><description><c>FabricationPartType.ItemPath</c> (reflected;
        /// authoritative).</description></item>
        /// <item><description>Family / type name → <c>.ITM</c> filename match
        /// (works for stock Fab content where filename == family name).</description></item>
        /// <item><description><c>BuiltInParameter.FABRICATION_PRODUCT_CODE</c>
        /// fallback — collision-prone, used only when (1) and (2) miss.</description></item>
        /// </list>
        /// Returns null when none of the strategies produce an entry.
        /// </summary>
        public static ItmFileParser.ItmEntry? ResolveItmEntry(
            FabricationPart part, ItmFileIndex itmIndex)
        {
            string itemPath = GetPartItemPath(part);
            if (!string.IsNullOrEmpty(itemPath))
            {
                var e = itmIndex.GetByItemPath(itemPath);
                if (e != null) return e;
            }
            string familyKey = GetPartFamilyKey(part);
            if (!string.IsNullOrEmpty(familyKey))
            {
                var e = itmIndex.GetByFileName(familyKey);
                if (e != null) return e;
            }
            string code = GetPartProductCode(part);
            if (!string.IsNullOrEmpty(code))
            {
                var e = itmIndex.GetByProductCode(code);
                if (e != null) return e;
            }
            return null;
        }

        /// <summary>
        /// Returns the .ITM file path the part was instantiated from.
        /// The API XML claims <c>FabricationPartType.ItemPath</c> exists
        /// since Revit 2019 but the public surface of <c>RevitAPI.dll</c>
        /// in 2026 doesn't expose it as a plain compile-time property,
        /// so we reach for it via several strategies in order:
        ///   1. <c>dynamic</c> dispatch (late-bound — handles COM-style
        ///      property bridging that <c>GetProperty</c> can't see).
        ///   2. Reflection on every level of the inheritance chain.
        ///   3. Common parameter aliases (<c>"Item Path"</c>,
        ///      <c>"Item File"</c>).
        /// Returns "" when none of the above produce a value.
        /// </summary>
        private static string GetPartItemPath(FabricationPart part)
        {
            try
            {
                var typeId = part.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    return "";
                var typeElem = part.Document.GetElement(typeId);
                if (typeElem == null) return "";

                // Strategy 1: dynamic dispatch.
                try
                {
                    dynamic dyn = typeElem;
                    string? p1 = (string?)dyn.ItemPath;
                    if (!string.IsNullOrWhiteSpace(p1)) return p1.Trim();
                }
                catch { }

                // Strategy 2: reflection across the inheritance chain.
                for (var t = typeElem.GetType(); t != null; t = t.BaseType)
                {
                    try
                    {
                        var prop = t.GetProperty("ItemPath",
                            BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (prop != null)
                        {
                            var v = prop.GetValue(typeElem)?.ToString();
                            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                        }
                    }
                    catch { }
                }

                // Strategy 3: parameter aliases.
                foreach (var name in new[] { "Item Path", "Item File", "ItemPath" })
                {
                    try
                    {
                        var p = typeElem.LookupParameter(name);
                        if (p != null && p.HasValue)
                        {
                            var v = p.AsString() ?? p.AsValueString();
                            if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Diagnostic trace for the per-part ITM install-table lookup.
        /// Returns a one-line summary describing exactly which step of
        /// the chain succeeded or failed. ALWAYS echoes the resolution
        /// source so the user can tell ItemPath / FamilyName / ProductCode
        /// apart in the UI. Returns "" when no diagnosis applies (pipe,
        /// no index, fabrication side).
        /// </summary>
        public static string DiagnoseInstallTableLookup(
            FabricationPart part, EtimesBreakpointParser parser,
            ItmFileIndex? itmIndex)
        {
            if (itmIndex == null)              return "no ITM index";
            if (part == null || parser == null) return "";
            if (part.Location is LocationCurve) return "";  // pipe — different path

            string itemPath  = GetPartItemPath(part);
            string familyKey = GetPartFamilyKey(part);
            string code      = GetPartProductCode(part);

            ItmFileParser.ItmEntry? entry = null;
            string source = "";
            if (!string.IsNullOrEmpty(itemPath))
            {
                entry = itmIndex.GetByItemPath(itemPath);
                if (entry != null) source = $"ItemPath '{Path.GetFileName(itemPath)}'";
            }
            if (entry == null && !string.IsNullOrEmpty(familyKey))
            {
                entry = itmIndex.GetByFileName(familyKey);
                if (entry != null) source = $"FamilyName '{familyKey}'";
            }
            if (entry == null && !string.IsNullOrEmpty(code))
            {
                entry = itmIndex.GetByProductCode(code);
                if (entry != null) source = $"ProductCode '{code}'";
            }
            if (entry == null)
                return $"no ITM entry — ItemPath='{itemPath}', " +
                       $"FamilyName='{familyKey}', code='{code}'";

            string? tableKey = entry.InstallTableKey;
            if (string.IsNullOrEmpty(tableKey))
            {
                var bracketStrs = new List<string>();
                foreach (var b in entry.PartBrackets)
                    bracketStrs.Add($"[{b.Group}]{b.Description}");
                return $"via {source}: no install bracket " +
                       $"(brackets: [{string.Join(", ", bracketStrs)}])";
            }

            var table = parser.GetTable(tableKey!);
            if (table == null)
                return $"via {source}: install key '{tableKey}' not in " +
                       $"etimes parser ({parser.TableCount} tables loaded)";

            double nominal = GetFittingLookupSize(part);
            if (nominal <= 0)
            {
                var sizes = GetConnectorSizesInches(part);
                foreach (var s in sizes) if (s > nominal) nominal = s;
            }
            if (nominal <= 0)
                return $"via {source}: key '{tableKey}' OK but part has no connector sizes";

            var m = table.GetMinutesForSize(nominal);
            if (!m.HasValue)
                return $"via {source}: key '{tableKey}' OK; no breakpoint covers " +
                       $"size {nominal}\" (table breakpoints: {table.Breakpoints.Count})";

            return $"OK via {source}: {tableKey} @ {nominal}\" = {m.Value:0.##} min";
        }

        /// <summary>
        /// Returns the part's family-name key used to look up its source
        /// .ITM by filename. Tries the "Family Name" parameter first
        /// (the typical match for stock Fab content), then falls back
        /// to the Revit type name. Returns "" when neither resolves.
        /// </summary>
        private static string GetPartFamilyKey(FabricationPart part)
        {
            try
            {
                var p = part.LookupParameter("Family Name");
                if (p != null && p.HasValue)
                {
                    var v = p.AsString() ?? p.AsValueString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
                }
            }
            catch { }
            try
            {
                var typeId = part.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var typeElem = part.Document.GetElement(typeId);
                    var n = typeElem?.Name;
                    if (!string.IsNullOrWhiteSpace(n)) return n!.Trim();
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Reads the part's Product Code (e.g. <c>"ADSK_30111286"</c>)
        /// from <see cref="BuiltInParameter.FABRICATION_PRODUCT_CODE"/>.
        /// Mirrors <c>CostBreakdownCommand.ReadProductCode</c> so both
        /// codepaths read the same value the UI displays. Returns ""
        /// when the parameter is missing or blank.
        /// </summary>
        private static string GetPartProductCode(FabricationPart part)
        {
            try
            {
                var bp = part.get_Parameter(BuiltInParameter.FABRICATION_PRODUCT_CODE);
                if (bp != null && bp.HasValue)
                {
                    var v = bp.AsString();
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            catch { }
            // Last-resort named-parameter lookup (custom configs).
            try
            {
                var p = part.LookupParameter("Product Code");
                if (p != null && p.HasValue)
                {
                    var v = p.AsString() ?? p.AsValueString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Per-connector lookup sizes (inches) for table breakpoint
        /// lookups. Shape-aware so duct fittings (rectangular) and
        /// transition fittings (two connectors at different sizes)
        /// each contribute their own true size:
        /// <list type="bullet">
        /// <item><b>Round / Oval</b> — diameter (Radius × 24, feet→inches).</item>
        /// <item><b>Rectangular</b> — half-periphery (Width + Height) × 12.
        /// Matches the "Half Periphery &lt;=" breakpoint axis duct
        /// labour tables use. Without this branch the rect connectors
        /// silently disappeared from the list (their <c>Radius</c> is
        /// 0), causing the connector-scoped loop to fall through to a
        /// single body-scoped lookup and under-count duct connector
        /// minutes by 2×.</item>
        /// </list>
        /// Different sizes at different positions are preserved in list
        /// order — a 24×12 → 16×8 transition contributes 36" + 24" so
        /// each connector's ancillaries get the right per-side minutes.
        /// </summary>
        public static List<double> GetConnectorSizesInches(FabricationPart part)
        {
            var list = new List<double>();
            try
            {
                var cm = part.ConnectorManager;
                if (cm == null) return list;
                foreach (Connector c in cm.Connectors)
                {
                    if (c == null) continue;
                    double size = 0;
                    try
                    {
                        if (c.Shape == ConnectorProfileType.Rectangular)
                        {
                            double w = c.Width  * 12.0;
                            double h = c.Height * 12.0;
                            if (w > 0 && h > 0) size = w + h;
                        }
                        else
                        {
                            double r = c.Radius;
                            if (r > 0) size = r * 24.0;
                        }
                    }
                    catch { }
                    if (size > 0) list.Add(size);
                }
            }
            catch { }
            return list;
        }

        public static bool IsConnectorScoped(FabricationAncillaryUsage anc)
        {
            try
            {
                return string.Equals(anc.UsageType.ToString(), "Connector",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ── Pipes ──────────────────────────────────────────────────────────

        /// <summary>
        /// Pipe labour computation. Pipes don't have ancillaries — the
        /// labour table is referenced directly via the pipe's attributes.
        /// Installation table varies per material:
        ///   "Copper Type L"   → etimes "Copper - Type L"   (fuzzy match on PMD alone)
        ///   "Carbon Steel"    → etimes "Carbon Steel - Sch40 - Plain End"
        ///                       (needs PMD + schedule + install type combined)
        /// Fabrication table is constant across pipe materials in the
        /// configs we've seen: "Tube End - Cut Threads".
        ///
        /// Verified math (2026-05-15 Imperial 4.02 config):
        ///   5' 2" Copper Type L  → 5.4 min/ft × 5 × $30/hr/60   = $13.50 install
        ///   1' 4" Carbon Steel   → 26.4 min/ft × 1 × $30/hr/60  = $13.20 install
        ///                          0.066 hr/ft × 1 × $30/hr     = $1.98 fab
        /// </summary>
        private static LabourMinutesResult ComputePipeMinutesByTable(
            FabricationPart part, EtimesBreakpointParser parser,
            bool isFabrication, ItmFileIndex? itmIndex)
        {
            var result = new LabourMinutesResult();

            if (!(part.Location is LocationCurve lc) || lc.Curve == null)
                return result;
            double curveLengthFt = lc.Curve.Length;
            if (curveLengthFt <= 0) return result;

            // ── Resolve the labour table ──
            // PRIMARY: read the .ITM's install/fab bracket-string. Works
            // for pipes AND ducts (the .ITM file format is the same).
            // FALLBACK: pipe fuzzy match for install ({PMD}{schedule}
            // {installType}) + hardcoded "Tube End - Cut Threads" for fab.
            // Fuzzy fallback only fires for parts where the ITM lookup
            // returned no usable key (custom families, etc.).
            EtimesBreakpointParser.TableEntry? table = null;
            string source = "";
            if (itmIndex != null)
            {
                var entry = ResolveItmEntry(part, itmIndex);
                string? key = isFabrication
                    ? entry?.FabricationTableKey
                    : entry?.InstallTableKey;
                if (!string.IsNullOrEmpty(key))
                {
                    table  = parser.GetTable(key!);
                    source = "ITM";
                }
            }
            if (table == null)
            {
                table = isFabrication
                    ? GetPipeFabricationTable(parser)
                    : FuzzyMatchPipeInstallTable(part, parser);
                if (table != null) source = "fuzzy";
            }
            if (table == null) return result;

            // ── Resolve the lookup size ──
            // Rectangular ducts use half-periphery = Width + Height
            // (matches Fab's "Half Periphery <=" breakpoint axis).
            // Pipes + round ducts use connector diameter.
            double lookupSize = GetCurveLookupSize(part);
            if (lookupSize <= 0) return result;

            var minutes = table.GetMinutesForSize(lookupSize);
            if (!minutes.HasValue || minutes.Value <= 0) return result;

            // ── Length scaling ──
            // PIPE FAB ([Mechanical Tube] "Tube End - Cut Threads") is
            // per-pipe (joint/setup time, independent of length). Verified:
            //   Carbon Steel 1' fab: 0.066 hr × 60 = 3.96 min × $30/60 = $1.98 ✓
            // Everything else is per-foot:
            //   Pipe install   (Copper Type L 5' × 2"): 5.4 min/ft × 5 = 27 min → $13.50 ✓
            //   Duct fab/inst  (24x12 × 4.92'):         0.28+0.339 hr/ft × 4.92 → $106.48 (target)
            bool perPipe = isFabrication
                        && !string.IsNullOrEmpty(table.Category)
                        && table.Category.Equals("Mechanical Tube",
                               StringComparison.OrdinalIgnoreCase);
            double totalMinutes = perPipe
                ? minutes.Value                     // per-pipe — no length scale
                : minutes.Value * curveLengthFt;    // per-foot × length

            _ = source;  // reserved for future diagnostic surfacing
            // Pipe / curve-based-duct labour table is the regular section
            // rate. No connector-fab micro-rate involved for the curve
            // path — that bucket stays empty.
            result.Regular[table.Name] = totalMinutes;
            return result;
        }

        /// <summary>
        /// Returns the breakpoint-axis size (inches) for a fitting
        /// (LocationPoint part). Iterates ALL connectors and returns
        /// the maximum of:
        ///   • Rectangular → half-periphery (W + H, in inches)
        ///   • Round / Oval → diameter (Radius × 24, feet→inches)
        /// MAX is the right choice for joint sizing — a transition
        /// fitting's joint table reads off the larger end (matches Fab
        /// UI). Returns 0 when no usable connector is found.
        /// </summary>
        public static double GetFittingLookupSize(FabricationPart part)
        {
            double max = 0;
            try
            {
                var cm = part.ConnectorManager;
                if (cm == null) return 0;
                foreach (Connector c in cm.Connectors)
                {
                    if (c == null) continue;
                    double size = 0;
                    try
                    {
                        if (c.Shape == ConnectorProfileType.Rectangular)
                        {
                            double w = c.Width  * 12.0;
                            double h = c.Height * 12.0;
                            size = w + h;
                        }
                        else
                        {
                            double r = c.Radius;
                            if (r > 0) size = r * 24.0;
                        }
                    }
                    catch { }
                    if (size > max) max = size;
                }
            }
            catch { }
            return max;
        }

        /// <summary>
        /// Returns the breakpoint-axis size (inches) for a curve-based
        /// part. Rectangular connectors yield half-periphery (W + H);
        /// round connectors yield diameter (radius × 2 × 12). Returns 0
        /// when no usable connector is found.
        /// </summary>
        private static double GetCurveLookupSize(FabricationPart part)
        {
            try
            {
                var cm = part.ConnectorManager;
                if (cm == null) return 0;
                foreach (Connector c in cm.Connectors)
                {
                    if (c == null) continue;
                    try
                    {
                        if (c.Shape == ConnectorProfileType.Rectangular)
                        {
                            // Width/Height come in Revit-internal feet.
                            double w = c.Width  * 12.0;
                            double h = c.Height * 12.0;
                            double halfPeriphery = w + h;
                            if (halfPeriphery > 0) return halfPeriphery;
                        }
                        else
                        {
                            // Round (pipe/round duct) and Oval fall back to
                            // diameter. Oval probably needs a future-tuned
                            // formula but diameter is at least non-zero.
                            double r = c.Radius;
                            if (r > 0) return r * 24.0;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Pipe fabrication: hardcoded "Tube End - Cut Threads" lookup.
        /// In the Imperial 4.02 config we've seen, every pipe (copper,
        /// carbon steel, …) uses this single ftimes table; values vary by
        /// size only. If a future fab config uses a different name, this
        /// could become a Pricing Source dropdown — for now a hardcode
        /// is the right complexity/value trade-off.
        /// </summary>
        private static EtimesBreakpointParser.TableEntry? GetPipeFabricationTable(
            EtimesBreakpointParser parser)
        {
            return parser.GetTable("Tube End - Cut Threads");
        }

        /// <summary>
        /// Pipe installation: try a chain of fuzzy-match candidates built
        /// from the pipe's attributes. First candidate that normalizes to
        /// an existing table name wins.
        ///
        /// Candidate order (most specific to least):
        ///   1. {PMD} {schedule} {installType}   "Carbon Steel Sch40 Plain End"
        ///   2. {PMD} {installType}              "Carbon Steel Plain End"
        ///   3. {PMD} {schedule}                 "Carbon Steel Sch40"
        ///   4. {PMD}                            "Copper Type L"
        ///
        /// Normalization strips all non-alphanumeric chars + lowercases on
        /// both sides, so "Carbon Steel Sch40 Plain End" ↔ "Carbon Steel
        /// - Sch40 - Plain End" both become "carbonsteelsch40plainend".
        /// </summary>
        private static EtimesBreakpointParser.TableEntry? FuzzyMatchPipeInstallTable(
            FabricationPart part, EtimesBreakpointParser parser)
        {
            string pmd         = GetProductMaterialDescription(part);
            string installType = GetInstallType(part);
            string schedule    = ExtractScheduleFromPart(part);

            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(pmd))
            {
                if (!string.IsNullOrEmpty(schedule) && !string.IsNullOrEmpty(installType))
                    candidates.Add($"{pmd} {schedule} {installType}");
                if (!string.IsNullOrEmpty(installType))
                    candidates.Add($"{pmd} {installType}");
                if (!string.IsNullOrEmpty(schedule))
                    candidates.Add($"{pmd} {schedule}");
                candidates.Add(pmd);
            }

            foreach (var c in candidates)
            {
                var t = FuzzyMatchTable(c, parser);
                if (t != null) return t;
            }
            return null;
        }

        private static string GetInstallType(FabricationPart part)
        {
            try
            {
                var p = part.LookupParameter("Install Type");
                if (p != null && p.HasValue)
                {
                    var v = p.AsString() ?? p.AsValueString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Extracts "Sch40" / "Sch80" / etc. from the pipe's family/type
        /// name via regex. Carbon steel & similar materials encode the
        /// schedule in the family name (e.g.
        /// "Pipe-A53-CS-(A)-SCH40-BLK(PE)") because it's not exposed via
        /// an API parameter we can reach.
        /// </summary>
        private static string ExtractScheduleFromPart(FabricationPart part)
        {
            string familyName = "";
            try
            {
                var p = part.LookupParameter("Family and Type");
                if (p != null && p.HasValue)
                    familyName = p.AsValueString() ?? "";
            }
            catch { }
            if (string.IsNullOrEmpty(familyName))
            {
                try
                {
                    var typeId = part.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                    {
                        var typeElem = part.Document.GetElement(typeId);
                        familyName = typeElem?.Name ?? "";
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(familyName)) return "";

            var match = Regex.Match(familyName, @"SCH(\d+)", RegexOptions.IgnoreCase);
            return match.Success ? "Sch" + match.Groups[1].Value : "";
        }

        private static string GetProductMaterialDescription(FabricationPart part)
        {
            // Reflected property access — Revit 2026 has this on the public
            // surface; reflection avoids hard-binding to an API version
            // that might be missing it.
            try
            {
                var prop = part.GetType().GetProperty("ProductMaterialDescription",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var v = prop.GetValue(part)?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            catch { }

            // Named parameter fallback.
            try
            {
                var p = part.LookupParameter("Product Material Description");
                if (p != null && p.HasValue)
                {
                    var v = p.AsString() ?? p.AsValueString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
                }
            }
            catch { }

            return "";
        }

        /// <summary>
        /// Tries an exact match first (case-insensitive — parser handles
        /// that), then a normalized match where both sides have all
        /// non-alphanumeric characters stripped and are lowercased. That
        /// bridges "Copper Type L" ↔ "Copper - Type L" without per-config
        /// hard-coding.
        /// </summary>
        private static EtimesBreakpointParser.TableEntry? FuzzyMatchTable(
            string lookup, EtimesBreakpointParser parser)
        {
            var exact = parser.GetTable(lookup);
            if (exact != null) return exact;

            string normLookup = NormalizeForMatch(lookup);
            if (string.IsNullOrEmpty(normLookup)) return null;

            foreach (var kv in parser.Tables)
            {
                if (NormalizeForMatch(kv.Key) == normLookup)
                    return kv.Value;
            }
            return null;
        }

        private static string NormalizeForMatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
