using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;
using EstimatingTools.Revit;

namespace EstimatingTools
{
    /// <summary>
    /// Walks every FabricationPart in the project and exports a rolled-up
    /// material estimate as CSV. Aggregates by Product Code:
    ///   • Pipes  — quantity = total feet across all pipe segments
    ///   • Fittings/Valves/Hangers — quantity = piece count
    ///   • Ancillaries (bolts, nuts, gaskets, …) — quantity = summed across
    ///     every parent part that consumes them
    /// Unit prices come from the configured Pricing Source (chained Fab DB
    /// + CSV files, same as Pricing Sync).
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class MaterialEstimateCommand : IExternalCommand
    {
        // Types/helpers are internal so CombinedEstimateCommand can reuse
        // the same line-building logic without duplicating it.
        // LineKind values are ordered to match the summary's display
        // grouping: piping geometry first, then ductwork geometry, then
        // shared ancillaries. Used as the OrderBy(int) key in the report.
        public enum LineKind
        {
            Pipe,         // piping curve-based
            Fitting,      // piping fitting (LocationPoint, Piping domain)
            Valve,        // valve (typically Piping domain)
            Hanger,       // hanger (cross-domain)
            Duct,         // duct curve-based (HVAC)
            DuctFitting,  // duct fitting (LocationPoint, HVAC domain)
            Ancillary,    // bolts / nuts / gaskets / sealants — domain-agnostic
        }

        /// <summary>True when the LineKind belongs to the ductwork
        /// subtotal group in the summary. Hangers + Ancillary roll into
        /// the "Other / shared" group since they're cross-domain.</summary>
        internal static bool IsDuctKind(LineKind k) =>
            k == LineKind.Duct || k == LineKind.DuctFitting;

        /// <summary>True when the LineKind belongs to the piping subtotal
        /// group in the summary.</summary>
        internal static bool IsPipingKind(LineKind k) =>
            k == LineKind.Pipe || k == LineKind.Fitting || k == LineKind.Valve;

        /// <summary>
        /// Mutable accumulator — one per unique (ProductCode, LineKind) key.
        /// Quantity is summed; UnitPrice is captured the first time a price is
        /// found and never overwritten (it's a per-product constant).
        /// </summary>
        internal sealed class LineAccum
        {
            public string ProductCode = "";
            public string Description = "";
            public LineKind Kind;
            public string Unit = "EA";
            public double Quantity;
            public double? UnitPrice;
            public int SourcePartCount;   // diagnostic — how many parents
                                          //   contributed to this row

            // ── Display + supplier metadata ─────────────────────────
            // Captured on first-population of each (Code, Kind) row.
            // Primary parts (pipe / duct / fitting / valve / hanger /
            // ductfitting) populate all three; ancillaries leave them
            // empty (they identify by ProductCode + Description).
            //
            // FamilyName / Size compose readable names like
            // `4" Sch 40 Black Steel Pipe`; Oem records the part's
            // manufacturer.
            public string FamilyName = "";
            public string Size       = "";
            public string Oem        = "";

            public double Total => UnitPrice.HasValue ? UnitPrice.Value * Quantity : 0;
        }

        /// <summary>
        /// Result bundle returned from BuildLines — all the data Combined
        /// or Material needs to render the report.
        /// </summary>
        internal sealed class MaterialResult
        {
            public List<LineAccum> Lines = new();
            public int PartsScanned;
            public int PartsWithoutCode;
            public int PartsWithoutPrice;
            public string SourceDescription = "";
            public string? Error;
        }

        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Material Estimate", "No active document.");
                return Result.Cancelled;
            }
            var doc = uiDoc.Document;
            var info = PricingSourceSchema.Read(doc);
            if (!info.IsConfigured)
            {
                TaskDialog.Show("Material Estimate",
                    "No pricing source configured. Run Pricing Setup first.");
                return Result.Cancelled;
            }

            var result = BuildLines(doc, info);
            if (result.Error != null)
            {
                TaskDialog.Show("Material Estimate", result.Error);
                return Result.Failed;
            }

            // Sort: Pipes first, then Fittings, Valves, Hangers, Ancillaries.
            var ordered = result.Lines
                .OrderBy(l => (int)l.Kind)
                .ThenBy(l => l.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Output path + writer choice: when an Excel template is
            // configured + exists we write a templated .xlsx; otherwise
            // fall back to the standalone-CSV behaviour. The result
            // dialog's "Open" button shells whichever file we actually
            // produced via the OS file association.
            double grand = ordered.Sum(l => l.Total);
            string? templateError = null;
            bool usedTemplate = false;
            string outputPath = "";  // assigned in one of the branches below
            if (EstimatingTools.Revit.EstimateTemplateBuilder.HasUsableTemplate(info))
            {
                outputPath = Path.Combine(Path.GetTempPath(),
                    $"MaterialEstimate_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
                try
                {
                    var req = new EstimatingTools.Revit.XlsxTemplateFiller.FillRequest();
                    EstimatingTools.Revit.EstimateTemplateBuilder.AddCommonScalars(req, doc,
                        result.SourceDescription, result.PartsScanned);
                    EstimatingTools.Revit.EstimateTemplateBuilder.AddMaterialData(req, ordered, grand);
                    EstimatingTools.Revit.EstimateTemplateBuilder.AddGrandTotal(req);
                    EstimatingTools.Revit.XlsxTemplateFiller.Fill(
                        info.EstimateTemplatePath, outputPath, req);
                    usedTemplate = true;
                }
                catch (Exception ex)
                {
                    templateError = $"Template fill failed: {ex.Message} " +
                                    "— wrote plain CSV instead.";
                    usedTemplate = false;
                }
            }
            if (!usedTemplate)
            {
                outputPath = Path.Combine(Path.GetTempPath(),
                    $"MaterialEstimate_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                WriteCsv(outputPath, ordered);
            }

            var totalsByKind = ordered.GroupBy(l => l.Kind)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.Total));
            string Display(LineKind k) => k switch
            {
                LineKind.Pipe        => "Pipe",
                LineKind.Fitting     => "Fitting",
                LineKind.Valve       => "Valve",
                LineKind.Hanger      => "Hanger",
                LineKind.Duct        => "Duct",
                LineKind.DuctFitting => "Duct fitting",
                LineKind.Ancillary   => "Ancillary",
                _                    => k.ToString(),
            };

            double pipingTotal = 0, ductworkTotal = 0, otherTotal = 0;
            foreach (var kv in totalsByKind)
            {
                if (IsPipingKind(kv.Key))    pipingTotal   += kv.Value;
                else if (IsDuctKind(kv.Key)) ductworkTotal += kv.Value;
                else                         otherTotal    += kv.Value;
            }

            // One card per discipline group; groups and kinds with zero
            // contribution are dropped so empty projects don't show $0.00
            // filler rows.
            IEnumerable<(string, double)> KindRows(params LineKind[] kinds) =>
                kinds.Where(k => totalsByKind.TryGetValue(k, out double t) && t > 0)
                     .Select(k => (Display(k), totalsByKind[k]));

            var sections = new List<EstimatingTools.UI.EstimateCard>();
            var totalRows = new List<(string, double)>();
            if (pipingTotal > 0)
            {
                sections.Add(EstimatingTools.UI.EstimateCards.Section("Piping", sections.Count,
                    KindRows(LineKind.Pipe, LineKind.Fitting, LineKind.Valve),
                    "Piping subtotal", pipingTotal));
                totalRows.Add(("Piping", pipingTotal));
            }
            if (ductworkTotal > 0)
            {
                sections.Add(EstimatingTools.UI.EstimateCards.Section("Ductwork", sections.Count,
                    KindRows(LineKind.Duct, LineKind.DuctFitting),
                    "Ductwork subtotal", ductworkTotal));
                totalRows.Add(("Ductwork", ductworkTotal));
            }
            if (otherTotal > 0)
            {
                sections.Add(EstimatingTools.UI.EstimateCards.Section("Hangers + ancillaries",
                    sections.Count, KindRows(LineKind.Hanger, LineKind.Ancillary),
                    "Subtotal", otherTotal));
                totalRows.Add(("Hangers + ancillaries", otherTotal));
            }
            var totalCard = EstimatingTools.UI.EstimateCards.Total(
                "Material — total", totalRows, "Grand total", grand);

            string infoLine =
                $"Source: {result.SourceDescription}   ·   " +
                $"{result.PartsScanned} Fabrication parts scanned" +
                (usedTemplate
                    ? $"   ·   Template: {Path.GetFileName(info.EstimateTemplatePath)}"
                    : "");

            var notes = new List<string> { $"Unique line items: {ordered.Count}" };
            if (result.PartsWithoutCode > 0)
                notes.Add($"Parts without Product Code: {result.PartsWithoutCode}");
            if (result.PartsWithoutPrice > 0)
                notes.Add($"Parts with no price match: {result.PartsWithoutPrice}");
            var warnings = templateError != null
                ? new List<string> { templateError }
                : new List<string>();

            var dlg = new EstimatingTools.UI.EstimateResultDialog(
                title:           "Material Estimate",
                heading:         "Material estimate generated",
                info:            infoLine,
                sections:        sections,
                total:           totalCard,
                notes:           notes,
                warnings:        warnings,
                outputPath:      outputPath,
                openButtonLabel: usedTemplate ? "Open XLSX" : "Open CSV");
            dlg.ShowDialog();
            return Result.Succeeded;
        }

        /// <summary>
        /// Walks every FabricationPart and builds the rolled-up material
        /// line list. Returns the lines plus diagnostic counts and a
        /// human-readable source description for the result dialog.
        /// </summary>
        internal static MaterialResult BuildLines(Document doc,
            Models.PricingSourceInfo info)
        {
            var result = new MaterialResult();

            SupplierMapReader? supplier = null;
            SupplierMapPricingSource? supplierSrc = null;
            CsvPricingSource? csvFallback = null;
            EstimatingTools.Revit.MaterialMapReader? materialMap = null;
            EstimatingTools.Revit.ItmFileIndex? rfaItmIndex = null;
            try
            {
                if (info.UseFabDatabase)
                {
                    string path = Path.Combine(info.DatabaseFolder, "supplier.map");
                    if (File.Exists(path)) supplier = new SupplierMapReader(path);
                    // Load etimes if present so SupplierMapPricingSource
                    // can use its table names as the joint-wrapper filter
                    // (excludes "Flange 150" / "GRC_Flange-150# (ASME B16.5)"
                    // etc. from the material ancillary roll-up — they're
                    // labour containers, not materials).
                    EstimatingTools.Revit.EtimesBreakpointParser? etimes = null;
                    string etimesPath = Path.Combine(info.DatabaseFolder, "etimes.map");
                    if (File.Exists(etimesPath))
                    {
                        try { etimes = new EstimatingTools.Revit.EtimesBreakpointParser(etimesPath); }
                        catch { }
                    }
                    supplierSrc = new SupplierMapPricingSource(info.DatabaseFolder, etimes);

                    // Material.MAP — sheet-metal duct cost (weight × $/lb).
                    // Best-effort; on failure ducts fall back to no price
                    // and show up in the PartsWithoutPrice tally.
                    string materialMapPath = Path.Combine(info.DatabaseFolder, "Material.map");
                    if (File.Exists(materialMapPath))
                    {
                        try { materialMap = new EstimatingTools.Revit.MaterialMapReader(materialMapPath); }
                        catch { }
                    }

                    // ITM index for the native RFA Pipe Accessory pass
                    // below. Loaded best-effort — when missing, RFA
                    // accessories still get their supplier.map base
                    // price; only the ITM-declared ancillary kit sum
                    // is omitted.
                    try
                    {
                        string? itemsRoot = info.DatabaseFolder != null
                            ? Path.Combine(info.DatabaseFolder, "Items")
                            : null;
                        if (itemsRoot != null && Directory.Exists(itemsRoot))
                        {
                            var loadedPaths = EstimatingTools.Revit.ItmFileIndex
                                .GetLoadedItmPaths(doc);
                            rfaItmIndex = new EstimatingTools.Revit.ItmFileIndex(
                                itemsRoot,
                                EstimatingTools.Revit.ItmFileIndex.DefaultSkipFolders,
                                loadedPaths.Count > 0 ? loadedPaths : null);
                        }
                    }
                    catch { /* index optional; degrade silently */ }
                }
                if (info.UseCsv && info.CsvFiles.Count > 0)
                {
                    foreach (var csv in info.CsvFiles)
                    {
                        if (File.Exists(csv))
                        {
                            csvFallback = new CsvPricingSource(csv);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = $"Failed to load pricing source:\n{ex.Message}";
                return result;
            }

            try
            {
                var chain = new List<IPricingSource>();
                if (supplierSrc != null) chain.Add(supplierSrc);
                foreach (var csv in info.CsvFiles)
                    chain.Add(new CsvPricingSource(csv));
                result.SourceDescription = new ChainedPricingSource(chain).Description;
            }
            catch (Exception ex)
            {
                result.Error = $"Failed to load pricing source:\n{ex.Message}";
                return result;
            }

            var parts = new FilteredElementCollector(doc)
                .OfClass(typeof(FabricationPart))
                .Cast<FabricationPart>()
                .ToList();
            result.PartsScanned = parts.Count;

            var lines = new Dictionary<(string Code, LineKind Kind), LineAccum>(
                EqualityComparer<(string, LineKind)>.Default);

            foreach (var part in parts)
            {
                string code = ReadProductCode(part);
                if (string.IsNullOrEmpty(code)) { result.PartsWithoutCode++; continue; }

                LineKind kind = ClassifyPart(part);

                // Quantity / unit convention:
                //   Pipe (curve-based) → quantity = feet
                //   Duct (curve-based) → quantity = feet (same as pipe so the
                //                          BOM aggregates linear feet of duct
                //                          per Product Code)
                //   Everything else    → quantity = 1 EA
                bool isCurveBased = part.Location is LocationCurve lc && lc.Curve != null;
                bool curveAsLength = isCurveBased &&
                    (kind == LineKind.Pipe || kind == LineKind.Duct);
                double curveLengthFt = isCurveBased
                    ? ((LocationCurve)part.Location).Curve.Length : 0;
                double qty  = curveAsLength ? curveLengthFt : 1;
                string unit = curveAsLength ? "ft" : "EA";

                // Per-piece BASE price (no length scaling yet — that comes
                // below when expressing as $/ft for curve aggregation).
                // For ducts: Material.MAP weight × $/lb. For everything
                // else: supplier.map / CSV chain.
                double? basePiecePrice = null;
                bool isDuct = DuctMaterialResolver.IsDuct(part);
                if (isDuct && materialMap != null)
                {
                    double piece = DuctMaterialResolver.ComputePerPiece(part, materialMap);
                    if (piece > 0) basePiecePrice = piece;
                }
                if (!basePiecePrice.HasValue && supplierSrc != null)
                {
                    var bd = supplierSrc.LookupBreakdown(part);
                    basePiecePrice = bd.BaseListPrice ?? bd.ItmCost;
                }
                if (!basePiecePrice.HasValue && csvFallback != null)
                {
                    var rates = csvFallback.LookupByCode(code);
                    if (rates?.MRate.HasValue == true) basePiecePrice = rates.MRate;
                }
                if (!basePiecePrice.HasValue) result.PartsWithoutPrice++;

                // Convention: for curve-based pipes/ducts reported in
                // feet, UnitPrice is $/ft. For everything else it's per-EA.
                // Aggregation by Product Code is correct either way.
                double? unitPrice = basePiecePrice;
                if (unitPrice.HasValue && curveAsLength && curveLengthFt > 0)
                    unitPrice = unitPrice.Value / curveLengthFt;

                Accumulate(lines, code, kind, ReadDescription(part), unit, qty, unitPrice,
                           ReadFamilyName(part), ReadSize(part), ReadOem(part));
                AccumulateAncillaries(part, supplier, csvFallback, lines);
            }

            // ── Native RFA Pipe Accessory pass ─────────────────────────
            // Walk every FamilyInstance in BuiltInCategory.OST_PipeAccessory
            // that isn't a FabricationPart (sibling classes — OfClass
            // implicitly excludes fab parts). For each one with a
            // populated "Product Code" type/instance parameter:
            //   1. Base unit price via supplier.map / CSV chain
            //      (code-based; same chain shape as FabricationParts but
            //      using code lookups rather than the part-aware
            //      SupplierMapPricingSource.LookupBreakdown).
            //   2. Ancillary kit cost via the matching ITM's
            //      AncillaryProductCodes list × supplier.map per-code.
            // Emit as Valve LineKind so they group with FabricationPart
            // valves in the BOM. Quantity = 1 EA per instance.
            var rfaAccessories = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeAccessory)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .ToList();
            foreach (var fi in rfaAccessories)
            {
                string code = ReadProductCodeFromRfa(fi);
                if (string.IsNullOrEmpty(code)) continue;

                // Base price via code-based chain.
                double? basePiece = null;
                if (supplier != null)
                {
                    var p = supplier.Lookup(code);
                    if (p.HasValue && p.Value > 0) basePiece = p;
                }
                if (!basePiece.HasValue && csvFallback != null)
                {
                    var rates = csvFallback.LookupByCode(code);
                    if (rates?.MRate.HasValue == true && rates.MRate.Value > 0)
                        basePiece = rates.MRate;
                }
                if (!basePiece.HasValue) { result.PartsWithoutPrice++; continue; }

                // Ancillary kit sum from the matching ITM.
                double ancSum = 0;
                if (rfaItmIndex != null && supplier != null)
                {
                    var entry = rfaItmIndex.GetByProductCode(code);
                    if (entry != null)
                    {
                        foreach (var ancCode in entry.AncillaryProductCodes)
                        {
                            var ancPrice = supplier.Lookup(ancCode);
                            if (ancPrice.HasValue && ancPrice.Value > 0)
                                ancSum += ancPrice.Value;
                        }
                    }
                }

                string typeName = "";
                string size = "";
                string famName = "";
                try
                {
                    var t = doc.GetElement(fi.GetTypeId()) as ElementType;
                    if (t != null)
                    {
                        typeName = t.Name ?? "";
                        famName  = t.FamilyName ?? "";
                    }
                    size = fi.LookupParameter("Size")?.AsString()
                        ?? fi.LookupParameter("Nominal Diameter")?.AsValueString()
                        ?? "";
                }
                catch { }

                Accumulate(lines, code, LineKind.Valve, typeName, "EA",
                           qty: 1, unitPrice: basePiece.Value + ancSum,
                           familyName: famName, size: size, oem: "");
            }
            result.PartsScanned += rfaAccessories.Count;

            result.Lines = lines.Values.ToList();
            return result;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static LineKind ClassifyPart(FabricationPart part)
        {
            bool isDuct = DuctMaterialResolver.IsDuct(part);
            if (part.Location is LocationCurve)
                return isDuct ? LineKind.Duct : LineKind.Pipe;
            try { if (part.IsAHanger()) return LineKind.Hanger; } catch { }
            if (isDuct) return LineKind.DuctFitting;
            // FabricationPart has no public IsAValve helper. Use three-
            // tier heuristic via LooksLikeValve (Product Range param →
            // ItemPath → name substring). Misses default to Fitting.
            if (LooksLikeValve(part)) return LineKind.Valve;
            return LineKind.Fitting;
        }

        /// <summary>Public alias for <see cref="ClassifyPart"/> exposed
        /// so the Zone rollup can bucket parts by the same LineKind the
        /// material report uses. Wrapper only — logic lives above.</summary>
        internal static LineKind ClassifyKind(FabricationPart part) =>
            ClassifyPart(part);

        /// <summary>Per-part quantity used by the material rollup. Pipe
        /// and Duct return their LocationCurve length in feet, everything
        /// else returns 1 EA. Kept in sync with the inline math inside
        /// <see cref="BuildLines"/>; the Zone rollup calls this so it
        /// doesn't drift.</summary>
        internal static double ComputePartQuantity(FabricationPart part,
            LineKind kind)
        {
            bool isCurveBased = part.Location is LocationCurve lc
                && lc.Curve != null;
            bool curveAsLength = isCurveBased &&
                (kind == LineKind.Pipe || kind == LineKind.Duct);
            if (curveAsLength)
                return ((LocationCurve)part.Location).Curve.Length;
            return 1;
        }

        private static bool LooksLikeValve(FabricationPart part)
        {
            // ── PRIMARY: "Product Range" parameter ─────────────────
            // Authoritative — set by the Fab configuration that
            // authored the part, not parsed from a display name.
            // Common values:
            //   "Pipe"       → curve-based pipe
            //   "Valves"     → valve     ← we match this
            //   "Standard"   → fitting   (annoyingly generic but expected)
            //   "Ductwork"   → duct / duct fitting
            //   "Hangers"    → hanger
            // Catches valves whose TypeName is "Default" and whose
            // FamilyName is brand+model (e.g. "Milwaukee 1550CB2 (FLG)")
            // with no literal "valve" anywhere — the old name-substring
            // heuristic's blind spot.
            try
            {
                var pr = part.LookupParameter("Product Range")?.AsString();
                if (!string.IsNullOrEmpty(pr) &&
                    pr.IndexOf("valve", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            catch { }

            // ── FALLBACK 1: FabricationPartType.ItemPath ───────────
            // Fab content tree organises items by top-level category:
            //   Imperial Content\Mechanical\Equipment\Valves\Milwaukee\
            //       Carbon Steel\1550CB2.ITM
            // Backup when Product Range is missing (older content).
            // ItemPath isn't on the public Revit 2026 compile surface
            // — accessed via reflection (same pattern as ItmFileIndex).
            try
            {
                var t = part.Document.GetElement(part.GetTypeId());
                var pathProp = t?.GetType().GetProperty("ItemPath");
                if (pathProp?.GetValue(t) is string itemPath &&
                    !string.IsNullOrEmpty(itemPath))
                {
                    if (ContainsPathSegment(itemPath, "Valves") ||
                        ContainsPathSegment(itemPath, "Valve"))
                        return true;
                }
            }
            catch { }

            // ── FALLBACK 2: name substring — original heuristic, kept
            try
            {
                string? n = part.Name;
                if (!string.IsNullOrEmpty(n) &&
                    n.IndexOf("valve", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            catch { }
            try
            {
                var t = part.Document.GetElement(part.GetTypeId()) as ElementType;
                if (t != null)
                {
                    if (!string.IsNullOrEmpty(t.Name) &&
                        t.Name.IndexOf("valve", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    if (!string.IsNullOrEmpty(t.FamilyName) &&
                        t.FamilyName.IndexOf("valve", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// True when <paramref name="path"/> contains
        /// <paramref name="segment"/> bounded by directory separators
        /// (either OS — `\` or `/`) on at least one side, OR at the
        /// very start/end. Prevents matching "ValveAdjacent" against
        /// the segment "Valve".
        /// </summary>
        private static bool ContainsPathSegment(string path, string segment)
        {
            string[] separators = { "\\", "/" };
            foreach (var sep in separators)
            {
                if (path.IndexOf(sep + segment + sep,
                        StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (path.StartsWith(segment + sep,
                        StringComparison.OrdinalIgnoreCase)) return true;
                if (path.EndsWith(sep + segment,
                        StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
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
            foreach (var name in new[] { "Product Code", "ProductCode", "ID" })
            {
                try
                {
                    var v = part.LookupParameter(name)?.AsString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
                }
                catch { }
            }
            return "";
        }

        /// <summary>
        /// Reads "Product Code" from a native Revit family instance —
        /// the bridge between the user's RFA content and the
        /// supplier.map pricing pipeline. Type parameter wins (where
        /// estimators most naturally put a part-level code: all
        /// instances of "6 inch Gate Valve" share the same code) and
        /// falls back to the instance parameter. Tries a short alias
        /// list for parameter-name variants estimators sometimes use.
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

        /// <summary>Best-effort human-readable description for the report.</summary>
        private static string ReadDescription(FabricationPart part)
        {
            // Type name is usually the most readable thing.
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
            return part.Name ?? "";
        }

        /// <summary>
        /// Revit family name of the part's type — e.g.
        /// <c>"Sch 40 Black Steel Pipe"</c>. Used by Send-to-Cost-
        /// Management to compose names as <c>"Size FamilyName"</c>.
        /// Returns empty when unavailable; consumers tolerate empties.
        /// </summary>
        private static string ReadFamilyName(FabricationPart part)
        {
            try
            {
                var typeId = part.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var t = part.Document.GetElement(typeId) as ElementType;
                    if (t != null && !string.IsNullOrEmpty(t.FamilyName))
                        return t.FamilyName;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Size string from <see cref="FabricationPart.Size"/> — e.g.
        /// <c>"4\""</c>, <c>"4&quot;-2&quot;"</c> for reducers, etc.
        /// Empty fallback so callers can compose with a leading space.
        /// </summary>
        private static string ReadSize(FabricationPart part)
        {
            try
            {
                string? s = part.Size;
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            catch { }
            return "";
        }

        /// <summary>
        /// OEM string from the fabrication part's Information tab —
        /// captured via best-effort named-parameter lookup at both
        /// instance and type level. Probes <c>OEM</c> first, then
        /// <c>Manufacturer</c>, then <c>Vendor</c>. Returns empty when
        /// no parameter matches; the caller's parent-supplier
        /// aggregation falls back to "Internal" on empty.
        /// </summary>
        private static string ReadOem(FabricationPart part)
        {
            string[] names = { "OEM", "Manufacturer", "Vendor" };
            foreach (var n in names)
            {
                try
                {
                    var v = part.LookupParameter(n)?.AsString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
                }
                catch { }
            }
            // Type-level fallback — some fab content exposes OEM only on
            // the ElementType, not the instance.
            try
            {
                var typeId = part.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var t = part.Document.GetElement(typeId) as ElementType;
                    if (t != null)
                    {
                        foreach (var n in names)
                        {
                            try
                            {
                                var v = t.LookupParameter(n)?.AsString();
                                if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Looks up the BASE list price for a Product Code — supplier.map
        /// first, then a CSV fallback. Does NOT include ancillary roll-up;
        /// the caller is responsible for adding ancillary line items
        /// separately so the report shows them as discrete BOM rows.
        /// </summary>
        private static double? ReadBaseListPrice(
            SupplierMapReader? supplier,
            CsvPricingSource? csv,
            string code)
        {
            if (supplier != null)
            {
                var p = supplier.Lookup(code);
                if (p.HasValue) return p;
            }
            if (csv != null)
            {
                var rates = csv.LookupByCode(code);
                if (rates?.MRate.HasValue == true) return rates.MRate;
            }
            return null;
        }

        private static void AccumulateAncillaries(FabricationPart part,
            SupplierMapReader? supplier,
            CsvPricingSource? csv,
            Dictionary<(string, LineKind), LineAccum> lines)
        {
            try
            {
                var usages = part.GetPartAncillaryUsage();
                if (usages == null) return;

                // Resolve ancillary names from the active fab config.
                FabricationConfiguration? cfg = null;
                try { cfg = FabricationConfiguration.GetFabricationConfiguration(part.Document); } catch { }

                foreach (var anc in usages)
                {
                    string ancCode = "";
                    try { ancCode = anc.ProductCode ?? ""; } catch { }
                    if (string.IsNullOrEmpty(ancCode)) continue;

                    double qty = 0;
                    try { qty = anc.Quantity; } catch { }
                    if (qty <= 0) continue;

                    string desc = "";
                    if (cfg != null)
                    {
                        try { desc = cfg.GetAncillaryName(anc.AncillaryId) ?? ""; } catch { }
                    }

                    double? unitPrice = ReadBaseListPrice(supplier, csv, ancCode);

                    Accumulate(lines, ancCode, LineKind.Ancillary, desc,
                               "EA", qty, unitPrice);
                }
            }
            catch { }
        }

        private static void Accumulate(
            Dictionary<(string, LineKind), LineAccum> lines,
            string code, LineKind kind, string desc, string unit,
            double qty, double? unitPrice)
            => Accumulate(lines, code, kind, desc, unit, qty, unitPrice,
                          familyName: "", size: "", oem: "");

        /// <summary>
        /// Same accumulator with the V2 metadata block (FamilyName /
        /// Size / Oem). The 7-arg overload above forwards with empty
        /// strings — used by ancillary roll-up where those fields don't
        /// apply. Backfills metadata on first non-empty value so an
        /// early-empty primary part doesn't lock the row to "".
        /// </summary>
        private static void Accumulate(
            Dictionary<(string, LineKind), LineAccum> lines,
            string code, LineKind kind, string desc, string unit,
            double qty, double? unitPrice,
            string familyName, string size, string oem)
        {
            var key = (code, kind);
            if (!lines.TryGetValue(key, out var acc))
            {
                acc = new LineAccum
                {
                    ProductCode = code,
                    Kind        = kind,
                    Description = desc,
                    Unit        = unit,
                    FamilyName  = familyName,
                    Size        = size,
                    Oem         = oem,
                };
                lines[key] = acc;
            }
            acc.Quantity += qty;
            acc.SourcePartCount += 1;
            // First non-null price wins; if a later occurrence has a price
            // and we don't, take it.
            if (!acc.UnitPrice.HasValue && unitPrice.HasValue)
                acc.UnitPrice = unitPrice;
            // Backfill description if it was empty initially.
            if (string.IsNullOrEmpty(acc.Description) && !string.IsNullOrEmpty(desc))
                acc.Description = desc;
            // Metadata backfill — same first-non-empty-wins policy so a
            // late part with populated parameters can fill an early gap.
            if (string.IsNullOrEmpty(acc.FamilyName) && !string.IsNullOrEmpty(familyName))
                acc.FamilyName = familyName;
            if (string.IsNullOrEmpty(acc.Size) && !string.IsNullOrEmpty(size))
                acc.Size = size;
            if (string.IsNullOrEmpty(acc.Oem) && !string.IsNullOrEmpty(oem))
                acc.Oem = oem;
        }

        internal static void WriteCsv(string path, List<LineAccum> lines)
        {
            var sb = new StringBuilder();
            WriteCsvBody(sb, lines, includeHeader: true, includeGrandTotal: true);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// Writes the material section into the provided StringBuilder.
        /// Used by Material (with header + grand total) and Combined
        /// (typically without grand total — Combined writes its own).
        /// </summary>
        internal static void WriteCsvBody(StringBuilder sb, List<LineAccum> lines,
            bool includeHeader, bool includeGrandTotal)
        {
            if (includeHeader)
                sb.AppendLine("Category,Product Code,Description,Quantity,Unit,Unit Price,Total Price");
            foreach (var l in lines)
            {
                sb.Append(l.Kind).Append(',');
                sb.Append(CsvEscape(l.ProductCode)).Append(',');
                sb.Append(CsvEscape(l.Description)).Append(',');
                sb.Append(l.Quantity.ToString("0.###",
                    CultureInfo.InvariantCulture)).Append(',');
                sb.Append(l.Unit).Append(',');
                sb.Append(l.UnitPrice.HasValue
                    ? l.UnitPrice.Value.ToString("0.####",
                        CultureInfo.InvariantCulture)
                    : "");
                sb.Append(',');
                sb.Append(l.UnitPrice.HasValue
                    ? l.Total.ToString("0.##",
                        CultureInfo.InvariantCulture)
                    : "");
                sb.AppendLine();
            }
            if (includeGrandTotal)
            {
                sb.AppendLine();
                double grand = lines.Sum(l => l.Total);
                sb.Append(",,,,,GRAND TOTAL,").Append(grand.ToString("0.##",
                    CultureInfo.InvariantCulture)).AppendLine();
            }
        }

        internal static string CsvEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
