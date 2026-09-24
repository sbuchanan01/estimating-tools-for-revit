using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EstimatingTools.Revit.Zones;

namespace EstimatingTools
{
    /// <summary>Show Zone Volumes — extrudes each Estimating Zones
    /// Area between its Bottom/Top elevation params and drops the
    /// prism in as a semi-transparent DirectShape. Open a 3D view
    /// after running to confirm the sketch reads the way you expect.
    /// Idempotent: running twice deletes the prior batch first so
    /// re-showing after a sketch tweak leaves exactly one set behind.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ShowZoneVolumesCommand : IExternalCommand
    {
        private const string Title = "Show Zone Volumes";

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
                    "Estimating Zones scheme doesn't exist yet. " +
                    "Open Estimate Zones → Setup first.");
                return Result.Cancelled;
            }

            int removed = 0, added = 0;
            try
            {
                using var tx = new Transaction(doc, "Show Zone Volumes");
                tx.Start();
                // Refresh idempotency: clear prior volumes first so a
                // second Show after moving a boundary doesn't leave a
                // stack of overlapping ghosts behind.
                removed = ZoneVolumeVisualizer.Hide(doc);
                var created = ZoneVolumeVisualizer.Show(doc, schemeId);
                added = created.Count;
                tx.Commit();
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, $"Show Zone Volumes failed:\n{ex.Message}");
                return Result.Failed;
            }

            if (added == 0)
            {
                TaskDialog.Show(Title,
                    "No zones were extruded. Either no zones are " +
                    "sketched under the Estimating Zones scheme yet, or " +
                    "the Area boundaries can't be closed into a solid.");
                return Result.Succeeded;
            }

            var td = new TaskDialog(Title)
            {
                MainInstruction =
                    $"Created {added} zone volume{(added == 1 ? "" : "s")}.",
                MainContent =
                    (removed > 0
                        ? $"Cleared {removed} prior volume(s) first.\n\n"
                        : "") +
                    "Switch to a 3D view to walk around the results. " +
                    "Colours cycle from a fixed palette keyed by zone " +
                    "name. Use Hide Zone Volumes when you're done.",
                CommonButtons = TaskDialogCommonButtons.Ok,
            };
            td.Show();
            return Result.Succeeded;
        }
    }

    /// <summary>Hide Zone Volumes — deletes every DirectShape stamped
    /// with our Comments tag. Hand-authored DirectShapes are safe
    /// because the tag filter is exact-prefix.</summary>
    [Transaction(TransactionMode.Manual)]
    public class HideZoneVolumesCommand : IExternalCommand
    {
        private const string Title = "Hide Zone Volumes";

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
            int removed;
            try
            {
                using var tx = new Transaction(doc, "Hide Zone Volumes");
                tx.Start();
                removed = ZoneVolumeVisualizer.Hide(doc);
                tx.Commit();
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, $"Hide Zone Volumes failed:\n{ex.Message}");
                return Result.Failed;
            }
            TaskDialog.Show(Title,
                removed == 0
                    ? "No zone volumes were found to hide."
                    : $"Removed {removed} zone volume(s).");
            return Result.Succeeded;
        }
    }
}
