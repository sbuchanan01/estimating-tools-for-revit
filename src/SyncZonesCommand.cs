using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EstimatingTools.Revit.Zones;

namespace EstimatingTools
{
    /// <summary>
    /// Sync Zones — walks every Fab part in the model, tests it
    /// against the Areas in the Estimating Zones scheme on its host
    /// level, and writes the one zone holding most of the part into
    /// the Estimate Zone parameter. Same pattern as Pricing Sync.
    ///
    /// Requires the Estimate Zones scheme + parameter to be set up
    /// first (via the Estimate Zones Setup dialog). Bails with a
    /// pointing-finger message when either piece is missing.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SyncZonesCommand : IExternalCommand
    {
        private const string Title = "Sync Zones";

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

            var schemeId = EstimateZoneScheme.FindSchemeId(doc);
            if (schemeId == ElementId.InvalidElementId)
            {
                TaskDialog.Show(Title,
                    "The Estimating Zones scheme doesn't exist yet. " +
                    "Open Estimate Zones → Setup and click Set up.");
                return Result.Cancelled;
            }
            if (!EstimateZoneScheme.IsParameterBound(doc))
            {
                TaskDialog.Show(Title,
                    "The Estimate Zone parameter isn't bound to Fab " +
                    "categories yet. Open Estimate Zones → Setup " +
                    "and click Set up.");
                return Result.Cancelled;
            }

            var zones = ZoneAssignment.Load(doc, schemeId);
            if (zones.TotalZoneCount == 0)
            {
                TaskDialog.Show(Title,
                    "The Estimating Zones scheme exists but no zones " +
                    "have been sketched yet. Open an Area Plan under " +
                    "the Estimating Zones scheme and draw at least " +
                    "one Area before running Sync Zones.");
                return Result.Cancelled;
            }

            // Collect all Fab parts we might tag. Match the categories
            // the Estimate Zone parameter is bound to (see
            // EstimateZoneScheme._fabCategories).
            var parts = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategory(BuiltInCategory.OST_FabricationPipework)
                .Concat(new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .OfCategory(BuiltInCategory.OST_FabricationDuctwork))
                .Concat(new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .OfCategory(BuiltInCategory.OST_FabricationHangers))
                .ToList();

            int scanned = 0, tagged = 0, unassigned = 0, missingParam = 0;

            try
            {
                using var tx = new Transaction(doc, "Sync Zones");
                tx.Start();
                foreach (var part in parts)
                {
                    scanned++;
                    string zone = zones.Classify(part);
                    var pr = part.LookupParameter(EstimateZoneScheme.ZoneParamName);
                    if (pr == null || pr.IsReadOnly) { missingParam++; continue; }

                    try
                    {
                        pr.Set(zone ?? "");
                        if (string.IsNullOrEmpty(zone)) unassigned++;
                        else tagged++;
                    }
                    catch { missingParam++; }
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title,
                    $"Sync Zones failed:\n{ex.Message}");
                return Result.Failed;
            }

            var td = new TaskDialog(Title)
            {
                MainInstruction = "Sync Zones complete.",
                MainContent =
                    $"Scanned {scanned} Fab part(s) across " +
                    $"{zones.TotalZoneCount} zone(s).\n\n" +
                    $"• {tagged} part(s) tagged with a zone " +
                    $"(parts spanning zones go to the zone holding " +
                    $"most of their length)\n" +
                    $"• {unassigned} part(s) mostly outside every zone " +
                    $"(shown as UNASSIGNED in reports)\n" +
                    $"• {missingParam} part(s) skipped (parameter " +
                    $"missing or read-only)",
                CommonButtons = TaskDialogCommonButtons.Ok,
            };
            td.Show();
            return Result.Succeeded;
        }
    }
}
