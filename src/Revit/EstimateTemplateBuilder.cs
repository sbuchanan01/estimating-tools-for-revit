using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using EstimatingTools.Models;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Builds the <see cref="XlsxTemplateFiller.FillRequest"/> for an
    /// estimate command. Centralises the scalar-name and field-name
    /// conventions so the three commands (Material / Labor / Combined)
    /// stay consistent — a template authored against one command works
    /// transparently in another: known placeholders a command doesn't
    /// produce come out blank, unknown (misspelled) ones stay literal.
    /// </summary>
    internal static class EstimateTemplateBuilder
    {
        /// <summary>
        /// Rounds a numeric value to 2 decimal places using banker-safe
        /// AwayFromZero rounding — same convention Pricing Sync uses when
        /// writing M / E / F rates to project parameters, and what the
        /// Cost Breakdown dialog shows. Keeps the XLSX output consistent
        /// with every other surface that displays money in this tool, so
        /// users never see e.g. 4.916666667 ft or $84.32000003 leaking
        /// into a branded estimate. Applied to all times, costs, rates,
        /// quantities, and totals in the scalar + table builders below.
        /// </summary>
        private static double Round2(double v) =>
            Math.Round(v, 2, MidpointRounding.AwayFromZero);

        /// <summary>
        /// Adds the project-metadata scalars (ProjectName, Date, Source,
        /// PartsScanned) common to every estimate. Caller layers on
        /// command-specific totals (material / labour).
        /// </summary>
        internal static void AddCommonScalars(
            XlsxTemplateFiller.FillRequest req,
            Document doc, string sourceDescription, int partsScanned)
        {
            req.Scalars["ProjectName"]   = ReadProjectName(doc);
            req.Scalars["Date"]          = DateTime.Now;
            req.Scalars["Source"]        = sourceDescription;
            req.Scalars["PartsScanned"]  = partsScanned;
        }

        /// <summary>
        /// Adds material-side scalars and (if any material rows exist)
        /// the <c>Materials</c> table to the request. Scalars include
        /// the per-domain subtotals (Piping / Ductwork / HangerAncillary)
        /// and the Material grand total.
        /// </summary>
        internal static void AddMaterialData(
            XlsxTemplateFiller.FillRequest req,
            List<MaterialEstimateCommand.LineAccum> lines,
            double materialTotal)
        {
            double piping = 0, duct = 0, other = 0;
            foreach (var l in lines)
            {
                if (MaterialEstimateCommand.IsPipingKind(l.Kind))      piping += l.Total;
                else if (MaterialEstimateCommand.IsDuctKind(l.Kind))   duct   += l.Total;
                else                                                    other  += l.Total;
            }

            req.Scalars["MaterialTotal"]        = Round2(materialTotal);
            req.Scalars["PipingTotal"]          = Round2(piping);
            req.Scalars["DuctworkTotal"]        = Round2(duct);
            req.Scalars["HangerAncillaryTotal"] = Round2(other);

            // Materials table — one dictionary per line. Field names
            // match the {{Materials.<FieldName>}} convention documented
            // in Pricing Setup's helper note. Sort: pipes first, then
            // fittings/valves/hangers, then ducts, then ancillaries —
            // matching the LineKind enum's display order. All numeric
            // fields rounded to 2 dp (see Round2 above).
            var rows = new List<Dictionary<string, object?>>();
            foreach (var l in lines
                .OrderBy(l => (int)l.Kind)
                .ThenBy(l => l.ProductCode, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Category"]    = l.Kind.ToString(),
                    ["Code"]        = l.ProductCode,
                    ["Description"] = l.Description,
                    ["Quantity"]    = Round2(l.Quantity),
                    ["Unit"]        = l.Unit,
                    ["UnitPrice"]   = l.UnitPrice.HasValue
                        ? (object)Round2(l.UnitPrice.Value) : null,
                    ["Total"]       = l.UnitPrice.HasValue
                        ? (object)Round2(l.Total) : null,
                });
            }
            req.Tables["Materials"] = rows;
        }

        /// <summary>
        /// Adds labour-side scalars (InstallLabor / FabLabor /
        /// LaborTotal) and the <c>Labor</c> table to the request.
        /// </summary>
        internal static void AddLaborData(
            XlsxTemplateFiller.FillRequest req,
            List<LaborEstimateCommand.LabourLine> lines)
        {
            double inst = lines.Where(l => l.Kind == LaborEstimateCommand.LabourKind.Installation)
                               .Sum(l => l.Cost);
            double fab  = lines.Where(l => l.Kind == LaborEstimateCommand.LabourKind.Fabrication)
                               .Sum(l => l.Cost);
            req.Scalars["InstallLabor"] = Round2(inst);
            req.Scalars["FabLabor"]     = Round2(fab);
            req.Scalars["LaborTotal"]   = Round2(inst + fab);

            // Labor table — all numeric fields rounded to 2 dp (see
            // Round2 above). Minutes/Hours both rounded so the visible
            // figures match: Hours = Minutes / 60 displayed at 2 dp
            // both ways without confusing carry-over digits.
            var rows = new List<Dictionary<string, object?>>();
            foreach (var l in lines
                .OrderBy(l => (int)l.Kind)
                .ThenBy(l => l.TableName, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Type"]    = l.Kind == LaborEstimateCommand.LabourKind.Installation
                                     ? "Installation" : "Fabrication",
                    ["Table"]   = l.TableName,
                    ["Minutes"] = Round2(l.TotalMinutes),
                    ["Hours"]   = Round2(l.TotalMinutes / 60.0),
                    ["Rate"]    = Round2(l.RatePerHour),
                    ["Cost"]    = Round2(l.Cost),
                });
            }
            req.Tables["Labor"] = rows;
        }

        private static readonly string[] KnownScalars =
        {
            "MaterialTotal", "PipingTotal", "DuctworkTotal", "HangerAncillaryTotal",
            "InstallLabor", "FabLabor", "LaborTotal",
        };
        private static readonly string[] KnownTables = { "Materials", "Labor" };

        /// <summary>
        /// Adds the cross-section GrandTotal scalar (Material + Labor).
        /// Call this last. Also blanks any known placeholder this estimate
        /// type doesn't produce (labor values in a Material estimate and
        /// vice versa) so one template serves all three commands without
        /// leaking raw {{Names}}; unknown names still stay literal so typos
        /// remain visible.
        /// </summary>
        internal static void AddGrandTotal(XlsxTemplateFiller.FillRequest req)
        {
            double material = req.Scalars.TryGetValue("MaterialTotal", out var m)
                && m is double md ? md : 0;
            double labor    = req.Scalars.TryGetValue("LaborTotal", out var l)
                && l is double ld ? ld : 0;
            req.Scalars["GrandTotal"] = Round2(material + labor);

            foreach (var name in KnownScalars)
                if (!req.Scalars.ContainsKey(name)) req.Scalars[name] = null;
            foreach (var table in KnownTables)
                if (!req.Tables.ContainsKey(table))
                    req.Tables[table] = new List<Dictionary<string, object?>>();
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>Revit's out-of-the-box Project Information name. A
        /// project that still carries it hasn't really been named.</summary>
        private const string RevitDefaultProjectName = "Project Name";

        /// <summary>Best-effort project name from the document's
        /// ProjectInformation. Falls back to the file name when the
        /// project info is blank or still Revit's default.</summary>
        internal static string ReadProjectName(Document doc)
        {
            try
            {
                var n = doc.ProjectInformation?.Name?.Trim();
                if (!string.IsNullOrEmpty(n) &&
                    !string.Equals(n, RevitDefaultProjectName, StringComparison.OrdinalIgnoreCase))
                    return n;
            }
            catch { }
            try
            {
                // Title carries ".rvt" when Windows shows file extensions.
                var t = doc.Title?.Trim() ?? "";
                if (t.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                    t = t.Substring(0, t.Length - 4);
                if (t.Length > 0) return t;
            }
            catch { }
            return "";
        }

        /// <summary>
        /// True when <paramref name="info"/> has a non-empty template
        /// path AND the file exists on disk. Caller uses this to decide
        /// whether to render via template or fall back to the default
        /// plain CSV / XLSX output.
        /// </summary>
        internal static bool HasUsableTemplate(PricingSourceInfo info)
        {
            return !string.IsNullOrWhiteSpace(info.EstimateTemplatePath) &&
                   System.IO.File.Exists(info.EstimateTemplatePath);
        }
    }
}
