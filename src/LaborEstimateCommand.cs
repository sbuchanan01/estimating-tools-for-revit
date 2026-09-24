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
    /// Generates a labour-cost estimate rolled up by Labour Table (the
    /// natural unit for labour budgets, unlike Material Estimate which
    /// rolls by Product Code). For each etimes / ftimes table referenced
    /// by any ancillary in the project, sums minutes across all parts,
    /// multiplies by the cost.map rate chosen for that side, and emits:
    ///
    ///   Type, Labour Table, Total Minutes, Total Hours, Rate ($/hr), Total Cost
    ///
    /// Type = Installation (etimes side) or Fabrication (ftimes side).
    /// The CSV ends with subtotals per type plus a grand-total row.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class LaborEstimateCommand : IExternalCommand
    {
        internal enum LabourKind { Installation, Fabrication }

        internal sealed class LabourLine
        {
            public LabourKind Kind;
            public string TableName = "";
            public double TotalMinutes;
            public double RatePerHour;
            public double Cost => TotalMinutes / 60.0 * RatePerHour;
        }

        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Labor Estimate", "No active document.");
                return Result.Cancelled;
            }
            var doc = uiDoc.Document;

            var info = PricingSourceSchema.Read(doc);
            if (!info.UseFabDatabase)
            {
                TaskDialog.Show("Labor Estimate",
                    "Configure Fab Database (and labor types) in Pricing Setup first.");
                return Result.Cancelled;
            }

            var lines = BuildLines(doc, info, out int partsScanned,
                out string? error);
            if (error != null)
            {
                TaskDialog.Show("Labor Estimate", error);
                return Result.Failed;
            }

            // Output path + writer choice: when an Excel template is
            // configured + exists we write a templated .xlsx; otherwise
            // fall back to the standalone CSV behaviour. Result-dialog
            // button label tracks whichever file actually got written.
            string? templateError = null;
            bool usedTemplate = false;
            string outputPath = "";  // assigned in one of the branches below
            if (EstimatingTools.Revit.EstimateTemplateBuilder.HasUsableTemplate(info))
            {
                outputPath = Path.Combine(Path.GetTempPath(),
                    $"LaborEstimate_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
                try
                {
                    var req = new EstimatingTools.Revit.XlsxTemplateFiller.FillRequest();
                    EstimatingTools.Revit.EstimateTemplateBuilder.AddCommonScalars(req,
                        doc, "Fab Database", partsScanned);
                    EstimatingTools.Revit.EstimateTemplateBuilder.AddLaborData(req, lines);
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
                    $"LaborEstimate_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                WriteCsv(outputPath, lines);
            }

            double instTotal = lines.Where(l => l.Kind == LabourKind.Installation).Sum(l => l.Cost);
            double fabTotal  = lines.Where(l => l.Kind == LabourKind.Fabrication).Sum(l => l.Cost);

            string infoLine =
                $"{partsScanned} Fabrication parts scanned   ·   " +
                $"{lines.Count} labor tables hit" +
                (usedTemplate
                    ? $"   ·   Template: {Path.GetFileName(info.EstimateTemplatePath)}"
                    : "");
            var totalCard = EstimatingTools.UI.EstimateCards.Total("Labor — total",
                new List<(string, double)>
                {
                    ("Installation labor", instTotal),
                    ("Fabrication labor", fabTotal),
                },
                "Total labor", instTotal + fabTotal);
            var warnings = templateError != null
                ? new List<string> { templateError }
                : new List<string>();

            var dlg = new EstimatingTools.UI.EstimateResultDialog(
                title:           "Labor Estimate",
                heading:         "Labor estimate generated",
                info:            infoLine,
                sections:        new List<EstimatingTools.UI.EstimateCard>(),
                total:           totalCard,
                notes:           new List<string>(),
                warnings:        warnings,
                outputPath:      outputPath,
                openButtonLabel: usedTemplate ? "Open XLSX" : "Open CSV");
            dlg.ShowDialog();
            return Result.Succeeded;
        }

        /// <summary>
        /// Walks every FabricationPart, builds per-labour-table totals
        /// for Installation (etimes) and Fabrication (ftimes) sides.
        /// Public-ish (internal) so CombinedEstimateCommand can reuse it.
        /// </summary>
        /// <summary>Loaded etimes/ftimes parsers + rates + ITM index
        /// bundle. Built once via <see cref="BuildLabourContext"/> so
        /// callers that need per-part labour numbers (Zone rollup) can
        /// reuse the exact same loading logic as
        /// <see cref="BuildLines"/> without re-parsing the .map files.</summary>
        internal sealed class LabourContext
        {
            public EtimesBreakpointParser? Etimes;
            public EtimesBreakpointParser? Ftimes;
            public double ErectionRate;
            public double FabricationRate;
            public EstimatingTools.Revit.ItmFileIndex? ItmIndex;
            public string? LoadError;

            /// <summary>True when at least one side (install OR fab)
            /// is fully configured (parser + rate). BuildLines gates
            /// on this to decide whether to bail with an error.</summary>
            public bool HasAnySide =>
                (Etimes != null && ErectionRate > 0) ||
                (Ftimes != null && FabricationRate > 0);
        }

        /// <summary>Load etimes.map + ftimes.map + rates + ITM index
        /// from the pricing source. Never throws — errors go into
        /// <see cref="LabourContext.LoadError"/>. Empty parsers /
        /// zero rates when the side isn't configured (both allowed
        /// so users can run install-only or fab-only if they choose).</summary>
        internal static LabourContext BuildLabourContext(Document doc,
            Models.PricingSourceInfo info)
        {
            var ctx = new LabourContext();
            try
            {
                string folder    = info.DatabaseFolder;
                string etimesPath = Path.Combine(folder, "etimes.map");
                string ftimesPath = Path.Combine(folder, "ftimes.map");

                if (!string.IsNullOrWhiteSpace(info.ErectionLabourTypeName) &&
                    info.ErectionRatePerHour > 0 &&
                    File.Exists(etimesPath))
                {
                    ctx.ErectionRate = info.ErectionRatePerHour;
                    ctx.Etimes = new EtimesBreakpointParser(etimesPath);
                }
                if (!string.IsNullOrWhiteSpace(info.FabricationLabourTypeName) &&
                    info.FabricationRatePerHour > 0 &&
                    File.Exists(ftimesPath))
                {
                    ctx.FabricationRate = info.FabricationRatePerHour;
                    ctx.Ftimes = new EtimesBreakpointParser(ftimesPath);
                }

                // ITM index for per-part install-table contribution
                // (fittings/valves — the row Fab UI shows as
                // "Installation Table Cost"). Fast path: only parse the
                // .ITMs Fab has loaded for this project's services.
                // Best-effort; on failure per-part labour falls back to
                // fixings-only.
                if (ctx.Etimes != null)
                {
                    string? itemsRoot = EstimatingTools.Revit.ItmFileIndex
                        .TryLocateItemsRoot(folder);
                    if (!string.IsNullOrEmpty(itemsRoot))
                    {
                        try
                        {
                            var loaded = EstimatingTools.Revit.ItmFileIndex
                                .GetLoadedItmPaths(doc);
                            ctx.ItmIndex = new EstimatingTools.Revit.ItmFileIndex(
                                itemsRoot!,
                                EstimatingTools.Revit.ItmFileIndex.DefaultSkipFolders,
                                loaded.Count > 0 ? loaded : null);
                        }
                        catch { /* swallow — ITM lookup is optional */ }
                    }
                }
            }
            catch (Exception ex)
            {
                ctx.LoadError = ex.Message;
            }
            return ctx;
        }

        /// <summary>Per-part labour dollars for a single Fab part.
        /// Sums minutes across every table the part touches, converts to
        /// hours, multiplies by the section rate + micro-rate. Returns
        /// (0,0) when the corresponding side isn't configured — so the
        /// Zone rollup can safely bucket regardless.</summary>
        internal static (double installCost, double fabCost) ComputePartLabourCost(
            FabricationPart part, LabourContext ctx)
        {
            double installCost = 0, fabCost = 0;
            if (ctx.Etimes != null && ctx.ErectionRate > 0)
            {
                var breakdown = AncillaryLabourBreakdown.ComputeMinutesByTable(
                    part, ctx.Etimes, isFabrication: false, ctx.ItmIndex);
                double regularMinutes = breakdown.Regular.Values.Sum();
                double microMinutes   = breakdown.ConnectorMicroRate.Values.Sum();
                installCost = (regularMinutes / 60.0) * ctx.ErectionRate
                            + (microMinutes   / 60.0) * AncillaryLabourBreakdown.ConnectorFabMicroRate;
            }
            if (ctx.Ftimes != null && ctx.FabricationRate > 0)
            {
                var breakdown = AncillaryLabourBreakdown.ComputeMinutesByTable(
                    part, ctx.Ftimes, isFabrication: true, ctx.ItmIndex);
                double regularMinutes = breakdown.Regular.Values.Sum();
                double microMinutes   = breakdown.ConnectorMicroRate.Values.Sum();
                fabCost = (regularMinutes / 60.0) * ctx.FabricationRate
                        + (microMinutes   / 60.0) * AncillaryLabourBreakdown.ConnectorFabMicroRate;
            }
            return (installCost, fabCost);
        }

        internal static List<LabourLine> BuildLines(Document doc,
            Models.PricingSourceInfo info, out int partsScanned,
            out string? error)
        {
            partsScanned = 0;
            error = null;

            var ctx = BuildLabourContext(doc, info);
            if (ctx.LoadError != null)
            {
                error = $"Failed to load labor data: {ctx.LoadError}";
                return new List<LabourLine>();
            }
            if (!ctx.HasAnySide)
            {
                error = "No labor types + rates configured in Pricing Setup, OR " +
                        "etimes.map / ftimes.map missing from the Database folder. " +
                        "Open Pricing Setup, pick Installation + Fabrication labor " +
                        "types and enter $/hr rates, save, then retry.";
                return new List<LabourLine>();
            }

            var parts = new FilteredElementCollector(doc)
                .OfClass(typeof(FabricationPart))
                .Cast<FabricationPart>()
                .ToList();
            partsScanned = parts.Count;

            // Aggregate per (Kind, TableName).
            var byKey = new Dictionary<(LabourKind, string), LabourLine>();
            foreach (var part in parts)
            {
                if (ctx.Etimes != null && ctx.ErectionRate > 0)
                    Aggregate(byKey, part, ctx.Etimes, ctx.ErectionRate,
                        LabourKind.Installation, ctx.ItmIndex);
                if (ctx.Ftimes != null && ctx.FabricationRate > 0)
                    Aggregate(byKey, part, ctx.Ftimes, ctx.FabricationRate,
                        LabourKind.Fabrication, ctx.ItmIndex);
            }
            return byKey.Values.ToList();
        }

        private static void Aggregate(
            Dictionary<(LabourKind, string), LabourLine> sink,
            FabricationPart part, EtimesBreakpointParser parser,
            double rate, LabourKind kind,
            EstimatingTools.Revit.ItmFileIndex? itmIndex)
        {
            var perTable = AncillaryLabourBreakdown.ComputeMinutesByTable(
                part, parser, isFabrication: kind == LabourKind.Fabrication, itmIndex);

            // Regular bucket → section rate ($30/hr etc.).
            foreach (var kv in perTable.Regular)
                AddOrSum(sink, kind, kv.Key, kv.Value, rate);

            // Connector-fab micro-rate bucket → $1/hr (Fab UI's internal
            // rate for connector-scoped fabrication ancillaries; only
            // populated when kind == Fabrication). Tracked as separate
            // LabourLine rows so the CSV report shows the right $/hr per
            // table — mixing rates within one bucket would mis-multiply.
            foreach (var kv in perTable.ConnectorMicroRate)
                AddOrSum(sink, kind, kv.Key, kv.Value,
                    AncillaryLabourBreakdown.ConnectorFabMicroRate);
        }

        private static void AddOrSum(
            Dictionary<(LabourKind, string), LabourLine> sink,
            LabourKind kind, string tableName, double minutes, double rate)
        {
            // Key includes rate so a table name that appears at both
            // section-rate AND micro-rate (theoretically possible) gets
            // separate rows. Practical: connector-scoped fab tables only
            // ever hit at $1/hr, so this is defensive.
            var key = (kind, $"{tableName}|@{rate:0.##}/hr");
            if (!sink.TryGetValue(key, out var line))
            {
                line = new LabourLine
                {
                    Kind        = kind,
                    TableName   = tableName,
                    RatePerHour = rate,
                };
                sink[key] = line;
            }
            line.TotalMinutes += minutes;
        }

        // ── Output ──────────────────────────────────────────────────────────

        internal static void WriteCsv(string path, List<LabourLine> lines)
        {
            // Sort: Installation first (alphabetical), then Fabrication.
            var ordered = lines
                .OrderBy(l => (int)l.Kind)
                .ThenBy (l => l.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("Type,Labor Table,Total Minutes,Total Hours,Rate ($/hr),Total Cost");
            double instCost = 0, fabCost = 0;
            foreach (var l in ordered)
            {
                sb.Append(l.Kind == LabourKind.Installation ? "Installation" : "Fabrication");
                sb.Append(',');
                sb.Append(CsvEscape(l.TableName));
                sb.Append(',');
                sb.Append(l.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append((l.TotalMinutes / 60.0).ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(l.RatePerHour.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(l.Cost.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine();

                if (l.Kind == LabourKind.Installation) instCost += l.Cost;
                else                                    fabCost += l.Cost;
            }
            sb.AppendLine();
            sb.AppendLine($",,,,Installation labor total,{instCost:0.##}");
            sb.AppendLine($",,,,Fabrication labor total,{fabCost:0.##}");
            sb.AppendLine($",,,,GRAND LABOR TOTAL,{instCost + fabCost:0.##}");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        internal static string CsvEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
