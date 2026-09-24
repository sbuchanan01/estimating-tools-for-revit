using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace EstimatingTools.Revit
{
    /// <summary>Material rate, Erection rate, Fabrication rate — all nullable when missing.</summary>
    public sealed record PartRates(double? MRate, double? ERate, double? FRate)
    {
        public static readonly PartRates Empty = new(null, null, null);
        public bool HasAny => MRate.HasValue || ERate.HasValue || FRate.HasValue;
    }

    /// <summary>
    /// Diagnostic breakdown of where a part's M-Rate came from. Used by
    /// Pricing Sync to flag ITMs whose Product Cost is "missing" — i.e. no
    /// supplier.map list price and no ITM-level cost — so the only money we
    /// have to write is the ancillary kit roll-up.
    /// </summary>
    public sealed record PartCostBreakdown(
        double? BaseListPrice,
        double  AncillaryTotal,
        double? ItmCost)
    {
        /// <summary>Final per-unit M-Rate (caller multiplies by length for pipes).</summary>
        public double? Total
        {
            get
            {
                double sum = (BaseListPrice ?? 0) + AncillaryTotal;
                if (sum > 0) return sum;
                return ItmCost;
            }
        }

        /// <summary>
        /// True when supplier.map had no entry AND no ITM-level Cost was set,
        /// but ancillaries still summed to something. Means the user has not
        /// priced this product directly — we're returning a kit total which
        /// may be incomplete.
        /// </summary>
        public bool MissingProductCost =>
            !BaseListPrice.HasValue && !ItmCost.HasValue && AncillaryTotal > 0;
    }

    public interface IPricingSource
    {
        /// <summary>Best-effort lookup. Returns Empty when the source has no entry for this part.</summary>
        PartRates Lookup(FabricationPart part);

        /// <summary>Diagnostic message for the result dialog.</summary>
        string Description { get; }
    }

    /// <summary>
    /// Reads M/E/F rates from CSV. Required header row with columns named
    /// (case-insensitive): ID, M-Rate, E-Rate, F-Rate. Extra columns are
    /// ignored. Match key is the FabricationPart's "ID" parameter (the
    /// fabrication-config catalog identifier that ties pricing to part).
    /// </summary>
    public sealed class CsvPricingSource : IPricingSource
    {
        private readonly Dictionary<string, PartRates> _byKey =
            new(StringComparer.OrdinalIgnoreCase);
        public string Description { get; }

        public CsvPricingSource(string path)
        {
            Description = $"CSV: {Path.GetFileName(path)}";
            Load(path);
        }

        public PartRates Lookup(FabricationPart part)
        {
            string? key = ResolveKey(part);
            if (string.IsNullOrEmpty(key)) return PartRates.Empty;
            return _byKey.TryGetValue(key!, out var r) ? r : PartRates.Empty;
        }

        /// <summary>
        /// Direct lookup by Product Code — used by Material Estimate to price
        /// ancillaries (which aren't FabricationPart elements but have a code).
        /// Returns null when the code isn't in the CSV.
        /// </summary>
        public PartRates? LookupByCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            return _byKey.TryGetValue(code, out var r) ? r : null;
        }

        public int RowCount => _byKey.Count;

        /// <summary>Read the FabricationPart's product code — exposed in the API as
        /// the built-in <c>FABRICATION_PRODUCT_CODE</c> parameter, surfaces in the
        /// Revit UI as "Product Code", and is the "ID" field shown by the part
        /// diagnostic tool. This is the catalog identifier that ties pricing
        /// rows to parts.</summary>
        private static string? ResolveKey(FabricationPart part)
        {
            // 1) Built-in parameter — canonical, fastest, no localization risk.
            try
            {
                var bp = part.get_Parameter(BuiltInParameter.FABRICATION_PRODUCT_CODE);
                var v = ReadStringish(bp);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { }

            // 2) Name-based fallbacks for older versions / unusual configs.
            foreach (var name in new[] { "Product Code", "ProductCode", "ID", "Part ID" })
            {
                try
                {
                    var p = part.LookupParameter(name);
                    var v = ReadStringish(p);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                catch { }
            }
            return null;
        }

        private static string? ReadStringish(Parameter? p)
        {
            if (p == null || !p.HasValue) return null;
            return p.StorageType switch
            {
                StorageType.String  => p.AsString(),
                StorageType.Integer => p.AsInteger().ToString(CultureInfo.InvariantCulture),
                StorageType.Double  => p.AsValueString() ?? p.AsDouble().ToString("0.####",
                                          CultureInfo.InvariantCulture),
                _ => null,
            };
        }

        private void Load(string path)
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0) throw new InvalidOperationException("CSV file is empty.");

            var header = SplitCsvLine(lines[0])
                .Select(h => h.Trim())
                .ToArray();

            // Match-key column — what users name the column for the FabricationPart
            // Product Code. Accept the common aliases.
            int idxKey = FindColumn(header,
                "Product Code", "ProductCode", "ID", "Part ID", "ItemCode", "Item Code", "Code");
            int idxM   = FindColumn(header, "M-Rate", "MRate", "Material", "Material Rate");
            int idxE   = FindColumn(header, "E-Rate", "ERate", "Labor", "Erection Rate");
            int idxF   = FindColumn(header, "F-Rate", "FRate", "Fabrication", "Fab Rate");

            if (idxKey < 0)
                throw new InvalidOperationException(
                    "CSV must have an ID column (header named 'ID', 'Part ID', or 'ItemCode').");

            for (int i = 1; i < lines.Length; i++)
            {
                var cols = SplitCsvLine(lines[i]);
                if (cols.Length <= idxKey) continue;
                var key = cols[idxKey].Trim();
                if (string.IsNullOrEmpty(key)) continue;

                _byKey[key] = new PartRates(
                    ParseDouble(idxM >= 0 && idxM < cols.Length ? cols[idxM] : null),
                    ParseDouble(idxE >= 0 && idxE < cols.Length ? cols[idxE] : null),
                    ParseDouble(idxF >= 0 && idxF < cols.Length ? cols[idxF] : null));
            }
        }

        private static int FindColumn(string[] header, params string[] candidates)
        {
            for (int i = 0; i < header.Length; i++)
                foreach (var c in candidates)
                    if (string.Equals(header[i], c, StringComparison.OrdinalIgnoreCase))
                        return i;
            return -1;
        }

        private static double? ParseDouble(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            // Accept "$1.50", "1,234.56", "1.50"
            var clean = s.Trim().TrimStart('$').Replace(",", "");
            return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double v) ? v : null;
        }

        // Minimal CSV splitter — handles quoted fields containing commas.
        private static string[] SplitCsvLine(string line)
        {
            var result = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    { sb.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }
    }

    /// <summary>
    /// Reads rates from a FabricationPart's parameters. Tries common rate field
    /// names — the actual names depend on the fab config's Item Custom Data
    /// configuration. If no rates are found, the Pricing Sync command writes a
    /// diagnostic dump so the user can see what's actually on the parts.
    /// </summary>
    public sealed class FabConfigPricingSource : IPricingSource
    {
        public string Description => "Fabrication Configuration";

        // Ordered by likelihood — first match wins. Extend as new field names surface.
        internal static readonly string[] MNames = { "M-Rate", "MRate", "Material Rate", "Material Cost", "Material" };
        internal static readonly string[] ENames = { "E-Rate", "ERate", "Erection Rate", "Labor Rate", "Labor" };
        internal static readonly string[] FNames = { "F-Rate", "FRate", "Fabrication Rate", "Fab Rate", "Fabrication" };

        public PartRates Lookup(FabricationPart part)
        {
            return new PartRates(
                ReadParamRate(part, MNames),
                ReadParamRate(part, ENames),
                ReadParamRate(part, FNames));
        }

        internal static double? ReadParamRate(FabricationPart part, string[] candidates)
        {
            foreach (var name in candidates)
            {
                try
                {
                    var p = part.LookupParameter(name);
                    if (p == null || !p.HasValue) continue;
                    var v = ParamToDouble(p);
                    if (v.HasValue) return v;
                }
                catch { }
            }
            return null;
        }

        private static double? ParamToDouble(Parameter p) =>
            p.StorageType switch
            {
                StorageType.Double  => p.AsDouble(),
                StorageType.Integer => p.AsInteger(),
                StorageType.String  => double.TryParse(p.AsString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null,
                _ => null,
            };
    }

    /// <summary>
    /// Reads M-Rate (material price) from supplier.map in the fabrication
    /// Database folder, with a fallback to the part's type-level Cost
    /// parameter (covers ITMs whose M-Rate is set directly on the ITM rather
    /// than via the supplier price list). E-Rate and F-Rate stay null —
    /// labor rates need a breakpoint-table reader.
    ///
    /// Match key: FabricationPart's Product Code (built-in FABRICATION_PRODUCT_CODE).
    /// </summary>
    public sealed class SupplierMapPricingSource : IPricingSource
    {
        private readonly SupplierMapReader _reader;
        // When set, ancillaries whose GetAncillaryName(...) returns a key
        // in this set are JOINT WRAPPERS (e.g. "Flange 150",
        // "GRC_Flange-150# (ASME B16.5)"), not material line items. Fab
        // UI rolls children's prices up under the wrapper's header but
        // does NOT charge the wrapper itself. Source: every etimes table
        // name. Caller passes EtimesBreakpointParser at construction.
        private readonly System.Collections.Generic.HashSet<string>? _jointWrapperNames;
        public string Description { get; }

        public SupplierMapPricingSource(string databaseFolder)
            : this(databaseFolder, etimesForJointWrapperFilter: null) { }

        /// <param name="etimesForJointWrapperFilter">When non-null, the
        /// names of every table in this parser become a skip-list:
        /// ancillaries whose runtime name matches one are treated as
        /// joint/install wrappers and excluded from the material total.
        /// Pass the same parser you use for labour lookups — same data
        /// source, same answers.</param>
        public SupplierMapPricingSource(string databaseFolder,
            EtimesBreakpointParser? etimesForJointWrapperFilter)
        {
            string path = Path.Combine(databaseFolder, "supplier.map");
            _reader = new SupplierMapReader(path);
            if (etimesForJointWrapperFilter != null &&
                etimesForJointWrapperFilter.TableCount > 0)
            {
                _jointWrapperNames = new System.Collections.Generic.HashSet<string>(
                    etimesForJointWrapperFilter.Tables.Keys,
                    StringComparer.OrdinalIgnoreCase);
            }
            // Generic "Fab Database" label — the source actually spans
            // supplier.map (material list prices), Material.MAP (sheet-
            // metal duct weight × $/lb via DuctMaterialPricingSource),
            // cost.map (labor rates pre-fill, dialog-only), and
            // etimes/ftimes + .ITM brackets (labor minutes). Calling
            // out just one file in the description was misleading; the
            // folder name is what uniquely identifies the source.
            Description = $"Fab Database: {Path.GetFileName(databaseFolder)}";
        }

        /// <summary>True when <see cref="GetAncillaryName"/> resolves
        /// <paramref name="anc"/> to a known etimes-table name, marking
        /// it as a joint wrapper rather than a material line item.
        /// Returns false when no etimes parser was supplied at
        /// construction, when the document has no FabricationConfiguration,
        /// or when the name doesn't match any table.</summary>
        internal bool IsJointWrapper(FabricationConfiguration? cfg,
            FabricationAncillaryUsage anc)
        {
            if (_jointWrapperNames == null || cfg == null) return false;
            string name = "";
            try { name = cfg.GetAncillaryName(anc.AncillaryId) ?? ""; } catch { }
            return !string.IsNullOrEmpty(name) &&
                   _jointWrapperNames.Contains(name);
        }

        public PartRates Lookup(FabricationPart part)
        {
            var bd = LookupBreakdown(part);
            return new PartRates(bd.Total, null, null);
        }

        /// <summary>
        /// Per-unit material cost broken into its three sources so callers
        /// can detect "missing Product Cost" (no list price, no ITM cost,
        /// only ancillary roll-up).
        /// </summary>
        public PartCostBreakdown LookupBreakdown(FabricationPart part)
        {
            // Stage 1: base part list price from supplier.map. A zero entry
            // is treated the same as "no entry" — supplier.map sometimes has
            // codes seeded with $0 and we want those flagged as missing.
            double? listPrice = null;
            string code = ReadProductCode(part);
            if (!string.IsNullOrEmpty(code))
            {
                var v = _reader.Lookup(code);
                if (v.HasValue && !double.IsNaN(v.Value) && v.Value > 0)
                    listPrice = v;
            }

            // Stage 2: roll up ancillary kit cost (bolts, nuts, gaskets,
            // hangers, etc.) — each ancillary's price × its quantity.
            double ancillary = SumAncillaryCosts(part);

            // Stage 3: ITM-level Cost parameter as a last resort. Only
            // consulted when supplier.map had no entry AND ancillaries
            // contributed nothing (so we don't shadow a real kit total).
            double? itmCost = null;
            if (!listPrice.HasValue && ancillary <= 0)
                itmCost = ReadItmCost(part);

            return new PartCostBreakdown(listPrice, ancillary, itmCost);
        }

        /// <summary>
        /// Walks every ancillary attached to the part — bolts, nuts, gaskets,
        /// hangers, supports, anything in the fab-config "ancillary kit" — and
        /// sums (price × quantity). Each ancillary carries its own ProductCode
        /// directly on the FabricationAncillaryUsage object, so the lookup is
        /// the same supplier.map call as for the parent.
        /// </summary>
        private double SumAncillaryCosts(FabricationPart part)
        {
            double total = 0;
            try
            {
                FabricationConfiguration? cfg = null;
                if (_jointWrapperNames != null)
                {
                    try { cfg = FabricationConfiguration.GetFabricationConfiguration(part.Document); }
                    catch { }
                }

                var usages = part.GetPartAncillaryUsage();
                if (usages == null || usages.Count == 0) return 0;
                foreach (var anc in usages)
                {
                    string ancCode = "";
                    try { ancCode = anc.ProductCode ?? ""; } catch { }
                    if (string.IsNullOrEmpty(ancCode)) continue;

                    // Joint-wrapper filter — see IsJointWrapper.
                    if (IsJointWrapper(cfg, anc)) continue;

                    var unitPrice = _reader.Lookup(ancCode);
                    if (!unitPrice.HasValue) continue;

                    double qty = 0;
                    try { qty = anc.Quantity; } catch { }
                    if (qty > 0) total += unitPrice.Value * qty;
                }
            }
            catch { }
            return total;
        }

        private static string ReadProductCode(FabricationPart part)
        {
            try
            {
                var bp = part.get_Parameter(BuiltInParameter.FABRICATION_PRODUCT_CODE);
                if (bp != null && bp.HasValue)
                    return bp.AsString() ?? "";
            }
            catch { }
            foreach (var name in new[] { "Product Code", "ProductCode", "ID" })
            {
                try
                {
                    var p = part.LookupParameter(name);
                    var v = p?.AsString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                catch { }
            }
            return "";
        }

        /// <summary>
        /// Reads the part's type-level Cost — Revit's standard ALL_MODEL_COST
        /// parameter, then a few named aliases. Type-level because cost lives
        /// per ITM type, not per instance. Returns null when no positive value
        /// is found (zero costs are treated as "not set" so we don't overwrite
        /// supplier-priced types that happen to have zero on the type).
        /// </summary>
        private static double? ReadItmCost(FabricationPart part)
        {
            // Cost is a type-level parameter; resolve the type element.
            Element? type = null;
            try
            {
                var typeId = part.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                    type = part.Document.GetElement(typeId);
            }
            catch { }

            // Try built-in Cost parameter on type and instance.
            foreach (var elem in new[] { type, part })
            {
                if (elem == null) continue;
                try
                {
                    var bp = elem.get_Parameter(BuiltInParameter.ALL_MODEL_COST);
                    var v = ParameterToPositiveDouble(bp);
                    if (v.HasValue) return v;
                }
                catch { }
                foreach (var name in new[] { "Cost", "Price", "Material Cost",
                                             "Material Price", "Unit Cost",
                                             "Unit Price", "M-Rate", "MRate" })
                {
                    try
                    {
                        var p = elem.LookupParameter(name);
                        var v = ParameterToPositiveDouble(p);
                        if (v.HasValue) return v;
                    }
                    catch { }
                }
            }
            return null;
        }

        private static double? ParameterToPositiveDouble(Parameter? p)
        {
            if (p == null || !p.HasValue) return null;
            double? v = p.StorageType switch
            {
                StorageType.Double  => p.AsDouble(),
                StorageType.Integer => p.AsInteger(),
                StorageType.String  => double.TryParse(p.AsString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double d) ? d : (double?)null,
                _ => null,
            };
            return v.HasValue && v.Value > 0 ? v : null;
        }
    }

    /// <summary>
    /// Composes multiple IPricingSource instances. For each rate field
    /// (M / E / F) the FIRST non-null value in source order wins. Lets
    /// supplier.map provide most M-Rates while CSV files supply labor
    /// rates and overrides for direct-priced ITMs.
    /// </summary>
    public sealed class ChainedPricingSource : IPricingSource
    {
        private readonly List<IPricingSource> _sources;
        public string Description { get; }

        public ChainedPricingSource(IEnumerable<IPricingSource> sources)
        {
            _sources = sources?.Where(s => s != null).ToList() ?? new List<IPricingSource>();
            // Filter sources with empty Description so members that
            // logically belong to an umbrella source (e.g.
            // DuctMaterialPricingSource is part of "Fab Database") can
            // opt out without leaving dangling " + " separators.
            var labels = _sources
                .Select(s => s.Description)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList();
            Description = labels.Count == 0
                ? "(no sources configured)"
                : string.Join("  +  ", labels);
        }

        public PartRates Lookup(FabricationPart part)
        {
            double? m = null, e = null, f = null;
            foreach (var src in _sources)
            {
                var r = src.Lookup(part);
                if (r == null) continue;
                if (m == null && r.MRate.HasValue) m = r.MRate;
                if (e == null && r.ERate.HasValue) e = r.ERate;
                if (f == null && r.FRate.HasValue) f = r.FRate;
                if (m.HasValue && e.HasValue && f.HasValue) break; // all filled
            }
            return new PartRates(m, e, f);
        }
    }
}
