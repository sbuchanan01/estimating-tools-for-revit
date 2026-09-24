using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EstimatingTools.Revit.Zones;

namespace EstimatingTools.UI
{
    /// <summary>
    /// One-time-per-project Estimate Zones setup. Shows the current
    /// state of the three pieces (Area Scheme, Estimate Zone parameter,
    /// per-Level Area Plans) and creates whichever are missing when
    /// the user clicks Set up.
    ///
    /// The Area Scheme itself is manual — Revit's API doesn't allow
    /// programmatic creation. We surface a short set of steps when
    /// it's missing and re-check on the next click.
    /// </summary>
    public partial class EstimateZonesSetupDialog : Window
    {
        private readonly UIDocument _uiDoc;
        private readonly Document _doc;
        private readonly Autodesk.Revit.ApplicationServices.Application _app;
        private readonly ObservableCollection<LevelRowVm> _levelRows = new();

        public EstimateZonesSetupDialog(UIDocument uiDoc)
        {
            InitializeComponent();
            _uiDoc = uiDoc;
            _doc   = uiDoc.Document;
            _app   = uiDoc.Application.Application;
            LevelsList.ItemsSource = _levelRows;
            Refresh();
        }

        private void Refresh()
        {
            _levelRows.Clear();

            var schemeId = EstimateZoneScheme.FindSchemeId(_doc);
            bool schemeExists = schemeId != ElementId.InvalidElementId;
            SchemeIcon.Text = schemeExists ? "✓" : "✗";
            SchemeIcon.Foreground = System.Windows.Media.Brushes.SeaGreen;
            if (!schemeExists)
                SchemeIcon.Foreground = System.Windows.Media.Brushes.Firebrick;
            SchemeText.Text = schemeExists
                ? "\"Estimating Zones\" Area Scheme found."
                : "\"Estimating Zones\" Area Scheme is missing.";
            SchemeInstructions.Visibility = schemeExists
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
            SchemeInstructions.Text = EstimateZoneScheme
                .ManualSchemeCreationInstructions;

            bool paramBound = EstimateZoneScheme.IsParameterBound(_doc);
            ParamIcon.Text = paramBound ? "✓" : "✗";
            ParamIcon.Foreground = paramBound
                ? System.Windows.Media.Brushes.SeaGreen
                : System.Windows.Media.Brushes.Firebrick;
            ParamText.Text = paramBound
                ? "\"Estimate Zone\" parameter bound to Fab Piping / Ductwork / Hangers."
                : "\"Estimate Zone\" parameter not bound yet.";

            bool elevBound = EstimateZoneScheme.AreElevationParamsBound(_doc);
            ElevParamIcon.Text = elevBound ? "✓" : "✗";
            ElevParamIcon.Foreground = elevBound
                ? System.Windows.Media.Brushes.SeaGreen
                : System.Windows.Media.Brushes.Firebrick;
            ElevParamText.Text = elevBound
                ? "\"Zone Bottom / Top Elevation\" + \"Zone Extra Bands\" params bound to Areas (for stacked zones)."
                : "\"Zone Bottom / Top Elevation\" + \"Zone Extra Bands\" params not bound yet — stacked zones won't work.";

            var levels = EstimateZoneScheme.ListLevels(_doc);
            foreach (var lvl in levels)
            {
                var plan = schemeExists
                    ? EstimateZoneScheme.FindAreaPlanFor(_doc, schemeId, lvl)
                    : null;
                _levelRows.Add(new LevelRowVm
                {
                    Icon  = plan != null ? "✓" : "✗",
                    Label = plan != null
                        ? $"{lvl.Name}  ·  Area Plan present"
                        : $"{lvl.Name}  ·  Area Plan missing",
                });
            }

            SetupButton.IsEnabled = !schemeExists || !paramBound || !elevBound ||
                _levelRows.Any(r => r.Icon == "✗");
            SetupButton.Content = schemeExists
                ? (paramBound && elevBound &&
                   _levelRows.All(r => r.Icon == "✓")
                    ? "Everything set up"
                    : "Set up missing pieces")
                : "Refresh (create scheme first)";
        }

        private void Setup_Click(object sender, RoutedEventArgs e)
        {
            var schemeId = EstimateZoneScheme.FindSchemeId(_doc);
            if (schemeId == ElementId.InvalidElementId)
            {
                // Scheme missing — the button is really just a refresh
                // in this state. Re-run so the user sees the updated
                // status after they create the scheme via Revit's UI.
                MessageBox.Show(this,
                    EstimateZoneScheme.ManualSchemeCreationInstructions,
                    "Estimate Zones — Setup",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Refresh();
                return;
            }

            try
            {
                using var tx = new Transaction(_doc, "Estimate Zones Setup");
                tx.Start();

                // EnsureParameterBound is idempotent — it checks its own
                // work internally for both the main Estimate Zone param
                // and the two optional elevation params. Always call so
                // users with a prior partial setup pick up the new
                // vertical-band bindings on their next Set up click.
                EstimateZoneScheme.EnsureParameterBound(_app, _doc);

                var levels = EstimateZoneScheme.ListLevels(_doc);
                foreach (var lvl in levels)
                {
                    EstimateZoneScheme.EnsureAreaPlanFor(_doc, schemeId, lvl);
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Setup failed:\n" + ex.Message,
                    "Estimate Zones — Setup",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Refresh();
            MessageBox.Show(this,
                "Estimate Zones is set up. Open one of the " +
                "\"Estimate Zones — <level>\" Area Plans from the " +
                "Project Browser, sketch your zones with Revit's " +
                "built-in Area tool, then run Sync Zones.",
                "Estimate Zones — Setup",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        /// <summary>Per-Level row in the "Area Plans" section.</summary>
        private sealed class LevelRowVm
        {
            public string Icon  { get; set; } = "";
            public string Label { get; set; } = "";
        }
    }

    /// <summary>Thin ExternalCommand that opens the Estimate Zones
    /// Setup dialog. Registered on the ribbon under the Estimate
    /// Zones pulldown.</summary>
    [Autodesk.Revit.Attributes.Transaction(
        Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class EstimateZonesSetupCommand : Autodesk.Revit.UI.IExternalCommand
    {
        public Autodesk.Revit.UI.Result Execute(
            Autodesk.Revit.UI.ExternalCommandData commandData,
            ref string message,
            Autodesk.Revit.DB.ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Estimate Zones",
                    "No active document.");
                return Autodesk.Revit.UI.Result.Cancelled;
            }
            var dlg = new EstimateZonesSetupDialog(uiDoc);
            try { new System.Windows.Interop.WindowInteropHelper(dlg).Owner =
                  System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
            catch { }
            dlg.ShowDialog();
            return Autodesk.Revit.UI.Result.Succeeded;
        }
    }
}
