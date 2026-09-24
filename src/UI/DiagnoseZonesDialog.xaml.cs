using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EstimatingTools.Revit.Zones;

namespace EstimatingTools.UI
{
    /// <summary>
    /// Diagnose Zones — lists every Fab part whose Estimate Zone came
    /// out as UNASSIGNED or MULTIPLE, with a per-row "Show in view"
    /// button that zooms + selects the element in the active view.
    ///
    /// Not a modal-blocking flow: after clicking Show, Revit takes the
    /// window's z-order, so the user sees the highlighted part. This
    /// dialog is Topmost so it comes back on top when the user moves
    /// focus, but doesn't disable Revit input.
    /// </summary>
    public partial class DiagnoseZonesDialog : Window
    {
        private readonly UIDocument _uiDoc;
        private readonly Document _doc;
        private readonly ObservableCollection<Row> _rows = new();

        public DiagnoseZonesDialog(UIDocument uiDoc)
        {
            InitializeComponent();
            _uiDoc = uiDoc;
            _doc   = uiDoc.Document;
            Topmost = true;
            Grid.ItemsSource = _rows;
            LoadRows();
        }

        private void LoadRows()
        {
            _rows.Clear();

            var parts = new FilteredElementCollector(_doc)
                .WhereElementIsNotElementType()
                .OfCategory(BuiltInCategory.OST_FabricationPipework)
                .Concat(new FilteredElementCollector(_doc)
                    .WhereElementIsNotElementType()
                    .OfCategory(BuiltInCategory.OST_FabricationDuctwork))
                .Concat(new FilteredElementCollector(_doc)
                    .WhereElementIsNotElementType()
                    .OfCategory(BuiltInCategory.OST_FabricationHangers))
                .ToList();

            int unassigned = 0, multiple = 0;
            foreach (var part in parts)
            {
                var p = part.LookupParameter(EstimateZoneScheme.ZoneParamName);
                if (p == null) continue;
                string zone = p.AsString() ?? "";
                bool isMultiple = zone == ZoneAssignment.MultipleTag;
                bool isUnassigned = string.IsNullOrEmpty(zone);
                if (!isMultiple && !isUnassigned) continue;

                _rows.Add(new Row
                {
                    Status      = isMultiple ? "MULTIPLE" : "UNASSIGNED",
                    Category    = part.Category?.Name ?? "",
                    IdText      = part.Id.Value.ToString(),
                    LevelName   = ResolveLevelName(part),
                    Description = TypeLabel(part),
                    ElementId   = part.Id,
                });
                if (isMultiple) multiple++; else unassigned++;
            }

            _rows
                .OrderBy(r => r.Status)
                .ThenBy(r => r.LevelName)
                .ThenBy(r => r.Category)
                .ToList();   // touch enumerable so grid picks up sort — DataGrid on ObservableCollection
                             // shows insertion order; explicit re-add keeps ordering deterministic.
            var sorted = _rows.OrderBy(r => r.Status)
                              .ThenBy(r => r.LevelName)
                              .ThenBy(r => r.Category).ToList();
            _rows.Clear();
            foreach (var r in sorted) _rows.Add(r);

            CountText.Text = _rows.Count == 0
                ? "No unassigned or multiple-zone parts. Zones are clean."
                : $"{_rows.Count} part(s) need attention  " +
                  $"·  {multiple} MULTIPLE · {unassigned} UNASSIGNED";
        }

        private string ResolveLevelName(Element part)
        {
            var lid = part.LevelId;
            if (lid == ElementId.InvalidElementId) return "";
            return _doc.GetElement(lid)?.Name ?? "";
        }

        // Try Type name → falls back to element name so the grid isn't blank.
        private string TypeLabel(Element part)
        {
            try
            {
                var t = _doc.GetElement(part.GetTypeId());
                if (t != null && !string.IsNullOrEmpty(t.Name)) return t.Name;
            }
            catch { }
            return part.Name ?? "";
        }

        private void Show_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.DataContext is not Row row) return;
            try
            {
                _uiDoc.Selection.SetElementIds(new[] { row.ElementId });
                _uiDoc.ShowElements(row.ElementId);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Couldn't show element {row.IdText}:\n{ex.Message}",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadRows();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        internal sealed class Row
        {
            public string    Status      { get; set; } = "";
            public string    Category    { get; set; } = "";
            public string    IdText      { get; set; } = "";
            public string    LevelName   { get; set; } = "";
            public string    Description { get; set; } = "";
            public ElementId ElementId   { get; set; } = ElementId.InvalidElementId;
        }
    }

    /// <summary>Thin ExternalCommand that opens the Diagnose Zones
    /// dialog. Registered on the Estimate Zones ribbon pulldown.</summary>
    [Autodesk.Revit.Attributes.Transaction(
        Autodesk.Revit.Attributes.TransactionMode.ReadOnly)]
    public class DiagnoseZonesCommand : Autodesk.Revit.UI.IExternalCommand
    {
        public Autodesk.Revit.UI.Result Execute(
            Autodesk.Revit.UI.ExternalCommandData commandData,
            ref string message,
            Autodesk.Revit.DB.ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Diagnose Zones",
                    "No active document.");
                return Autodesk.Revit.UI.Result.Cancelled;
            }
            if (!EstimateZoneScheme.IsParameterBound(uiDoc.Document))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Diagnose Zones",
                    "Estimate Zone parameter isn't bound yet. Open " +
                    "Estimate Zones → Setup first.");
                return Autodesk.Revit.UI.Result.Cancelled;
            }

            var dlg = new DiagnoseZonesDialog(uiDoc);
            try { new System.Windows.Interop.WindowInteropHelper(dlg).Owner =
                  System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
            catch { }
            dlg.Show();     // modeless — Show in view needs Revit input
            return Autodesk.Revit.UI.Result.Succeeded;
        }
    }
}
