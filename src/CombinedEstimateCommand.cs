using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EstimatingTools.Revit;

namespace EstimatingTools
{
    /// <summary>
    /// Generates a combined Material + Labour estimate as a 3-sheet XLSX:
    ///
    ///   Sheet 1: Combined  — summary + material detail + labour detail
    ///                        in one continuous view (everything in one
    ///                        scroll, easiest to skim).
    ///   Sheet 2: Materials — just the material BOM (same as the
    ///                        standalone Material Estimate command).
    ///   Sheet 3: Labour    — just the labour table rollup (same as the
    ///                        standalone Labor Estimate command).
    ///
    /// Material uses MaterialEstimateCommand.BuildLines; labour uses
    /// LaborEstimateCommand.BuildLines — single source of truth shared
    /// with the standalone commands.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class CombinedEstimateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Material + Labor Estimate", "No active document.");
                return Result.Cancelled;
            }
            var doc = uiDoc.Document;

            var info = PricingSourceSchema.Read(doc);
            if (!info.IsConfigured)
            {
                TaskDialog.Show("Material + Labor Estimate",
                    "No pricing source configured. Run Pricing Setup first.");
                return Result.Cancelled;
            }

            // Material side.
            var matResult = MaterialEstimateCommand.BuildLines(doc, info);
            if (matResult.Error != null)
            {
                TaskDialog.Show("Material + Labor Estimate",
                    "Material rollup failed:\n" + matResult.Error);
                return Result.Failed;
            }
            var matOrdered = matResult.Lines
                .OrderBy(l => (int)l.Kind)
                .ThenBy(l => l.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
            double materialTotal = matOrdered.Sum(l => l.Total);

            // Labour side — non-fatal if not configured.
            string? labourWarning = null;
            var labourLines = LaborEstimateCommand.BuildLines(doc, info,
                out int _, out string? labourErr);
            if (labourErr != null)
            {
                labourWarning = labourErr;
                labourLines = new List<LaborEstimateCommand.LabourLine>();
            }
            var labourOrdered = labourLines
                .OrderBy(l => (int)l.Kind)
                .ThenBy(l => l.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            double instTotal = labourOrdered
                .Where(l => l.Kind == LaborEstimateCommand.LabourKind.Installation)
                .Sum(l => l.Cost);
            double fabTotal  = labourOrdered
                .Where(l => l.Kind == LaborEstimateCommand.LabourKind.Fabrication)
                .Sum(l => l.Cost);
            double labourTotal = instTotal + fabTotal;
            double grandTotal  = materialTotal + labourTotal;

            string xlsxPath = Path.Combine(Path.GetTempPath(),
                $"MaterialLaborEstimate_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

            // When an Excel template is configured + exists, route the
            // output through XlsxTemplateFiller so the user's branded
            // layout, currency formats, and column widths are preserved.
            // The MinimalXlsxWriter 3-sheet fallback below runs only
            // when no template is configured (or the file is missing).
            string? templateError = null;
            bool usedTemplate = false;
            if (EstimateTemplateBuilder.HasUsableTemplate(info))
            {
                try
                {
                    var req = new XlsxTemplateFiller.FillRequest();
                    EstimateTemplateBuilder.AddCommonScalars(req, doc,
                        matResult.SourceDescription, matResult.PartsScanned);
                    EstimateTemplateBuilder.AddMaterialData(req, matOrdered, materialTotal);
                    EstimateTemplateBuilder.AddLaborData(req, labourOrdered);
                    EstimateTemplateBuilder.AddGrandTotal(req);
                    XlsxTemplateFiller.Fill(info.EstimateTemplatePath, xlsxPath, req);
                    usedTemplate = true;
                }
                catch (Exception ex)
                {
                    // Template processing failed — fall back to plain
                    // output so the user still gets a usable report,
                    // and surface the reason in the result dialog.
                    templateError = $"Template fill failed: {ex.Message} " +
                                    "— wrote plain workbook instead.";
                }
            }
            if (!usedTemplate)
            {
                WriteXlsx(xlsxPath, matResult, matOrdered, materialTotal,
                    labourOrdered, instTotal, fabTotal, labourWarning);
            }

            double pipingTotal = matOrdered
                .Where(l => MaterialEstimateCommand.IsPipingKind(l.Kind)).Sum(l => l.Total);
            double ductworkTotal = matOrdered
                .Where(l => MaterialEstimateCommand.IsDuctKind(l.Kind)).Sum(l => l.Total);
            double otherTotal = materialTotal - pipingTotal - ductworkTotal;

            var materialRows = new List<(string, double)>();
            if (pipingTotal > 0)   materialRows.Add(("Piping", pipingTotal));
            if (ductworkTotal > 0) materialRows.Add(("Ductwork", ductworkTotal));
            if (otherTotal > 0)    materialRows.Add(("Hangers + ancillaries", otherTotal));

            var sections = new List<EstimatingTools.UI.EstimateCard>
            {
                EstimatingTools.UI.EstimateCards.Section("Material", 0, materialRows,
                    "Material total", materialTotal),
            };
            if (labourWarning == null)
            {
                sections.Add(EstimatingTools.UI.EstimateCards.Section("Labor", 1,
                    new List<(string, double)>
                    {
                        ("Installation labor", instTotal),
                        ("Fabrication labor", fabTotal),
                    },
                    "Labor total", labourTotal));
            }
            var totalCard = EstimatingTools.UI.EstimateCards.Total("Material + labor — total",
                new List<(string, double)>
                {
                    ("Material", materialTotal),
                    ("Installation labor", instTotal),
                    ("Fabrication labor", fabTotal),
                },
                "Grand total", grandTotal);

            string infoLine =
                $"Source: {matResult.SourceDescription}   ·   " +
                $"{matResult.PartsScanned} Fabrication parts scanned" +
                (usedTemplate
                    ? $"   ·   Template: {Path.GetFileName(info.EstimateTemplatePath)}"
                    : "");
            var warnings = new List<string>();
            if (templateError != null) warnings.Add(templateError);
            if (labourWarning != null) warnings.Add("Labor: " + labourWarning);

            var dlg = new EstimatingTools.UI.EstimateResultDialog(
                title:           "Material + Labor Estimate",
                heading:         "Combined estimate generated",
                info:            infoLine,
                sections:        sections,
                total:           totalCard,
                notes:           new List<string>(),
                warnings:        warnings,
                outputPath:      xlsxPath,
                openButtonLabel: "Open XLSX");
            dlg.ShowDialog();
            return Result.Succeeded;
        }

        // ── Workbook layout ─────────────────────────────────────────────────

        private static void WriteXlsx(string path,
            MaterialEstimateCommand.MaterialResult matResult,
            List<MaterialEstimateCommand.LineAccum> matLines,
            double materialTotal,
            List<LaborEstimateCommand.LabourLine> labourLines,
            double instTotal, double fabTotal,
            string? labourWarning)
        {
            var x = new MinimalXlsxWriter();
            WriteCombinedSheet(x, matResult, matLines, materialTotal,
                labourLines, instTotal, fabTotal, labourWarning);
            WriteMaterialsSheet(x, matLines, materialTotal);
            WriteLabourSheet(x, labourLines, instTotal, fabTotal);
            x.Save(path);
        }

        /// <summary>
        /// Sheet 1: everything in one continuous view — summary on top,
        /// then material detail, then labour detail. Same logical
        /// content as the old single-CSV combined output, just as an
        /// Excel sheet now.
        /// </summary>
        private static void WriteCombinedSheet(MinimalXlsxWriter x,
            MaterialEstimateCommand.MaterialResult matResult,
            List<MaterialEstimateCommand.LineAccum> matLines,
            double materialTotal,
            List<LaborEstimateCommand.LabourLine> labourLines,
            double instTotal, double fabTotal,
            string? labourWarning)
        {
            var s = x.AddSheet("Combined");

            // Summary block.
            s.AddRow("SUMMARY");
            s.AddRow("Source",                       matResult.SourceDescription);
            s.AddRow("Fabrication parts scanned",    matResult.PartsScanned);
            s.AddRow();
            s.AddRow("Category", "Total");
            s.AddRow("Material",             materialTotal);
            s.AddRow("Installation labor",   instTotal);
            s.AddRow("Fabrication labor",    fabTotal);
            s.AddRow("GRAND TOTAL",          materialTotal + instTotal + fabTotal);
            if (labourWarning != null)
            {
                s.AddRow();
                s.AddRow("⚠ Labor note", labourWarning);
            }
            s.AddRow();
            s.AddRow();

            // Material detail.
            s.AddRow("MATERIAL DETAIL");
            AddMaterialHeader(s);
            foreach (var l in matLines) AddMaterialRow(s, l);
            s.AddRow();
            s.AddRow(null, null, null, null, null, "Material total", materialTotal);
            s.AddRow();
            s.AddRow();

            // Labor detail.
            s.AddRow("LABOR DETAIL");
            AddLabourHeader(s);
            foreach (var l in labourLines) AddLabourRow(s, l);
            s.AddRow();
            s.AddRow(null, null, null, null, "Installation labor total", instTotal);
            s.AddRow(null, null, null, null, "Fabrication labor total",  fabTotal);
            s.AddRow(null, null, null, null, "Labor total",              instTotal + fabTotal);
        }

        /// <summary>Sheet 2: materials only. Same shape as the standalone Material Estimate CSV.</summary>
        private static void WriteMaterialsSheet(MinimalXlsxWriter x,
            List<MaterialEstimateCommand.LineAccum> matLines, double materialTotal)
        {
            var s = x.AddSheet("Materials");
            AddMaterialHeader(s);
            foreach (var l in matLines) AddMaterialRow(s, l);
            s.AddRow();
            s.AddRow(null, null, null, null, null, "GRAND TOTAL", materialTotal);
        }

        /// <summary>Sheet 3: labour only. Same shape as the standalone Labor Estimate CSV.</summary>
        private static void WriteLabourSheet(MinimalXlsxWriter x,
            List<LaborEstimateCommand.LabourLine> labourLines,
            double instTotal, double fabTotal)
        {
            var s = x.AddSheet("Labor");
            AddLabourHeader(s);
            foreach (var l in labourLines) AddLabourRow(s, l);
            s.AddRow();
            s.AddRow(null, null, null, null, "Installation labor total", instTotal);
            s.AddRow(null, null, null, null, "Fabrication labor total",  fabTotal);
            s.AddRow(null, null, null, null, "GRAND LABOR TOTAL",        instTotal + fabTotal);
        }

        // ── Row builders ────────────────────────────────────────────────────

        private static void AddMaterialHeader(MinimalXlsxWriter.Sheet s)
            => s.AddRow("Category", "Product Code", "Description", "Quantity", "Unit",
                "Unit Price", "Total Price");

        private static void AddMaterialRow(MinimalXlsxWriter.Sheet s,
            MaterialEstimateCommand.LineAccum l)
            => s.AddRow(
                l.Kind.ToString(),
                l.ProductCode,
                l.Description,
                l.Quantity,
                l.Unit,
                l.UnitPrice.HasValue ? (object)l.UnitPrice.Value : null!,
                l.UnitPrice.HasValue ? (object)l.Total : null!);

        private static void AddLabourHeader(MinimalXlsxWriter.Sheet s)
            => s.AddRow("Type", "Labor Table", "Total Minutes", "Total Hours",
                "Rate ($/hr)", "Total Cost");

        private static void AddLabourRow(MinimalXlsxWriter.Sheet s,
            LaborEstimateCommand.LabourLine l)
            => s.AddRow(
                l.Kind == LaborEstimateCommand.LabourKind.Installation
                    ? "Installation" : "Fabrication",
                l.TableName,
                l.TotalMinutes,
                l.TotalMinutes / 60.0,
                l.RatePerHour,
                l.Cost);
    }
}
