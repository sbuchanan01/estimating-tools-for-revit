using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EstimatingTools.Revit.Zones;

namespace EstimatingTools
{
    /// <summary>
    /// Place Zone — thin proxy over Revit's built-in Area placement
    /// tool. Same story as <see cref="ZoneBoundaryCommand"/>: keeps
    /// estimators on our tab so they don't have to enable the
    /// Architecture ribbon just to drop an Area inside a closed
    /// boundary loop.
    ///
    /// After Revit's tool starts, the user clicks inside each enclosed
    /// region to place an Area, then renames it in Properties.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class PlaceZoneCommand : IExternalCommand
    {
        private const string Title = "Place Zone";

        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show(Title, "No active document.");
                return Result.Cancelled;
            }

            var doc  = uiDoc.Document;
            var view = uiDoc.ActiveView;

            var schemeId = EstimateZoneScheme.FindSchemeId(doc);
            bool inZonePlan =
                view is ViewPlan vp
                && vp.ViewType == ViewType.AreaPlan
                && vp.AreaScheme != null
                && schemeId != ElementId.InvalidElementId
                && vp.AreaScheme.Id == schemeId;
            if (!inZonePlan)
            {
                TaskDialog.Show(Title,
                    "Place Zone only works from an Area Plan under the " +
                    "Estimating Zones scheme. Open one of the " +
                    "'Estimate Zones — <level>' plans first.");
                return Result.Cancelled;
            }

            // PostableCommand.Area exposes the Area placement tool
            // directly — cleaner than the string-id fallback used by
            // Zone Boundary because Autodesk shipped this one in the
            // public enum.
            var cmdId = RevitCommandId.LookupPostableCommandId(
                PostableCommand.Area);
            if (cmdId == null || !uiApp.CanPostCommand(cmdId))
            {
                TaskDialog.Show(Title,
                    "Revit isn't accepting the Area command right now " +
                    "(something else may be active — an open sketch or " +
                    "modal dialog). Close whatever's active and try " +
                    "again.");
                return Result.Cancelled;
            }
            uiApp.PostCommand(cmdId);
            return Result.Succeeded;
        }
    }
}
