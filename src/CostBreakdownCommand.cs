using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;
using EstimatingTools.Revit;
using EstimatingTools.UI;

namespace EstimatingTools
{
    /// <summary>
    /// Per-part cost forensics — mirrors the "Cost Breakdown" view in
    /// Autodesk Fabrication ESTmep. Launches a modeless WPF window with
    /// a collapsible tree of Material / Fabrication / Installation costs
    /// for each selected FabricationPart, plus a Save Report option that
    /// writes a hierarchical TXT file.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CostBreakdownCommand : IExternalCommand
    {
        private static CostBreakdownDialog? _instance;

        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            // If the dialog's already open, just bring it forward and let
            // the user use the Refresh / Pick buttons.
            if (_instance != null)
            {
                _instance.Activate();
                return Result.Succeeded;
            }

            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Cost Breakdown", "No active document.");
                return Result.Cancelled;
            }
            var doc = uiDoc.Document;

            var info = PricingSourceSchema.Read(doc);
            if (!info.IsConfigured)
            {
                TaskDialog.Show("Cost Breakdown",
                    "Configure Pricing Setup first.");
                return Result.Cancelled;
            }

            var ctx = BuildContext(doc, info, out string? err);
            if (err != null)
            {
                TaskDialog.Show("Cost Breakdown", err);
                return Result.Failed;
            }

            // Seed from the current Revit selection. Accepts both
            // FabricationParts (the original surface) and native RFA
            // pipe-accessory FamilyInstances (new — Product Code based
            // pricing chains through CostBreakdownCommand.ComputeBreakdownForRfa).
            var initialParts = uiDoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e is FabricationPart ||
                            (e is FamilyInstance fi
                             && fi.Category?.Id?.Value ==
                                (long)BuiltInCategory.OST_PipeAccessory))
                .ToList();

            var dlg = new CostBreakdownDialog(uiDoc, ctx!, initialParts);
            _instance = dlg;
            dlg.Closed += (_, _) => _instance = null;
            dlg.Show();
            return Result.Succeeded;
        }

        // ── Context loading ─────────────────────────────────────────────────

        internal static CostBreakdownContext? BuildContext(Document doc,
            Models.PricingSourceInfo info, out string? error)
        {
            error = null;
            var ctx = new CostBreakdownContext();
            if (!info.UseFabDatabase)
            {
                error = "Pricing Setup must have Fab Database enabled to break down costs.";
                return null;
            }

            try
            {
                // V6: labour rates come from Pricing Setup (user-entered),
                // not cost.map. Parse the etimes/ftimes binaries directly
                // when both a labour name AND a rate are set; otherwise
                // that side is skipped (no labour cost shown).
                if (!string.IsNullOrWhiteSpace(info.ErectionLabourTypeName) &&
                    info.ErectionRatePerHour > 0)
                {
                    ctx.InstallRate       = info.ErectionRatePerHour;
                    ctx.InstallLabourName = info.ErectionLabourTypeName;
                    string p = Path.Combine(info.DatabaseFolder, "etimes.map");
                    if (File.Exists(p)) ctx.Etimes = new EtimesBreakpointParser(p);
                }
                if (!string.IsNullOrWhiteSpace(info.FabricationLabourTypeName) &&
                    info.FabricationRatePerHour > 0)
                {
                    ctx.FabRate       = info.FabricationRatePerHour;
                    ctx.FabLabourName = info.FabricationLabourTypeName;
                    string p = Path.Combine(info.DatabaseFolder, "ftimes.map");
                    if (File.Exists(p)) ctx.Ftimes = new EtimesBreakpointParser(p);
                }

                string supplierPath = Path.Combine(info.DatabaseFolder, "supplier.map");
                if (File.Exists(supplierPath))
                {
                    ctx.Supplier    = new SupplierMapReader(supplierPath);
                    ctx.SupplierSrc = new SupplierMapPricingSource(
                        info.DatabaseFolder, ctx.Etimes);
                    ctx.SupplierLabel = $"Fab Database (supplier.map) — {Path.GetFileName(info.DatabaseFolder)}";
                }

                // Material.MAP — gauge → $/lb + lb/sqft for sheet-metal
                // ducts. Best-effort; on failure ducts fall back to $0
                // base price (current behavior).
                string materialPath = Path.Combine(info.DatabaseFolder, "Material.map");
                if (File.Exists(materialPath))
                {
                    try { ctx.MaterialMap = new MaterialMapReader(materialPath); }
                    catch { /* swallow — material-side is optional */ }
                }

                ctx.IncludeLooseAncillaries = info.IncludeLooseAncillaries;

                // Build the .ITM index so the install-table contribution
                // (per-part labour the runtime API doesn't expose) can be
                // resolved for fittings. Prefer the LOADED-files fast
                // path: Fab tells us which ITMs the project's services
                // actually use (~tens to ~hundreds), instead of walking
                // the 15k+ files under the Items root.
                if (ctx.Etimes != null)
                {
                    string? itemsRoot = ItmFileIndex.TryLocateItemsRoot(info.DatabaseFolder);
                    if (string.IsNullOrEmpty(itemsRoot))
                    {
                        ctx.ItmStatus = $"ITM index: Items folder not found relative to {info.DatabaseFolder}";
                    }
                    else
                    {
                        try
                        {
                            var loadedPaths = ItmFileIndex.GetLoadedItmPaths(doc);
                            string mode = loadedPaths.Count > 0
                                ? $"loaded-services ({loadedPaths.Count:N0} ITMs)"
                                : $"full-tree fallback [{ItmFileIndex.LastLoadedQueryNote}]";
                            ctx.Itm = new ItmFileIndex(itemsRoot!,
                                ItmFileIndex.DefaultSkipFolders,
                                loadedPaths.Count > 0 ? loadedPaths : null);
                            ctx.ItmStatus = $"ITM index: {ctx.Itm.EntryCount:N0} parts " +
                                $"from {mode}, " +
                                $"{ctx.Itm.FilesScanned:N0} files scanned, " +
                                $"{ctx.Itm.FilesFailed:N0} failed " +
                                $"in {ctx.Itm.BuildDuration.TotalSeconds:0.0}s";
                        }
                        catch (Exception ex)
                        {
                            ctx.ItmStatus = $"ITM index: build failed — {ex.Message}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"Could not load Fab DB:\n{ex.Message}";
                return null;
            }

            return ctx;
        }

        // ── Per-part calculation (single source of truth) ───────────────────

        /// <summary>
        /// Computes a fully-resolved PartBreakdown for a single part. Both
        /// the dialog's TreeView builder and the TXT report consume this
        /// — keeps the two views guaranteed-consistent.
        /// </summary>
        internal static PartBreakdown ComputeBreakdown(FabricationPart part,
                                                       CostBreakdownContext ctx)
        {
            // IsDuct is domain-based (HVAC), not geometry-based: a duct
            // fitting (LocationPoint) is still a duct. LengthFt > 0
            // discriminates "duct run" (curve-based, charged per-ft)
            // from "duct fitting" (per-piece). IsPipe stays geometry-
            // gated since pipes are always curve-based.
            bool isCurveBased = part.Location is LocationCurve;
            bool isDuct = IsDuctDomain(part);
            var br = new PartBreakdown
            {
                Part             = part,
                TypeName         = ReadTypeName(part),
                ShortDescription = ReadParam(part, "Product Short Description"),
                SizeDescription  = ReadParam(part, "Product Size Description"),
                FamilyName       = ReadParam(part, "Family Name"),
                ProductCode      = ReadProductCode(part),
                ElementId        = part.Id.ToString(),
                IsPipe           = isCurveBased && !isDuct,
                IsDuct           = isDuct,
                SizeIn           = isDuct
                    ? AncillaryLabourBreakdown.GetFittingLookupSize(part)
                    : GetPrimarySizeInches(part),
                SizeLabel        = isDuct ? GetDuctSizeLabel(part) : "",
            };
            if (isCurveBased && part.Location is LocationCurve lc && lc.Curve != null)
                br.LengthFt = lc.Curve.Length;

            // ── Material ──
            double basePerUnit = 0;
            double ancTotal    = 0;
            if (ctx.SupplierSrc != null)
            {
                var bd = ctx.SupplierSrc.LookupBreakdown(part);
                basePerUnit = bd.BaseListPrice ?? bd.ItmCost ?? 0;
                ancTotal    = bd.AncillaryTotal;
            }

            // Sheet-metal duct override: ducts don't have a per-piece
            // supplier.map price (M-Rate = None in Fab UI). The cost
            // comes from `Weight × $/lb`, where $/lb is keyed by
            // (Material, Gauge) in Material.MAP.
            //   • Curve-based duct (LengthFt > 0) → $/ft, displayed
            //     and extended by length.
            //   • Duct fitting (LengthFt == 0) → per-piece total, used
            //     directly as BaseListPerUnit (no length scaling).
            if (isDuct && basePerUnit <= 0 && ctx.MaterialMap != null)
            {
                if (br.LengthFt > 0)
                {
                    double perFt = ComputeDuctMaterialPerFt(part, ctx.MaterialMap, br.LengthFt);
                    if (perFt > 0) basePerUnit = perFt;
                }
                else
                {
                    double perPiece = ComputeDuctMaterialPerPiece(part, ctx.MaterialMap);
                    if (perPiece > 0) basePerUnit = perPiece;
                }
            }

            br.BaseListPerUnit = basePerUnit;
            br.AncillaryTotal  = ancTotal;

            // Per-ancillary detail (fittings only; pipes don't carry these).
            // Note: pipes go straight to the SumAncillaryCosts-derived
            // AncillaryTotal from above. Fittings (including duct
            // fittings) take the per-line walk below, then we OVERRIDE
            // AncillaryTotal with the displayed-rows sum at the bottom
            // so the total always matches what the user sees (Loose
            // toggle, kit flattening, joint-wrapper filter all consistent).
            if (!br.IsPipe && ctx.Supplier != null)
            {
                try
                {
                    FabricationConfiguration? cfg = null;
                    try { cfg = FabricationConfiguration.GetFabricationConfiguration(part.Document); } catch { }
                    var usages = part.GetPartAncillaryUsage();
                    if (usages != null)
                    {
                        // Walk in file order. When we hit a KIT (via
                        // FabricationConfiguration.IsAncillaryKit), it
                        // becomes the parent for every subsequent fixing
                        // line until the next kit. Mirrors Fab UI's:
                        //   Kit (Flange Kit - 150# (ASME B16.5)) = $2.50
                        //     ├ Bolt × 8
                        //     ├ Nut × 16
                        //     └ Gasket
                        AncillaryLine? currentKit = null;
                        foreach (var anc in usages)
                        {
                            // Kit container? IsAncillaryKit is the
                            // authoritative API signal. The kit itself
                            // doesn't charge — its children's sum is the
                            // displayed total.
                            bool isKit = false;
                            if (cfg != null)
                            {
                                try { isKit = cfg.IsAncillaryKit(anc.AncillaryId); }
                                catch { }
                            }
                            if (isKit)
                            {
                                string kitTitle = "";
                                if (cfg != null)
                                {
                                    try { kitTitle = cfg.GetAncillaryGroupName(anc.AncillaryId) ?? ""; }
                                    catch { }
                                    if (string.IsNullOrEmpty(kitTitle))
                                    {
                                        try { kitTitle = cfg.GetAncillaryName(anc.AncillaryId) ?? ""; }
                                        catch { }
                                    }
                                }
                                if (string.IsNullOrEmpty(kitTitle))
                                {
                                    try { kitTitle = anc.ProductCode ?? "Kit"; } catch { kitTitle = "Kit"; }
                                }
                                currentKit = new AncillaryLine
                                {
                                    Name        = kitTitle,
                                    IsKitHeader = true,
                                };
                                br.AncillaryDetail.Add(currentKit);
                                continue;
                            }

                            // Joint-wrapper filter (etimes-table-name
                            // match) — catches install-table refs that
                            // aren't strict kits but also shouldn't be
                            // charged (e.g. "Flange 150" tied to the
                            // [Mechanical Flanges] etimes table).
                            if (ctx.SupplierSrc?.IsJointWrapper(cfg, anc) == true)
                                continue;

                            // Loose-ancillary filter — Fab ESTmep's Cost
                            // Breakdown spec-tracks UsageType=Loose
                            // ancillaries (body-level sealants like the
                            // SMACNA Class A duct sealant) but doesn't
                            // bill them as separate material lines. The
                            // user can opt in via Pricing Source if
                            // their project bills these as line items.
                            if (!ctx.IncludeLooseAncillaries &&
                                IsLooseAncillary(anc))
                                continue;

                            string ancCode = "";
                            try { ancCode = anc.ProductCode ?? ""; } catch { }
                            if (string.IsNullOrEmpty(ancCode)) continue;

                            var unit = ctx.Supplier.Lookup(ancCode);
                            if (!unit.HasValue || unit.Value <= 0) continue;

                            double q = 0;
                            try { q = anc.Quantity; } catch { }
                            if (q <= 0) q = 1;

                            string name = "";
                            if (cfg != null)
                            {
                                try { name = cfg.GetAncillaryName(anc.AncillaryId) ?? ""; } catch { }
                            }
                            if (string.IsNullOrEmpty(name)) name = ancCode;

                            // Category prefix for ancillaries with a
                            // semantically meaningful Type enum (Sealant,
                            // Gasket, Clip, TieRod, etc.). Skipped for
                            // generic / structural types (AncillaryMaterial,
                            // Fixing, Unknown) where it would add noise.
                            // Yields lines like "Sealant: Class A" /
                            // "Gasket: FL Gasket".
                            string typePrefix = GetAncillaryTypePrefix(anc);
                            string displayName = string.IsNullOrEmpty(typePrefix)
                                ? name
                                : $"{typePrefix}: {name}";

                            var line = new AncillaryLine
                            {
                                Name      = displayName,
                                UnitPrice = unit.Value,
                                Quantity  = q,
                            };
                            if (currentKit != null)
                                currentKit.Children.Add(line);
                            else
                                br.AncillaryDetail.Add(line);
                        }
                    }
                }
                catch { }

                // Re-derive AncillaryTotal from the displayed tree so the
                // total always matches the visible rows. SumAncillaryCosts
                // (used as the initial seed) doesn't know about the Loose
                // toggle, kit-header flattening, or the supplier-price>0
                // filter that the display walk applies — leaving the two
                // out of sync causes "hidden" cents in the total. Walking
                // AncillaryDetail here is the single source of truth.
                br.AncillaryTotal = br.AncillaryDetail.Sum(a => a.DisplayTotal);
            }

            // ── Installation labour ──
            // Install side has no connector-fab micro-rate buckets (only
            // fabrication side does), so ConnectorMicroRate is always
            // empty here — Regular contains everything.
            if (ctx.Etimes != null && ctx.InstallRate > 0)
            {
                var perTable = AncillaryLabourBreakdown.ComputeMinutesByTable(
                    part, ctx.Etimes, isFabrication: false, ctx.Itm);
                foreach (var kv in perTable.Regular.OrderByDescending(p => p.Value))
                {
                    br.Installation.Add(new LabourTableLine
                    {
                        TableName    = kv.Key,
                        TotalMinutes = kv.Value,
                        Rate         = ctx.InstallRate,
                    });
                }
            }
            br.InstallRate       = ctx.InstallRate;
            br.InstallLabourName = ctx.InstallLabourName;

            // ── Fabrication labour ──
            // Two rate streams:
            //   Regular            → section rate ($30/hr Skilled etc.)
            //   ConnectorMicroRate → Fab UI's internal $1/hr connector-
            //                        fabrication micro-rate (the
            //                        "Standing S&D … @ 1.00 $/(hrs)"
            //                        rows in ESTmep's Cost Breakdown).
            if (ctx.Ftimes != null && ctx.FabRate > 0)
            {
                var perTable = AncillaryLabourBreakdown.ComputeMinutesByTable(
                    part, ctx.Ftimes, isFabrication: true, ctx.Itm);
                foreach (var kv in perTable.Regular.OrderByDescending(p => p.Value))
                {
                    br.Fabrication.Add(new LabourTableLine
                    {
                        TableName    = kv.Key,
                        TotalMinutes = kv.Value,
                        Rate         = ctx.FabRate,
                    });
                }
                foreach (var kv in perTable.ConnectorMicroRate.OrderByDescending(p => p.Value))
                {
                    br.Fabrication.Add(new LabourTableLine
                    {
                        TableName    = kv.Key,
                        TotalMinutes = kv.Value,
                        Rate         = AncillaryLabourBreakdown.ConnectorFabMicroRate,
                    });
                }
            }
            br.FabRate       = ctx.FabRate;
            br.FabLabourName = ctx.FabLabourName;

            return br;
        }

        /// <summary>
        /// Per-part breakdown for a native RFA FamilyInstance carrying a
        /// "Product Code" parameter. Same PartBreakdown shape so the
        /// dialog + report can render it identically to a
        /// FabricationPart. Material cost = supplier.map base +
        /// ITM-declared ancillary kit sum (each fixing as its own
        /// AncillaryLine for the drill-down view). Labour is empty —
        /// labour-table lookup for native content isn't wired yet.
        /// </summary>
        internal static PartBreakdown ComputeBreakdownForRfa(FamilyInstance fi,
                                                             CostBreakdownContext ctx)
        {
            string code = ReadProductCodeFromRfa(fi);
            string famName = "";
            string typeName = "";
            try
            {
                var t = fi.Document.GetElement(fi.GetTypeId()) as ElementType;
                if (t != null)
                {
                    typeName = t.Name ?? "";
                    famName  = t.FamilyName ?? "";
                }
            }
            catch { }

            var br = new PartBreakdown
            {
                Part             = fi,
                TypeName         = typeName,
                ShortDescription = "",
                SizeDescription  = "",
                FamilyName       = famName,
                ProductCode      = code,
                ElementId        = fi.Id.ToString(),
                IsPipe           = false,
                IsDuct           = false,
                SizeIn           = 0,
                SizeLabel        = "",
                LengthFt         = 0,
                InstallRate      = ctx.InstallRate,
                InstallLabourName= ctx.InstallLabourName,
                FabRate          = ctx.FabRate,
                FabLabourName    = ctx.FabLabourName,
            };
            if (string.IsNullOrEmpty(code)) return br;   // empty header row

            // Base material price via supplier.map. Skip if no entry —
            // the RFA falls through with $0 material; user sees a row
            // with the code + ElementId so they know it was found.
            double basePiece = 0;
            if (ctx.Supplier != null)
            {
                var p = ctx.Supplier.Lookup(code);
                if (p.HasValue && p.Value > 0) basePiece = p.Value;
            }
            br.BaseListPerUnit = basePiece;

            // ITM ancillary kit — each code becomes its own
            // AncillaryLine row so the drill-down view shows the
            // breakdown (matches the per-line walk FabricationParts get
            // via SumAncillaryCosts). All quantities are 1 EA — the
            // ITM declares the kit without per-fitting multipliers.
            double ancTotal = 0;
            if (ctx.Itm != null && ctx.Supplier != null)
            {
                var entry = ctx.Itm.GetByProductCode(code);
                if (entry != null)
                {
                    foreach (var ancCode in entry.AncillaryProductCodes)
                    {
                        var p = ctx.Supplier.Lookup(ancCode);
                        if (!p.HasValue || p.Value <= 0) continue;
                        br.AncillaryDetail.Add(new AncillaryLine
                        {
                            Name      = ancCode,
                            Quantity  = 1,
                            UnitPrice = p.Value,
                        });
                        ancTotal += p.Value;
                    }
                }
            }
            br.AncillaryTotal = ancTotal;
            return br;
        }

        /// <summary>
        /// Mirrors MaterialEstimateCommand.ReadProductCodeFromRfa —
        /// Type parameter first then instance fallback, with a short
        /// alias list. Local copy avoids a circular dependency.
        /// </summary>
        private static string ReadProductCodeFromRfa(FamilyInstance fi)
        {
            string[] names = { "Product Code", "ProductCode", "ADSK Code" };
            try
            {
                var t = fi.Document.GetElement(fi.GetTypeId()) as ElementType;
                if (t != null)
                {
                    foreach (var n in names)
                    {
                        var p = t.LookupParameter(n);
                        if (p == null) continue;
                        string v = p.AsString() ?? p.AsValueString() ?? "";
                        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                    }
                }
            }
            catch { }
            foreach (var n in names)
            {
                try
                {
                    var p = fi.LookupParameter(n);
                    if (p == null) continue;
                    string v = p.AsString() ?? p.AsValueString() ?? "";
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
                catch { }
            }
            return "";
        }

        // ── TXT report writer (Save Report button) ──────────────────────────

        /// <summary>
        /// Writes the full hierarchical TXT report including the tool name +
        /// timestamp header. Same content the standalone command used to
        /// produce — preserved for "Save Report" exports.
        /// </summary>
        internal static string WriteTextReport(List<Element> parts,
                                               CostBreakdownContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine(new string('═', 72));
            sb.AppendLine($"  EstimatingTools — Cost Breakdown");
            sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  Parts in this report: {parts.Count}");
            sb.AppendLine($"  Pricing source: {ctx.SupplierLabel}");
            if (ctx.InstallRate > 0)
                sb.AppendLine($"  Installation labor: {ctx.InstallLabourName} @ ${ctx.InstallRate:0.##}/hr");
            if (ctx.FabRate > 0)
                sb.AppendLine($"  Fabrication labor: {ctx.FabLabourName} @ ${ctx.FabRate:0.##}/hr");
            sb.AppendLine(new string('═', 72));
            sb.AppendLine();

            double grandMat = 0, grandInst = 0, grandFab = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                PartBreakdown br;
                if (parts[i] is FabricationPart fp)
                    br = ComputeBreakdown(fp, ctx);
                else if (parts[i] is FamilyInstance fi)
                    br = ComputeBreakdownForRfa(fi, ctx);
                else
                    continue;
                WriteOnePart(sb, br, i + 1, parts.Count);
                grandMat  += br.MaterialExtended;
                grandInst += br.InstallTotal;
                grandFab  += br.FabTotal;
                sb.AppendLine();
            }

            if (parts.Count > 1)
            {
                double grand = grandMat + grandInst + grandFab;
                sb.AppendLine(new string('═', 72));
                sb.AppendLine($"  PROJECT TOTALS ({parts.Count} parts)");
                sb.AppendLine(new string('═', 72));
                sb.AppendLine($"    Material:              ${grandMat,12:N2}");
                sb.AppendLine($"    Installation labor:    ${grandInst,12:N2}");
                sb.AppendLine($"    Fabrication labor:     ${grandFab,12:N2}");
                sb.AppendLine($"    ────────────────────────────────────");
                sb.AppendLine($"    GRAND TOTAL:           ${grand,12:N2}");
                sb.AppendLine(new string('═', 72));
            }

            return sb.ToString();
        }

        private static void WriteOnePart(StringBuilder sb, PartBreakdown br,
                                         int index, int total)
        {
            sb.AppendLine(new string('─', 72));
            sb.AppendLine($"  Item {index} of {total}: {br.HeaderLabel}");
            sb.AppendLine(new string('─', 72));
            sb.Append("    Product Code:  ").AppendLine(br.ProductCode);
            sb.Append("    ElementId:     ").AppendLine(br.ElementId);
            if (br.IsPipe)
            {
                sb.AppendLine("    Geometry:      Pipe");
                sb.AppendLine($"    Size:          {br.SizeIn:0.##}\"");
                sb.AppendLine($"    Length:        {br.LengthFt:0.##} ft");
            }
            else if (br.IsDuct && br.LengthFt > 0)
            {
                sb.AppendLine("    Geometry:      Duct");
                sb.AppendLine($"    Size:          {(string.IsNullOrEmpty(br.SizeLabel) ? $"{br.SizeIn:0.##}\"" : br.SizeLabel)}");
                sb.AppendLine($"    Length:        {br.LengthFt:0.##} ft");
            }
            else if (br.IsDuct)
            {
                sb.AppendLine("    Geometry:      Duct fitting");
                sb.AppendLine($"    Size:          {(string.IsNullOrEmpty(br.SizeLabel) ? $"{br.SizeIn:0.##}\"" : br.SizeLabel)}");
            }
            else
            {
                sb.AppendLine("    Geometry:      Fitting / valve / hanger");
                sb.AppendLine($"    Size:          {br.SizeIn:0.##}\"");
            }
            sb.AppendLine();

            // Material — curve-based pipes/ducts (LengthFt > 0): base
            // scales by length, ancillaries are per-piece. Fittings,
            // valves, hangers, and duct fittings: everything per-piece.
            sb.AppendLine("    MATERIAL COSTS:");
            if (br.LengthFt > 0)
            {
                double baseExt = br.BaseListPerUnit * br.LengthFt;
                sb.AppendLine($"      Base price: ${br.BaseListPerUnit,10:N4}/ft × {br.LengthFt,5:0.##} ft = ${baseExt,12:N2}");
                sb.AppendLine($"      Ancillary kit:                              ${br.AncillaryTotal,12:N2}");
                foreach (var a in br.AncillaryDetail)
                    WriteAncillaryLineTxt(sb, a, indent: "        ");
                sb.AppendLine($"      ──────────────────────────────────────────");
                sb.AppendLine($"      Material subtotal:                          ${br.MaterialExtended,12:N2}");
            }
            else
            {
                sb.AppendLine($"      Base price:                                 ${br.BaseListPerUnit,12:N2}");
                sb.AppendLine($"      Ancillary kit:                              ${br.AncillaryTotal,12:N2}");
                foreach (var a in br.AncillaryDetail)
                    WriteAncillaryLineTxt(sb, a, indent: "        ");
                sb.AppendLine($"      ──────────────────────────────────────────");
                sb.AppendLine($"      Material subtotal:                          ${br.MaterialExtended,12:N2}");
            }
            sb.AppendLine();

            // Installation labour. "Per-foot" rendering applies whenever
            // the part has positive length (curve-based pipe OR duct).
            bool perFootDisplay = br.LengthFt > 0;
            WriteLabourTxt(sb, "INSTALLATION LABOR", br.InstallLabourName,
                br.InstallRate, br.Installation, perFootDisplay, br.LengthFt,
                isFabrication: false, isPipeFab: br.IsPipe,
                sectionTotal: br.InstallTotal);
            sb.AppendLine();

            // Fabrication labour
            WriteLabourTxt(sb, "FABRICATION LABOR", br.FabLabourName,
                br.FabRate, br.Fabrication, perFootDisplay, br.LengthFt,
                isFabrication: true, isPipeFab: br.IsPipe,
                sectionTotal: br.FabTotal);
            sb.AppendLine();

            // Item total
            sb.AppendLine($"    ────────────────────────────────────");
            sb.AppendLine($"    ITEM TOTAL:            ${br.GrandTotal,12:N2}");
        }

        /// <summary>
        /// Renders an ancillary line into the Save Report TXT. Kit
        /// headers nest their children under a "Kit (…) = $X" parent;
        /// flat lines render as a single row.
        /// </summary>
        private static void WriteAncillaryLineTxt(StringBuilder sb,
            AncillaryLine a, string indent)
        {
            if (a.IsKitHeader)
            {
                sb.AppendLine($"{indent}Kit ({a.Name,-36}) = ${a.DisplayTotal,10:N2}");
                foreach (var child in a.Children)
                    WriteAncillaryLineTxt(sb, child, indent + "  ");
            }
            else
            {
                sb.AppendLine($"{indent}{a.Name,-44} ${a.UnitPrice,8:N4} × {a.Quantity,4:0.##} = ${a.Total,10:N2}");
            }
        }

        private static void WriteLabourTxt(StringBuilder sb, string heading,
            string labourName, double rate, List<LabourTableLine> lines,
            bool perFootDisplay, double lengthFt, bool isFabrication,
            bool isPipeFab, double sectionTotal)
        {
            sb.Append("    ").Append(heading);
            if (rate > 0 && !string.IsNullOrEmpty(labourName))
                sb.Append(" (").Append(labourName).Append(" @ $")
                  .Append(rate.ToString("0.##")).Append("/hr)");
            sb.AppendLine(":");

            if (rate <= 0)
            {
                sb.AppendLine("      (labor type not configured in Pricing Setup)");
                return;
            }
            if (lines.Count == 0)
            {
                sb.AppendLine("      (no matching labor tables on this part)");
                return;
            }

            foreach (var line in lines)
            {
                string detail;
                // Pipe FAB is per-pipe (one joint-prep cycle, no
                // length scaling — see ComputePipeMinutesByTable's
                // perPipe branch). Everything else with positive
                // length is per-foot. Zero length = per-piece.
                if (isPipeFab && isFabrication)
                {
                    detail = $"{line.TotalMinutes,8:0.##} mins / pipe";
                }
                else if (perFootDisplay && lengthFt > 0)
                {
                    double minsPerFt = line.TotalMinutes / lengthFt;
                    detail = $"{minsPerFt,6:0.##} mins/ft × {lengthFt,5:0.##} ft = {line.TotalMinutes,8:0.##} mins";
                }
                else
                {
                    detail = $"{line.TotalMinutes,8:0.##} mins total";
                }
                sb.AppendLine($"      {line.TableName,-40} {detail} → ${line.Cost,9:N2}");
            }

            sb.AppendLine($"      ──────────────────────────────────────────");
            sb.AppendLine($"      Subtotal:                                   ${sectionTotal,12:N2}");
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static string ReadTypeName(FabricationPart part)
        {
            try
            {
                var typeId = part.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var t = part.Document.GetElement(typeId) as ElementType;
                    if (t != null && !string.IsNullOrEmpty(t.Name)) return t.Name;
                }
            }
            catch { }
            return part.Name ?? "(no name)";
        }

        /// <summary>
        /// Per-foot duct material cost — delegates to the shared
        /// <see cref="DuctMaterialResolver.ComputePerPiece"/> for the
        /// underlying weight × $/lb calc and divides by length. Matches
        /// Fab UI's <c>$3.00/ft</c> for a 26ga Galvanized 24×12 duct
        /// (27.80 lb / 4.92 ft × $0.53/lb).
        /// </summary>
        private static double ComputeDuctMaterialPerFt(
            FabricationPart part, MaterialMapReader material, double lengthFt)
        {
            if (lengthFt <= 0) return 0;
            double pieceCost = DuctMaterialResolver.ComputePerPiece(part, material);
            return pieceCost > 0 ? pieceCost / lengthFt : 0;
        }

        /// <summary>
        /// Per-piece duct material cost (duct fittings). Thin wrapper
        /// over <see cref="DuctMaterialResolver.ComputePerPiece"/> — kept
        /// as a passthrough so the call sites in this file stay readable.
        /// </summary>
        private static double ComputeDuctMaterialPerPiece(
            FabricationPart part, MaterialMapReader material)
            => DuctMaterialResolver.ComputePerPiece(part, material);

        /// <summary>
        /// True when the ancillary is <c>UsageType=Loose</c> —
        /// Revit's "miscellaneous loose ancillary defined on the part"
        /// catch-all (body-scoped sealants, etc.). Fab UI's Cost
        /// Breakdown spec-tracks these but doesn't bill them as
        /// material lines; we mirror that unless the user opts in via
        /// Pricing Source.
        /// </summary>
        private static bool IsLooseAncillary(
            FabricationAncillaryUsage anc)
        {
            try
            {
                return string.Equals(anc.UsageType.ToString(), "Loose",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Whitelist of <c>FabricationAncillaryType</c> values that
        /// make sense as a readable display prefix on the ancillary
        /// line — concrete material categories like <c>Sealant</c>,
        /// <c>Gasket</c>, <c>Clip</c>, <c>TieRod</c>. Excludes generic
        /// types (<c>AncillaryMaterial</c>, <c>Fixing</c>,
        /// <c>Unknown</c>) where a prefix would add noise to every
        /// connector-fixing row.
        ///
        /// Yields lines like <c>"Sealant: Class A"</c> /
        /// <c>"Gasket: FL Gasket (ASME B16.20)"</c>. Returns "" for
        /// types that don't deserve a prefix.
        /// </summary>
        private static string GetAncillaryTypePrefix(
            FabricationAncillaryUsage anc)
        {
            string type = "";
            try { type = anc.Type.ToString(); } catch { return ""; }
            switch (type)
            {
                case "Sealant":      return "Sealant";
                case "Gasket":       return "Gasket";
                case "Clip":         return "Clip";
                case "TieRod":       return "Tie Rod";
                case "SupportRod":   return "Support Rod";
                case "Corner":       return "Corner";
                case "Isolator":     return "Isolator";
                case "SeamMaterial": return "Seam";
                case "AirturnVane":  return "Airturn Vane";
                case "AirturnTrack": return "Airturn Track";
                // AncillaryMaterial / Fixing / Unknown — no prefix
                default:             return "";
            }
        }

        /// <summary>
        /// True when the part is a duct (HVAC domain). Thin wrapper over
        /// <see cref="DuctMaterialResolver.IsDuct"/> — kept for readability
        /// of the call sites in this file.
        /// </summary>
        private static bool IsDuctDomain(FabricationPart part)
            => DuctMaterialResolver.IsDuct(part);

        /// <summary>
        /// Returns a duct's size label — <c>"24"x12""</c> for
        /// rectangular, <c>"12""</c> for round/oval. Reads from the
        /// first usable connector.
        /// </summary>
        private static string GetDuctSizeLabel(FabricationPart part)
        {
            try
            {
                var cm = part.ConnectorManager;
                if (cm == null) return "";
                foreach (Connector c in cm.Connectors)
                {
                    if (c == null) continue;
                    try
                    {
                        if (c.Shape == ConnectorProfileType.Rectangular)
                        {
                            double w = c.Width  * 12.0;
                            double h = c.Height * 12.0;
                            if (w > 0 && h > 0)
                                return $"{w:0.##}\"x{h:0.##}\"";
                        }
                        else
                        {
                            double r = c.Radius;
                            if (r > 0) return $"{r * 24.0:0.##}\"";
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        private static string ReadProductCode(FabricationPart part)
        {
            try
            {
                var bp = part.get_Parameter(BuiltInParameter.FABRICATION_PRODUCT_CODE);
                if (bp != null && bp.HasValue)
                {
                    var s = bp.AsString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Look up a string parameter by display name. Case-insensitive.
        /// Walks instance params then type params. Returns "" when missing.
        /// </summary>
        internal static string ReadParam(FabricationPart part, string paramName)
        {
            try
            {
                foreach (Parameter p in part.Parameters)
                {
                    if (p == null || p.Definition == null) continue;
                    if (!string.Equals(p.Definition.Name, paramName,
                                       StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.StorageType == StorageType.String)
                    {
                        var s = p.AsString();
                        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                    }
                    else
                    {
                        var s = p.AsValueString();
                        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Best-effort primary size in inches. For pipes/fittings with
        /// connectors, uses the first non-zero connector radius × 24.
        /// </summary>
        internal static double GetPrimarySizeInches(FabricationPart part)
        {
            try
            {
                var cm = part.ConnectorManager;
                if (cm != null)
                {
                    foreach (Connector c in cm.Connectors)
                    {
                        if (c == null) continue;
                        double r = 0;
                        try { r = c.Radius; } catch { }
                        if (r > 0) return r * 24.0;
                    }
                }
            }
            catch { }
            return 0;
        }
    }

    // ── Shared types (used by command + dialog) ─────────────────────────────

    public sealed class CostBreakdownContext
    {
        public SupplierMapReader? Supplier;
        public SupplierMapPricingSource? SupplierSrc;
        public EtimesBreakpointParser? Etimes;
        public EtimesBreakpointParser? Ftimes;
        public ItmFileIndex? Itm;
        /// <summary>Material.MAP reader — gauge → $/lb + lb/sqft for
        /// sheet-metal duct/pipe parts that don't have a per-piece
        /// supplier.map price (M-Rate = None in Fab UI).</summary>
        public MaterialMapReader? MaterialMap;
        public double InstallRate;
        public double FabRate;
        public string InstallLabourName = "";
        public string FabLabourName    = "";
        public string SupplierLabel    = "";
        /// <summary>Human-readable status of the ITM index build —
        /// shown in the Cost Breakdown header so we can diagnose
        /// install-table lookup failures at a glance.</summary>
        public string ItmStatus        = "";
        /// <summary>Pricing Source toggle — when true, body-scoped
        /// (<c>UsageType=Loose</c>) ancillaries like body sealants
        /// appear as separate billed lines. Default false matches Fab
        /// ESTmep's Cost Breakdown view.</summary>
        public bool   IncludeLooseAncillaries;
    }

    public sealed class PartBreakdown
    {
        // Source element. Typed as Element (the common base) so the
        // dialog can hold a mix of FabricationParts AND native RFA
        // FamilyInstances (pipe-accessory valves). For RFAs, IsPipe /
        // IsDuct stay false and the labour blocks stay empty —
        // breakdown is base price + ITM ancillary kit + zero labour.
        public Element Part = null!;
        public string TypeName         = "";
        public string ShortDescription = "";
        public string SizeDescription  = "";
        public string FamilyName       = "";
        public string ProductCode      = "";
        public string ElementId        = "";
        public bool   IsPipe;
        public bool   IsDuct;
        /// <summary>For ducts, formatted "<c>WxH</c>" in inches
        /// (rectangular) or just the diameter (round). For pipes, the
        /// diameter is in <see cref="SizeIn"/>.</summary>
        public string SizeLabel = "";
        public double SizeIn;
        public double LengthFt;

        public double BaseListPerUnit;        // per ft for pipe, per qty for fitting
        public double AncillaryTotal;         // per ft for pipe, per qty for fitting
        public List<AncillaryLine> AncillaryDetail = new();

        public List<LabourTableLine> Installation = new();
        public List<LabourTableLine> Fabrication  = new();
        public double InstallRate;
        public double FabRate;
        public string InstallLabourName = "";
        public string FabLabourName     = "";

        public double MaterialUnit     => BaseListPerUnit + AncillaryTotal;
        /// <summary>Per-piece material total. For curve-based pipes and
        /// ducts (LengthFt > 0): base price scales by length (per-foot
        /// supplier-map / Material.MAP cost), and ancillaries stay
        /// per-piece (joint kits / fixings don't scale linearly with
        /// length). For fittings (including duct fittings) and valves,
        /// BaseListPerUnit is already a per-piece price — no length
        /// scaling.</summary>
        public double MaterialExtended => LengthFt > 0
            ? BaseListPerUnit * LengthFt + AncillaryTotal
            : MaterialUnit;
        public double InstallTotal     => Installation.Sum(l => l.Cost);
        public double FabTotal         => Fabrication.Sum(l => l.Cost);
        public double GrandTotal       => MaterialExtended + InstallTotal + FabTotal;

        /// <summary>
        /// "<descriptor> - <Product Size Description> - (1)" where descriptor
        /// is "Product Short Description" by default. When that parameter is
        /// blank or just "Standard" (an unhelpful generic value emitted by
        /// some fab content), falls back to "Family Name", then to the Revit
        /// type name as a last resort.
        /// </summary>
        public string HeaderLabel
        {
            get
            {
                string descriptor = PickDescriptor();
                var size = !string.IsNullOrWhiteSpace(SizeDescription)
                    ? SizeDescription
                    : (SizeIn > 0 ? $"{SizeIn:0.##}\"" : "");
                var sb = new StringBuilder(descriptor);
                if (!string.IsNullOrEmpty(size)) sb.Append(" - ").Append(size);
                sb.Append(" - (1)");
                return sb.ToString();
            }
        }

        private string PickDescriptor()
        {
            if (!IsNullOrStandard(ShortDescription)) return ShortDescription;
            if (!IsNullOrStandard(FamilyName))       return FamilyName;
            if (!string.IsNullOrWhiteSpace(TypeName)) return TypeName;
            return "(no description)";
        }

        private static bool IsNullOrStandard(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            return string.Equals(s.Trim(), "Standard",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class AncillaryLine
    {
        public string Name      = "";
        public double UnitPrice;
        public double Quantity;
        public double Total => UnitPrice * Quantity;
        /// <summary>When true, this line is a kit header (parent
        /// container) rather than a charged material line. Its own
        /// UnitPrice / Quantity are ignored — the displayed total is
        /// the sum of <see cref="Children"/>. Mirrors Fab UI's
        /// "Kit (Flange Kit - 150# (ASME B16.5))" header.</summary>
        public bool IsKitHeader;
        /// <summary>Child fixings (bolts, nuts, gaskets, …) of a kit
        /// header. Empty for flat lines.</summary>
        public List<AncillaryLine> Children = new();
        /// <summary>Displayed total: sum of children for kit headers,
        /// own <see cref="Total"/> otherwise.</summary>
        public double DisplayTotal => IsKitHeader
            ? Children.Sum(c => c.Total)
            : Total;
    }

    public sealed class LabourTableLine
    {
        public string TableName    = "";
        public double TotalMinutes;
        public double Rate;
        public double Cost => (TotalMinutes / 60.0) * Rate;
    }
}
