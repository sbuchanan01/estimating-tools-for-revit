using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EstimatingTools.Revit.Zones;

namespace EstimatingTools
{
    /// <summary>
    /// Zone Estimate — same pricing + labour pipelines as Material +
    /// Labor Estimate but bucketed by the Estimate Zone parameter each
    /// part carries. Result dialog shows per-zone
    /// Material / Installation labor / Fabrication labor / Zone total
    /// (matching the Combined estimate layout) with an
    /// "Exclude Unassigned Parts" toggle.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class EstimateByZoneCommand : IExternalCommand
    {
        private const string Title = "Zone Estimate";

        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show(Title, "No active document.");
                return Result.Cancelled;
            }
            var doc = uiDoc.Document;

            var info = Revit.PricingSourceSchema.Read(doc);
            if (!info.IsConfigured)
            {
                TaskDialog.Show(Title,
                    "No pricing source configured. Run Pricing Setup first.");
                return Result.Cancelled;
            }
            if (!EstimateZoneScheme.IsParameterBound(doc))
            {
                TaskDialog.Show(Title,
                    "The Estimate Zone parameter isn't bound to Fab " +
                    "categories yet. Open Estimate Zones → Setup " +
                    "and click Set up. Then run Sync Zones.");
                return Result.Cancelled;
            }

            var report = ZoneRollupBuilder.Build(doc, info);
            if (report.Error != null)
            {
                TaskDialog.Show(Title, report.Error);
                return Result.Failed;
            }
            if (report.Zones.Count == 0)
            {
                TaskDialog.Show(Title,
                    "No priced parts were found. Either no zones have " +
                    "been sketched yet, Sync Zones hasn't been run, " +
                    "or none of the parts have a price in the " +
                    "configured pricing source.");
                return Result.Cancelled;
            }

            string xlsxPath = Path.Combine(Path.GetTempPath(),
                $"ZoneEstimate_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

            var dlg = new UI.ZoneEstimateResultDialog(report, xlsxPath);
            dlg.ShowDialog();
            return Result.Succeeded;
        }
    }
}
