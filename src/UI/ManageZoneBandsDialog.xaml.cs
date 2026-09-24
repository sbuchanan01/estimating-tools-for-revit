using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using EstimatingTools.Revit.Zones;

namespace EstimatingTools.UI
{
    /// <summary>
    /// Edits the vertical bands on one Estimating Zones Area. Band 1 is
    /// stored in Zone Bottom / Top Elevation, the rest in Zone Extra
    /// Bands (see <see cref="EstimateZoneScheme.WriteZoneBands"/>), so
    /// the values stay visible in the Properties palette.
    /// </summary>
    public partial class ManageZoneBandsDialog : Window
    {
        private const double DefaultBandHeightFt = 10.0;

        private readonly Document _doc;
        private readonly Area _area;
        private readonly string _areaName;
        private readonly List<double> _levelZs;
        private readonly ObservableCollection<BandRow> _rows = new();

        internal ManageZoneBandsDialog(Document doc, Area area)
        {
            InitializeComponent();
            _doc = doc;
            _area = area;
            _areaName = EstimateZoneScheme.AreaZoneName(area);

            var levels = EstimateZoneScheme.ListLevels(doc)
                .OrderBy(l => l.ProjectElevation)
                .ToList();
            _levelZs = levels.Select(l => l.ProjectElevation).ToList();
            LevelsText.Text = levels.Count == 0
                ? "(no levels)"
                : string.Join("   ·   ", levels.Select(l =>
                    $"{l.Name} = {FormatLength(l.ProjectElevation)}"));

            HeaderText.Text = $"Bands for \"{_areaName}\"";
            BandsList.ItemsSource = _rows;
            _rows.CollectionChanged += (_, _) => Renumber();

            foreach (var (bottom, top, name) in EstimateZoneScheme.ReadZoneBands(area))
            {
                double b = bottom ?? AreaLevelZ();
                double t = top ?? NextLevelAbove(b);
                _rows.Add(new BandRow(name, FormatLength(b), FormatLength(t)));
            }
            Renumber();
        }

        private void AddBand_Click(object sender, RoutedEventArgs e)
        {
            double bottom = AreaLevelZ();
            if (_rows.Count > 0 && TryParseLength(_rows[^1].TopText, out double lastTop))
                bottom = lastTop;
            _rows.Add(new BandRow(null, FormatLength(bottom),
                FormatLength(NextLevelAbove(bottom))));
        }

        private void RemoveBand_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is BandRow row)
                _rows.Remove(row);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var bands = Validate(out string? error);
            if (bands == null)
            {
                ShowError(error!);
                return;
            }

            try
            {
                using var tx = new Transaction(_doc, "Zone Bands");
                tx.Start();
                EstimateZoneScheme.WriteZoneBands(_area, bands);
                tx.Commit();
            }
            catch (Exception ex)
            {
                ShowError("Couldn't save the bands: " + ex.Message);
                return;
            }
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>Parses + checks every row. Returns null (with a
        /// message) on the first problem.</summary>
        private List<(double Bottom, double Top, string? Name)>? Validate(out string? error)
        {
            error = null;
            if (_rows.Count == 0 && EstimateZoneScheme.HasPrimaryBand(_area))
            {
                error = "Revit can't blank the elevation parameters once they're set, " +
                        "so this Area needs at least one band. To cover the whole " +
                        "level again, make one band span from this level to the next.";
                return null;
            }

            var result = new List<(double Bottom, double Top, string? Name)>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                string label = $"Band {i + 1}";

                if (!TryParseLength(row.BottomText, out double bottom))
                {
                    error = $"{label}: can't read the bottom elevation \"{row.BottomText}\".";
                    return null;
                }
                if (!TryParseLength(row.TopText, out double top))
                {
                    error = $"{label}: can't read the top elevation \"{row.TopText}\".";
                    return null;
                }
                if (top <= bottom)
                {
                    error = $"{label}: top must be above bottom.";
                    return null;
                }

                string auto = EstimateZoneScheme.AutoBandName(_areaName, i, _rows.Count);
                string typed = (row.Name ?? "").Trim();
                string? explicitName = row.IsAuto || typed.Length == 0 ||
                    string.Equals(typed, auto, StringComparison.Ordinal)
                    ? null
                    : typed;
                if (explicitName != null)
                {
                    if (explicitName.IndexOfAny(EstimateZoneScheme.ReservedBandNameChars) >= 0)
                    {
                        error = $"{label}: names can't contain : ; , ' or \".";
                        return null;
                    }
                    if (string.Equals(explicitName, ZoneAssignment.MultipleTag,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"{label}: \"{ZoneAssignment.MultipleTag}\" is reserved.";
                        return null;
                    }
                }
                string resolved = explicitName ?? auto;
                if (!seenNames.Add(resolved))
                {
                    error = $"{label}: another band is already named \"{resolved}\".";
                    return null;
                }
                result.Add((bottom, top, explicitName));
            }

            var sorted = result.Select((b, i) => (b.Bottom, b.Top, Index: i))
                .OrderBy(b => b.Bottom).ToList();
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Bottom < sorted[i - 1].Top - 1e-9)
                {
                    error = $"Bands {sorted[i - 1].Index + 1} and {sorted[i].Index + 1} " +
                            "overlap. Adjust them so each elevation belongs to one band.";
                    return null;
                }
            }
            return result;
        }

        private void Renumber()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].Number = i + 1;
                if (_rows[i].IsAuto)
                    _rows[i].SetAutoName(
                        EstimateZoneScheme.AutoBandName(_areaName, i, _rows.Count));
            }
            EmptyText.Visibility = _rows.Count == 0
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            ErrorText.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = System.Windows.Visibility.Visible;
        }

        private double AreaLevelZ() =>
            (_doc.GetElement(_area.LevelId) as Level)?.ProjectElevation ?? 0.0;

        private double NextLevelAbove(double z)
        {
            foreach (var lz in _levelZs)
                if (lz > z + 1e-6) return lz;
            return z + DefaultBandHeightFt;
        }

        private string FormatLength(double feet) =>
            UnitFormatUtils.Format(_doc.GetUnits(), SpecTypeId.Length, feet, false);

        private bool TryParseLength(string? text, out double feet)
        {
            feet = 0;
            return !string.IsNullOrWhiteSpace(text) &&
                   UnitFormatUtils.TryParse(_doc.GetUnits(), SpecTypeId.Length,
                       text, out feet);
        }

        /// <summary>One editable row. IsAuto stays true until the user
        /// types a name; auto rows get renamed as bands are added or
        /// removed so "A-1, A-2" always follows row order.</summary>
        private sealed class BandRow : INotifyPropertyChanged
        {
            private int _number;
            private string _name = "";
            private bool _isAuto;
            private string _bottomText;
            private string _topText;

            public BandRow(string? explicitName, string bottomText, string topText)
            {
                _isAuto = string.IsNullOrWhiteSpace(explicitName);
                _name = explicitName ?? "";
                _bottomText = bottomText;
                _topText = topText;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public int Number
            {
                get => _number;
                set { _number = value; OnChanged(); }
            }

            public string Name
            {
                get => _name;
                set
                {
                    if (_name == value) return;
                    _name = value ?? "";
                    OnChanged();
                    IsAuto = string.IsNullOrWhiteSpace(_name);
                }
            }

            public bool IsAuto
            {
                get => _isAuto;
                private set { if (_isAuto == value) return; _isAuto = value; OnChanged(); }
            }

            public string BottomText
            {
                get => _bottomText;
                set { _bottomText = value; OnChanged(); }
            }

            public string TopText
            {
                get => _topText;
                set { _topText = value; OnChanged(); }
            }

            public void SetAutoName(string name)
            {
                _name = name;
                OnChanged(nameof(Name));
            }

            private void OnChanged([CallerMemberName] string? prop = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }
    }

    /// <summary>Manage Bands — uses the selected Estimating Zones Area,
    /// or prompts for one, then opens <see cref="ManageZoneBandsDialog"/>.</summary>
    [Autodesk.Revit.Attributes.Transaction(
        Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class ManageZoneBandsCommand : IExternalCommand
    {
        private const string Title = "Manage Zone Bands";

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
            if (!EstimateZoneScheme.AreElevationParamsBound(doc))
            {
                TaskDialog.Show(Title,
                    "The zone band parameters aren't bound to Areas yet. " +
                    "Open Estimate Zones → Setup and click Set up missing pieces.");
                return Result.Cancelled;
            }

            var area = uiDoc.Selection.GetElementIds()
                .Select(doc.GetElement)
                .OfType<Area>()
                .FirstOrDefault(a => a.AreaScheme?.Id == schemeId);
            if (area == null)
            {
                try
                {
                    var picked = uiDoc.Selection.PickObject(ObjectType.Element,
                        new ZoneAreaFilter(schemeId),
                        "Pick an Estimating Zones Area (in its Area Plan)");
                    area = doc.GetElement(picked) as Area;
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
            }
            if (area == null) return Result.Cancelled;

            var dlg = new ManageZoneBandsDialog(doc, area);
            try { new System.Windows.Interop.WindowInteropHelper(dlg).Owner =
                  System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
            catch { }
            dlg.ShowDialog();
            return Result.Succeeded;
        }

        private sealed class ZoneAreaFilter : ISelectionFilter
        {
            private readonly ElementId _schemeId;
            public ZoneAreaFilter(ElementId schemeId) { _schemeId = schemeId; }
            public bool AllowElement(Element e) =>
                e is Area a && a.AreaScheme?.Id == _schemeId;
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }
}
