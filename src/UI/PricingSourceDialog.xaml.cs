using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using EstimatingTools.Models;
using EstimatingTools.Revit;

namespace EstimatingTools.UI
{
    public partial class PricingSourceDialog : Window
    {
        private readonly Document _doc;

        // Snapshot of cost.map rows (Name / $/hr) for the labour-type combos.
        // Built when the user picks (or had picked) a Database folder.
        // Stays "" / 0 until then.
        private List<LabourRow> _labourRows = new();

        public PricingSourceDialog(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            Topmost = true;

            var info = PricingSourceSchema.Read(doc);

            // Fab Database
            if (info.UseFabDatabase)
            {
                UseFabDbCheck.IsChecked = true;
                FolderPathBox.Text      = info.DatabaseFolder;
                LoadLabourRates(info.DatabaseFolder);
            }
            // Labour rates: prefer saved V6 values; fall back to cost.map
            // lookup when the V5/V4 fallback left rates at 0. This keeps
            // existing projects working on first dialog-open after upgrade
            // without forcing them to retype the rate they already chose.
            ApplyLabourSelection(ErectionLabourCombo, ErectionRateBox,
                info.ErectionLabourTypeName, info.ErectionRatePerHour);
            ApplyLabourSelection(FabricationLabourCombo, FabricationRateBox,
                info.FabricationLabourTypeName, info.FabricationRatePerHour);

            // CSV files
            if (info.UseCsv)
            {
                UseCsvCheck.IsChecked = true;
                foreach (var f in info.CsvFiles) CsvList.Items.Add(f);
            }

            IncludeLooseAncillariesCheck.IsChecked = info.IncludeLooseAncillaries;
            WriteLaborAsHoursCheck.IsChecked       = info.WriteLaborAsHours;

            // Revit parameter mapping — populate from project params, then
            // restore saved selections (start empty if the user never set
            // them, per the design decision in 2026-05-19's review).
            LoadProjectParameters();
            SetParamComboSelection(MaterialParamCombo,    info.MaterialRateParamName);
            SetParamComboSelection(InstallParamCombo,     info.InstallRateParamName);
            SetParamComboSelection(FabricationParamCombo, info.FabricationRateParamName);

            // Estimate template path (V7).
            TemplatePathBox.Text = info.EstimateTemplatePath ?? "";
        }

        // ── Estimate template picker ───────────────────────────────────────────

        private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Excel template (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                Title  = "Select Estimate template (.xlsx)",
            };
            if (!string.IsNullOrEmpty(TemplatePathBox.Text) &&
                File.Exists(TemplatePathBox.Text))
                ofd.InitialDirectory = Path.GetDirectoryName(TemplatePathBox.Text);
            if (ofd.ShowDialog(this) == true)
                TemplatePathBox.Text = ofd.FileName;
        }

        private void ClearTemplate_Click(object sender, RoutedEventArgs e)
            => TemplatePathBox.Text = "";

        /// <summary>
        /// Opens a modal explaining how the .xlsx template works,
        /// listing the supported placeholders + table anchors in a
        /// readable form. Replaces the wall-of-text info block that
        /// used to sit below the template path row.
        /// </summary>
        private void EstimateTemplateHelp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new EstimateTemplateHelpDialog { Owner = this };
            dlg.ShowDialog();
        }

        /// <summary>
        /// Writes a sample estimate template to a user-chosen path and
        /// offers to set it as the active template + open it in Excel.
        /// Lets the user grab a starting point without leaving the dialog.
        /// The generated file lists every supported scalar placeholder
        /// and table anchor, plus a dedicated Instructions sheet.
        /// </summary>
        private void GenerateSampleTemplate_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "Save sample estimate template",
                Filter   = "Excel template (*.xlsx)|*.xlsx",
                FileName = "EstimateTemplate_Sample.xlsx",
            };
            if (sfd.ShowDialog(this) != true) return;

            try
            {
                EstimatingTools.Revit.SampleTemplateGenerator.Write(sfd.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not write sample template:\n{ex.Message}",
                    "Pricing Setup", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Offer to plug the new template in as the active path AND
            // open it in Excel so the user can start customising right
            // away. Both are opt-in via Yes/No so the user stays in
            // control of which template applies to estimates.
            var setActive = MessageBox.Show(this,
                $"Sample template saved:\n{sfd.FileName}\n\n" +
                "Set this as the active estimate template?",
                "Pricing Setup", MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (setActive == MessageBoxResult.Yes)
                TemplatePathBox.Text = sfd.FileName;

            var openNow = MessageBox.Show(this,
                "Open the sample in Excel now?",
                "Pricing Setup", MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (openNow == MessageBoxResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(sfd.FileName)
                        { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        $"Could not open '{sfd.FileName}':\n{ex.Message}",
                        "Pricing Setup", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }

        private void Source_Changed(object sender, RoutedEventArgs e) { /* IsEnabled bindings handle UI */ }

        // ── Folder browsing ────────────────────────────────────────────────────

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var fd = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select fabrication Database folder (containing supplier.map)",
            };
            if (!string.IsNullOrEmpty(FolderPathBox.Text) && Directory.Exists(FolderPathBox.Text))
                fd.InitialDirectory = FolderPathBox.Text;
            if (fd.ShowDialog(this) != true) return;
            FolderPathBox.Text = fd.FolderName;
            LoadLabourRates(fd.FolderName);
        }

        // ── Labour rates (cost.map) loading ────────────────────────────────────

        internal sealed class LabourRow
        {
            public string Name = "";
            public double Rate;
            public string Display => string.IsNullOrEmpty(Name)
                ? $"(blank) — ${Rate:0.##}/hr"
                : $"{Name} — ${Rate:0.##}/hr";
            public override string ToString() => Display;
        }

        /// <summary>
        /// Reads cost.map from <paramref name="folder"/> and populates
        /// <see cref="_labourRows"/> + both labour-type combos. Tolerant
        /// of missing cost.map — the combos just stay empty (the user
        /// can still type a custom name + rate freely).
        /// </summary>
        private void LoadLabourRates(string folder)
        {
            ErectionLabourCombo.ItemsSource    = null;
            FabricationLabourCombo.ItemsSource = null;
            _labourRows = new List<LabourRow>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            string costPath = Path.Combine(folder, "cost.map");
            if (!File.Exists(costPath)) return;

            try
            {
                var reader = new CostMapReader(costPath);
                _labourRows = reader.All
                    .Select(r => new LabourRow { Name = r.Name, Rate = r.RatePerHour })
                    .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                ErectionLabourCombo.ItemsSource    = _labourRows;
                FabricationLabourCombo.ItemsSource = _labourRows.ToList();  // separate binding source
            }
            catch
            {
                // cost.map unreadable — leave combos empty; user can still type.
            }
        }

        /// <summary>
        /// Applies a saved (name, rate) pair to a labour combo + rate textbox.
        /// Matches the saved name against the loaded cost.map rows. If the
        /// rate isn't saved (0 from V4/V5 fallback) but the name matches a
        /// cost.map row, uses the row's rate as a one-time migration default.
        /// </summary>
        private void ApplyLabourSelection(ComboBox combo, TextBox rateBox,
                                          string savedName, double savedRate)
        {
            if (string.IsNullOrEmpty(savedName))
            {
                combo.Text     = "";
                rateBox.Text   = "";
                return;
            }

            // Try to select the matching row in the combo (for the dropdown
            // display); IsEditable=true means the user can also type a name
            // that doesn't match any row.
            LabourRow? row = null;
            if (_labourRows.Count > 0)
            {
                row = _labourRows.FirstOrDefault(r => string.Equals(
                    r.Name, savedName, StringComparison.OrdinalIgnoreCase));
            }

            if (row != null) combo.SelectedItem = row;
            combo.Text = savedName;

            // Rate priority: saved value if set; else cost.map row's rate
            // as a migration default; else blank.
            double rate = savedRate > 0 ? savedRate : (row?.Rate ?? 0);
            rateBox.Text = rate > 0
                ? rate.ToString("0.##", CultureInfo.InvariantCulture)
                : "";
        }

        private void ErectionLabourCombo_SelectionChanged(object sender,
            SelectionChangedEventArgs e)
        {
            PrefillRateFromSelection(ErectionLabourCombo, ErectionRateBox);
        }

        private void FabricationLabourCombo_SelectionChanged(object sender,
            SelectionChangedEventArgs e)
        {
            PrefillRateFromSelection(FabricationLabourCombo, FabricationRateBox);
        }

        /// <summary>
        /// When the user picks a row from the combo dropdown, pre-fill the
        /// associated rate textbox with that row's $/hr. The user can then
        /// override it freely — pre-fill only fires on SelectionChanged
        /// (combo-item click), not on text edit, so manual typing doesn't
        /// wipe the rate the user just entered.
        /// </summary>
        private static void PrefillRateFromSelection(ComboBox combo, TextBox rateBox)
        {
            if (combo.SelectedItem is LabourRow row && row.Rate > 0)
            {
                rateBox.Text = row.Rate.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }

        // ── Revit parameter mapping ───────────────────────────────────────────

        internal sealed class ParamRow
        {
            public string Name        = "";
            public string TypeLabel   = "";   // "Text" / "Number" / "Currency"
            public string Display => $"{Name}  ({TypeLabel})";
            public override string ToString() => Display;
        }

        /// <summary>
        /// Builds the M/E/F-Rate target dropdown rows by combining two
        /// sources, deduped by parameter name:
        /// <list type="number">
        /// <item><b>Sample FabricationPart probe</b> — collects every
        /// writable non-built-in parameter on the first available
        /// FabricationPart in the project. Uses <c>StorageType</c> as the
        /// primary classifier (String → Text, Double → Number or Currency
        /// per Definition's ForgeTypeId, Integer → Number). This is the
        /// most accurate source: it directly answers "is this parameter
        /// writable on a FabricationPart in this project?" — which is
        /// exactly what Pricing Sync needs.</item>
        /// <item><b>ParameterBindings sweep</b> — fallback for empty
        /// projects (no FabricationParts placed yet). Uses
        /// <see cref="ClassifyParameterType"/> on each Definition.</item>
        /// </list>
        /// Sample-probe results win on dedupe so the StorageType-derived
        /// label (more reliable across API versions) is preferred over
        /// Definition-based classification.
        /// </summary>
        private void LoadProjectParameters()
        {
            var byName = new Dictionary<string, ParamRow>(StringComparer.OrdinalIgnoreCase);

            // Source 1: probe a sample FabricationPart. Most reliable —
            // StorageType always works regardless of API version, and we
            // only show params that ACTUALLY appear on a FabricationPart.
            try
            {
                var sample = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FabricationPart))
                    .FirstElement();
                if (sample != null)
                {
                    foreach (Parameter p in sample.Parameters)
                    {
                        if (p == null || p.Definition == null) continue;
                        if (p.IsReadOnly) continue;

                        // Exclude built-in parameters via the InternalDefinition
                        // back-channel. User-mappable project + shared params
                        // have InternalDefinition.BuiltInParameter == INVALID.
                        bool isBuiltIn = false;
                        try
                        {
                            if (p.Definition is InternalDefinition idef)
                                isBuiltIn = idef.BuiltInParameter != BuiltInParameter.INVALID;
                        }
                        catch { }
                        if (isBuiltIn) continue;

                        string? label = ClassifyByStorageType(p);
                        if (label == null) continue;

                        string name = p.Definition.Name;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (!byName.ContainsKey(name))
                            byName[name] = new ParamRow { Name = name, TypeLabel = label };
                    }
                }
            }
            catch { }

            // Source 2: ParameterBindings iteration. Catches projects with
            // no FabricationParts yet (newly-applied template). Definition-
            // based classifier is best-effort — used only when source 1
            // didn't already populate the name.
            try
            {
                var bindings = _doc.ParameterBindings;
                var it = bindings.ForwardIterator();
                while (it.MoveNext())
                {
                    var def = it.Key as Definition;
                    if (def == null) continue;
                    string name = def.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (byName.ContainsKey(name)) continue;

                    string? label = ClassifyParameterType(def);
                    if (label == null) continue;
                    byName[name] = new ParamRow { Name = name, TypeLabel = label };
                }
            }
            catch { }

            // Build the final combo list — alphabetical, with explicit
            // "(none)" placeholder so users can unmap a slot.
            var sorted = byName.Values
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var withNone = new List<ParamRow>
            {
                new ParamRow { Name = "", TypeLabel = "none" },
            };
            withNone.AddRange(sorted);

            // Three separate binding sources so SelectedItem state stays
            // independent per combo.
            MaterialParamCombo.ItemsSource    = withNone;
            InstallParamCombo.ItemsSource     = withNone.ToList();
            FabricationParamCombo.ItemsSource = withNone.ToList();
        }

        /// <summary>
        /// Classifies a writable parameter by its <see cref="StorageType"/>
        /// (the bulletproof signal — works regardless of API version):
        /// <list type="bullet">
        /// <item>String → "Text"</item>
        /// <item>Double → "Currency" or "Number" (Currency wins if the
        /// Definition's spec TypeId resolves to <c>currency</c>; else
        /// "Number")</item>
        /// <item>Integer → "Number" (rare but valid for cost; sync's
        /// <c>SetIfWritable</c> handles Integer via Math.Round)</item>
        /// </list>
        /// Returns null for None / ElementId — those aren't valid M/E/F targets.
        /// </summary>
        private static string? ClassifyByStorageType(Parameter p)
        {
            return p.StorageType switch
            {
                StorageType.String  => "Text",
                StorageType.Double  => DistinguishCurrencyVsNumber(p.Definition),
                StorageType.Integer => "Number",
                _ => null,
            };
        }

        /// <summary>
        /// For a Double-storage parameter, distinguishes Currency from
        /// Number using the ForgeTypeId TypeId (Revit 2022+) or the
        /// legacy ParameterType enum. Defaults to "Number" when neither
        /// resolves — the param is still a valid numeric target either way.
        /// </summary>
        private static string DistinguishCurrencyVsNumber(Definition def)
        {
            try
            {
                var m = def.GetType().GetMethod("GetDataType");
                if (m != null)
                {
                    var dt = m.Invoke(def, null);
                    string tid = (dt?.ToString() ?? "").ToLowerInvariant();
                    // TypeId format in 2022+: "autodesk.spec.<discipline>:<name>-<version>".
                    // We match on the segment AFTER the colon to be
                    // discipline-agnostic (covers both "spec.aec:currency"
                    // and "spec.common:currency" etc.).
                    if (tid.Contains(":currency-") || tid.EndsWith(":currency"))
                        return "Currency";
                    if (tid.Contains(":number-") || tid.EndsWith(":number"))
                        return "Number";
                    // Other numeric specs (length, area, volume, …) are
                    // unit-bearing; we still report them as "Number" so
                    // the user can pick one if they really want — sync
                    // will write a raw double either way.
                }
            }
            catch { }
            try
            {
                var ptProp = def.GetType().GetProperty("ParameterType");
                if (ptProp != null)
                {
                    var pt = ptProp.GetValue(def)?.ToString() ?? "";
                    if (pt.Equals("Currency", StringComparison.OrdinalIgnoreCase))
                        return "Currency";
                }
            }
            catch { }
            return "Number";
        }

        /// <summary>
        /// Fallback classifier used when no FabricationPart exists in the
        /// project (empty model). Returns "Text"/"Number"/"Currency" for a
        /// Definition matching one of the three supported writeback types,
        /// or null when the parameter clearly isn't a valid Pricing Sync
        /// target (e.g. a length-spec parameter).
        /// </summary>
        private static string? ClassifyParameterType(Definition def)
        {
            // Path 1: ForgeTypeId via GetDataType() (Revit 2022+).
            try
            {
                var m = def.GetType().GetMethod("GetDataType");
                if (m != null)
                {
                    var dt = m.Invoke(def, null);
                    string tid = (dt?.ToString() ?? "").ToLowerInvariant();
                    if (!string.IsNullOrEmpty(tid))
                    {
                        // Match the name segment after the last colon — handles
                        // both 2022 ("autodesk.spec:spec.string-2.0.0") and
                        // 2024+ ("autodesk.spec.aec:string-2.0.0") layouts.
                        if (tid.Contains(":currency-") || tid.EndsWith(":currency"))
                            return "Currency";
                        if (tid.Contains(":number-") || tid.EndsWith(":number"))
                            return "Number";
                        if (tid.Contains(":string-") || tid.EndsWith(":string") ||
                            tid.Contains(":string."))
                            return "Text";
                        if (tid.Contains(":multilinetext-") || tid.Contains(":url-"))
                            return "Text";
                        // Anything else (length, area, etc.) — not a valid cost target.
                        return null;
                    }
                }
            }
            catch { }

            // Path 2: legacy ParameterType enum (pre-2022).
            try
            {
                var ptProp = def.GetType().GetProperty("ParameterType");
                if (ptProp != null)
                {
                    var pt = ptProp.GetValue(def)?.ToString() ?? "";
                    if (pt.Equals("Text", StringComparison.OrdinalIgnoreCase))     return "Text";
                    if (pt.Equals("Number", StringComparison.OrdinalIgnoreCase))   return "Number";
                    if (pt.Equals("Currency", StringComparison.OrdinalIgnoreCase)) return "Currency";
                }
            }
            catch { }
            return null;
        }

        private static void SetParamComboSelection(ComboBox combo, string savedName)
        {
            if (combo.ItemsSource is not IEnumerable<ParamRow> rows) return;
            var match = rows.FirstOrDefault(r => string.Equals(
                r.Name, savedName ?? "", StringComparison.OrdinalIgnoreCase));
            if (match != null) combo.SelectedItem = match;
        }

        // ── CSV list management ────────────────────────────────────────────────

        private void AddCsv_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter      = "Pricing files (*.csv;*.xlsx)|*.csv;*.xlsx|CSV (*.csv)|*.csv|All files (*.*)|*.*",
                Title       = "Select pricing file(s)",
                Multiselect = true,
            };
            if (ofd.ShowDialog(this) != true) return;

            foreach (var path in ofd.FileNames)
            {
                if (!CsvList.Items.Cast<string>().Contains(path,
                        StringComparer.OrdinalIgnoreCase))
                    CsvList.Items.Add(path);
            }
        }

        private void RemoveCsv_Click(object sender, RoutedEventArgs e)
        {
            var selected = CsvList.SelectedItems.Cast<object>().ToList();
            foreach (var item in selected) CsvList.Items.Remove(item);
        }

        // ── Save ───────────────────────────────────────────────────────────────

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var info = new PricingSourceInfo();

            if (UseFabDbCheck.IsChecked == true)
            {
                string folder = FolderPathBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    MessageBox.Show(this,
                        "Pick a valid Database folder, or uncheck the Fab Database option.",
                        "Pricing Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!File.Exists(Path.Combine(folder, "supplier.map")))
                {
                    var ok = MessageBox.Show(this,
                        "supplier.map not found in this folder. Pricing Sync will fail when run.\n\n" +
                        "Save the path anyway?",
                        "Pricing Setup", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (ok != MessageBoxResult.OK) return;
                }
                info.DatabaseFolder = folder;

                // Labour: combo Text is the source of truth (IsEditable=true,
                // so the user may have typed a custom name). If they picked a
                // row, the combo's Text mirrors the row's Name automatically.
                info.ErectionLabourTypeName    = (ErectionLabourCombo.Text    ?? "").Trim();
                info.FabricationLabourTypeName = (FabricationLabourCombo.Text ?? "").Trim();
                info.ErectionRatePerHour       = ParseRate(ErectionRateBox.Text);
                info.FabricationRatePerHour    = ParseRate(FabricationRateBox.Text);

                // If a name was entered but no rate, warn — sync would skip
                // that labour side silently otherwise.
                if (!string.IsNullOrEmpty(info.ErectionLabourTypeName) &&
                    info.ErectionRatePerHour <= 0)
                {
                    var ok = MessageBox.Show(this,
                        "Installation labor type is set but rate is 0. " +
                        "Pricing Sync will skip install labor.\n\nSave anyway?",
                        "Pricing Setup", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (ok != MessageBoxResult.OK) return;
                }
                if (!string.IsNullOrEmpty(info.FabricationLabourTypeName) &&
                    info.FabricationRatePerHour <= 0)
                {
                    var ok = MessageBox.Show(this,
                        "Fabrication labor type is set but rate is 0. " +
                        "Pricing Sync will skip fabrication labor.\n\nSave anyway?",
                        "Pricing Setup", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (ok != MessageBoxResult.OK) return;
                }
            }

            if (UseCsvCheck.IsChecked == true)
            {
                var paths = CsvList.Items.Cast<string>()
                    .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                if (paths.Count == 0)
                {
                    MessageBox.Show(this,
                        "Add at least one CSV file, or uncheck the CSV option.",
                        "Pricing Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var missing = paths.Where(p => !File.Exists(p)).ToList();
                if (missing.Count > 0)
                {
                    var ok = MessageBox.Show(this,
                        "The following files don't exist:\n\n" + string.Join("\n", missing) +
                        "\n\nSave anyway?",
                        "Pricing Setup", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (ok != MessageBoxResult.OK) return;
                }
                var xlsx = paths.Where(p => p.EndsWith(".xlsx",
                    StringComparison.OrdinalIgnoreCase)).ToList();
                if (xlsx.Count > 0)
                {
                    var ok = MessageBox.Show(this,
                        "Excel (.xlsx) files aren't supported yet — save them as CSV (UTF-8) " +
                        "and add the .csv versions instead.\n\nSave the .xlsx paths anyway?",
                        "Pricing Setup", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                    if (ok != MessageBoxResult.OK) return;
                }
                info.CsvFiles = paths;
            }

            info.IncludeLooseAncillaries = IncludeLooseAncillariesCheck.IsChecked == true;
            info.WriteLaborAsHours       = WriteLaborAsHoursCheck.IsChecked == true;

            // Revit parameter mapping — always saved (independent of source
            // selection). Empty selection = unmapped; Pricing Sync hard-fails
            // when it encounters a rate value but no target parameter.
            info.MaterialRateParamName    = (MaterialParamCombo.SelectedItem    as ParamRow)?.Name ?? "";
            info.InstallRateParamName     = (InstallParamCombo.SelectedItem     as ParamRow)?.Name ?? "";
            info.FabricationRateParamName = (FabricationParamCombo.SelectedItem as ParamRow)?.Name ?? "";

            // Estimate template. Empty string = no template, estimates
            // fall back to the default plain CSV / XLSX outputs.
            info.EstimateTemplatePath = (TemplatePathBox.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(info.EstimateTemplatePath) &&
                !File.Exists(info.EstimateTemplatePath))
            {
                var ok = MessageBox.Show(this,
                    $"Estimate template not found:\n{info.EstimateTemplatePath}\n\nSave anyway?",
                    "Pricing Setup", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (ok != MessageBoxResult.OK) return;
            }

            if (!info.IsConfigured)
            {
                MessageBox.Show(this,
                    "Enable at least one source (Fab Database or CSV) before saving.",
                    "Pricing Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                using var tx = new Transaction(_doc, "Save Pricing Setup");
                tx.Start();
                PricingSourceSchema.Write(_doc, info);
                tx.Commit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Save failed:\n{ex.Message}", "Pricing Setup",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Parses a $/hr textbox. Strips a leading "$" and any thousands
        /// separators; tolerates the user typing "30", "30.00", "$30.00",
        /// " 30 ", etc. Returns 0 when the field is blank or unparseable.
        /// </summary>
        private static double ParseRate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            string cleaned = text.Trim().TrimStart('$', ' ').Replace(",", "");
            return double.TryParse(cleaned, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double v) && v > 0
                ? v
                : 0;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
