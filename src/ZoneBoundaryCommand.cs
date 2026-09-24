using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EstimatingTools.Revit.Zones;

namespace EstimatingTools
{
    /// <summary>
    /// Zone Boundary — thin proxy over Revit's built-in Area Boundary
    /// Line tool, kept on our Estimating tab so estimators sketching
    /// zones don't have to enable the Architecture tab.
    ///
    /// Uses <see cref="UIApplication.PostCommand"/> to invoke the
    /// built-in command AFTER our external command returns (Revit
    /// requires that — PostCommand queues, doesn't inline-execute).
    /// Pre-flight checks the active view is an Area Plan under the
    /// Estimating Zones scheme, otherwise the built-in tool would
    /// silently no-op or trigger a confusing error.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ZoneBoundaryCommand : IExternalCommand
    {
        private const string Title = "Zone Boundary";

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

            // Must be in an Area Plan under our Estimating Zones scheme.
            // Any other view (3D, section, floor plan, other-scheme area
            // plan) will make Revit's built-in tool no-op or throw.
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
                    "Zone Boundary only works from an Area Plan under the " +
                    "Estimating Zones scheme. Open one of the " +
                    "'Estimate Zones — <level>' plans from the Project " +
                    "Browser and try again.\n\n" +
                    "If those plans don't exist yet, run Estimate Zones " +
                    "→ Setup first.");
                return Result.Cancelled;
            }

            // Post Revit's built-in Area Boundary command. PostableCommand
            // doesn't expose it as of Revit 2026, so we fall back to the
            // internal string command-id lookup. Candidate IDs come from
            // Revit's journal files + the Revit-Lookup project. The first
            // one that resolves + accepts posting wins. Each ID that
            // doesn't match gets logged in `tried` so the fallback dialog
            // can help the user pinpoint the right one.
            // Real ID confirmed from journal 2026-07-16:
            //   Jrn.Command "Ribbon", "Draw Area Boundary Lines,
            //                          ID_OBJECTS_AREASCHEME_BOUNDARY"
            // Keep the fallback list only in case Autodesk renames it in
            // a future Revit release.
            string[] candidateIds =
            {
                "ID_OBJECTS_AREASCHEME_BOUNDARY",
                "ID_OBJECTS_AREA_BOUNDARY",
                "ID_OBJECTS_AREA_BOUNDARY_LINE",
            };

            RevitCommandId? cmdId = null;
            var tried = new System.Collections.Generic.List<string>();
            foreach (var id in candidateIds)
            {
                try
                {
                    var candidate = RevitCommandId.LookupCommandId(id);
                    if (candidate != null && uiApp.CanPostCommand(candidate))
                    {
                        cmdId = candidate;
                        break;
                    }
                    tried.Add(id + (candidate == null
                        ? "  (not registered)"
                        : "  (registered but not postable)"));
                }
                catch (System.Exception ex)
                {
                    tried.Add(id + $"  ({ex.Message})");
                }
            }
            if (cmdId == null)
            {
                var td = new TaskDialog(Title)
                {
                    MainInstruction = "Couldn't locate Revit's Area Boundary command.",
                    MainContent = "Use Architecture → Area Boundary instead; " +
                        "it draws the same zone boundary lines.\n\n" +
                        "Tried these command IDs:\n\n  " +
                        string.Join("\n  ", tried) + "\n\n" +
                        "To report this, open the journal folder, run " +
                        "Architecture → Area Boundary once, and include " +
                        "the last few journal lines and your Revit version " +
                        "in an issue on the project's GitHub page.",
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    "Reveal current Revit journal folder");
                if (td.Show() == TaskDialogResult.CommandLink1)
                {
                    try
                    {
                        string journalDir = System.IO.Path.Combine(
                            System.Environment.GetFolderPath(
                                System.Environment.SpecialFolder.LocalApplicationData),
                            "Autodesk", "Revit",
                            "Autodesk Revit " + uiApp.Application.VersionNumber,
                            "Journals");
                        if (System.IO.Directory.Exists(journalDir))
                            System.Diagnostics.Process.Start("explorer.exe", journalDir);
                    }
                    catch { }
                }
                return Result.Cancelled;
            }
            uiApp.PostCommand(cmdId);
            return Result.Succeeded;
        }
    }
}
