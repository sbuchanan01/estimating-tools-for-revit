using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using EstimatingTools.Revit;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EstimatingTools.UI
{
    /// <summary>
    /// Modeless cost-breakdown window. Shows the hierarchical tree (with
    /// collapsible Material / Fabrication / Installation sections) for
    /// every part the user picks or has selected, plus a Save Report button
    /// that writes the same content as a flat TXT file. All sections start
    /// collapsed — Item-level totals are visible on launch, drill in for
    /// detail.
    /// </summary>
    public partial class CostBreakdownDialog : Window
    {
        private readonly UIDocument _uiDoc;
        private readonly CostBreakdownContext _ctx;
        // Element-typed so the list can hold FabricationParts AND
        // native RFA FamilyInstances (pipe-accessory valves) without
        // forcing a parallel list. Rebuild dispatches by type.
        private List<Element> _parts;
        private readonly ObservableCollection<CostNode> _rootNodes = new();

        public CostBreakdownDialog(UIDocument uiDoc,
                                   CostBreakdownContext ctx,
                                   List<Element> initialParts)
        {
            InitializeComponent();
            _uiDoc = uiDoc;
            _ctx   = ctx;
            _parts = initialParts ?? new List<Element>();

            ResultsTree.ItemsSource = _rootNodes;

            var context = new List<string>
            {
                $"Pricing source: {ctx.SupplierLabel}",
                ctx.InstallRate > 0
                    ? $"Installation labor: {ctx.InstallLabourName} @ ${ctx.InstallRate:0.##}/hr"
                    : "Installation labor: not configured",
                ctx.FabRate > 0
                    ? $"Fabrication labor: {ctx.FabLabourName} @ ${ctx.FabRate:0.##}/hr"
                    : "Fabrication labor: not configured",
            };
            SourceText.Text = string.Join("   ·   ", context);
            ItmText.Text = ctx.ItmStatus;
            ItmText.Visibility = string.IsNullOrWhiteSpace(ctx.ItmStatus)
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;

            Rebuild();
        }

        // ── Rebuild tree ────────────────────────────────────────────────────

        private void Rebuild()
        {
            _rootNodes.Clear();

            if (_parts.Count == 0)
            {
                StatusText.Text = "No parts selected. Click \"Pick Parts in Revit\" " +
                                  "or select parts in the model then click Refresh.";
                TotalLabel.Text = "Grand total";
                TotalValue.Text = EstimateCards.Money(0);
                return;
            }

            double grand = 0;
            for (int i = 0; i < _parts.Count; i++)
            {
                PartBreakdown br;
                if (_parts[i] is FabricationPart fp)
                    br = CostBreakdownCommand.ComputeBreakdown(fp, _ctx);
                else if (_parts[i] is FamilyInstance fi)
                    br = CostBreakdownCommand.ComputeBreakdownForRfa(fi, _ctx);
                else
                    continue;
                var node = BuildPartNode(br, i + 1, _parts.Count);
                SetLevels(node, 0);
                _rootNodes.Add(node);
                grand += br.GrandTotal;
            }

            StatusText.Text = $"{_parts.Count} part(s) — click a row to drill into material and labor.";
            TotalLabel.Text = _parts.Count == 1
                ? "Item total"
                : $"All items — total ({_parts.Count} items)";
            TotalValue.Text = EstimateCards.Money(grand);
        }

        private static void SetLevels(CostNode node, int level)
        {
            node.Level = level;
            foreach (var c in node.Children) SetLevels(c, level + 1);
        }

        // ── Tree-node builder ───────────────────────────────────────────────

        private static CostNode BuildPartNode(PartBreakdown br, int index, int total)
        {
            var item = new CostNode
            {
                Kind   = CostRowKind.Item,
                Label  = $"Item {index} of {total}   ·   {br.HeaderLabel}",
                Amount = EstimateCards.Money(br.GrandTotal),
            };

            // Identification block — leaves under the item header.
            item.Children.Add(InfoRow("Product Code", br.ProductCode));
            item.Children.Add(InfoRow("ElementId", $"{br.ElementId}"));
            string size = br.IsDuct && !string.IsNullOrEmpty(br.SizeLabel)
                ? br.SizeLabel
                : $"{br.SizeIn:0.##}\"";
            if (br.IsPipe)
            {
                item.Children.Add(InfoRow("Geometry", "Pipe"));
                item.Children.Add(InfoRow("Size", size));
                item.Children.Add(InfoRow("Length", $"{br.LengthFt:0.##} ft"));
            }
            else if (br.IsDuct && br.LengthFt > 0)
            {
                item.Children.Add(InfoRow("Geometry", "Duct"));
                item.Children.Add(InfoRow("Size", size));
                item.Children.Add(InfoRow("Length", $"{br.LengthFt:0.##} ft"));
            }
            else if (br.IsDuct)
            {
                item.Children.Add(InfoRow("Geometry", "Duct fitting"));
                item.Children.Add(InfoRow("Size", size));
            }
            else
            {
                item.Children.Add(InfoRow("Geometry", "Fitting / valve / hanger"));
                item.Children.Add(InfoRow("Size", size));
            }

            item.Children.Add(BuildMaterialNode(br));
            item.Children.Add(BuildLabourNode(br,
                heading: "Fabrication cost",
                labourName: br.FabLabourName, rate: br.FabRate,
                lines: br.Fabrication, sectionTotal: br.FabTotal,
                isFabrication: true));
            item.Children.Add(BuildLabourNode(br,
                heading: "Installation cost",
                labourName: br.InstallLabourName, rate: br.InstallRate,
                lines: br.Installation, sectionTotal: br.InstallTotal,
                isFabrication: false));

            item.Children.Add(TotalRow("Total unit cost", br.GrandTotal, "per qty"));
            item.Children.Add(new CostNode
            {
                Kind   = CostRowKind.Total,
                NoRule = true,
                Label  = "Gross item extension",
                Amount = EstimateCards.Money(br.GrandTotal),
            });
            return item;
        }

        private static CostNode BuildMaterialNode(PartBreakdown br)
        {
            // Per-foot rendering applies whenever the part has positive
            // length (curve-based pipe OR curve-based duct). Duct
            // fittings (LocationPoint, LengthFt == 0) render per-piece
            // like any other fitting.
            bool perFootMaterial = br.LengthFt > 0;
            var section = new CostNode
            {
                Kind   = CostRowKind.Section,
                Label  = "Material costs",
                Detail = perFootMaterial ? "extended for length" : "per qty",
                Amount = EstimateCards.Money(br.MaterialExtended),
            };

            var ancParent = new CostNode
            {
                Kind   = CostRowKind.Group,
                Label  = "Ancillary cost",
                Detail = "per qty",
                Amount = EstimateCards.Money(br.AncillaryTotal),
            };
            foreach (var a in br.AncillaryDetail)
                ancParent.Children.Add(BuildAncillaryNode(a));

            if (perFootMaterial)
            {
                // Base price is per-foot; ancillary kits (joints, fixings)
                // are per-piece. Earlier code multiplied ancillaries by
                // length too, which over-counted by 5–6× on multi-foot
                // pieces. Now matches Fab UI: only the base scales by length.
                double baseExt = br.BaseListPerUnit * br.LengthFt;
                section.Children.Add(LineRow("Base price",
                    $"${br.BaseListPerUnit:N4}/ft × {br.LengthFt:0.##} ft", baseExt));
                section.Children.Add(ancParent);
                section.Children.Add(LineRow("Insulation cost", "", 0));
            }
            else
            {
                section.Children.Add(LineRow("Price list cost", "per qty", br.BaseListPerUnit));
                section.Children.Add(ancParent);
                section.Children.Add(LineRow("Insulation cost", "per qty", 0));
            }
            section.Children.Add(TotalRow("Total", br.MaterialExtended));
            return section;
        }

        private static CostNode BuildLabourNode(PartBreakdown br,
            string heading, string labourName, double rate,
            List<LabourTableLine> lines, double sectionTotal,
            bool isFabrication)
        {
            var section = new CostNode
            {
                Kind   = CostRowKind.Section,
                Label  = heading,
                Detail = rate > 0 && !string.IsNullOrEmpty(labourName)
                    ? $"{labourName} @ ${rate:0.##}/hr" : "",
                Amount = EstimateCards.Money(sectionTotal),
            };

            if (rate <= 0)
            {
                section.Children.Add(NoteRow("Labor type not configured in Pricing Setup"));
                return section;
            }
            if (lines.Count == 0)
            {
                section.Children.Add(NoteRow("No matching labor tables on this part"));
                return section;
            }

            foreach (var line in lines)
            {
                string detail;
                // Pipe FAB is per-pipe (one joint/setup cycle, no
                // length scaling — see ComputePipeMinutesByTable's
                // perPipe branch). Anything else with positive length
                // is per-foot. Zero length (fittings, including duct
                // fittings) → per-piece.
                if (br.IsPipe && isFabrication)
                {
                    detail = $"{line.TotalMinutes:0.##} mins / pipe";
                }
                else if (br.LengthFt > 0)
                {
                    double minsPerFt = line.TotalMinutes / br.LengthFt;
                    detail = $"{minsPerFt:0.##} mins/ft × {br.LengthFt:0.##} ft = {line.TotalMinutes:0.##} mins";
                }
                else
                {
                    detail = $"{line.TotalMinutes:0.##} mins total";
                }
                section.Children.Add(LineRow(line.TableName, detail, line.Cost));
            }

            section.Children.Add(TotalRow("Subtotal", sectionTotal));
            return section;
        }

        private static CostNode InfoRow(string label, string value) =>
            new() { Kind = CostRowKind.Info, Label = label, Detail = value };

        private static CostNode LineRow(string label, string detail, double amount) =>
            new() { Kind = CostRowKind.Line, Label = label, Detail = detail,
                    Amount = EstimateCards.Money(amount) };

        private static CostNode TotalRow(string label, double amount, string detail = "") =>
            new() { Kind = CostRowKind.Total, Label = label, Detail = detail,
                    Amount = EstimateCards.Money(amount) };

        private static CostNode NoteRow(string text) =>
            new() { Kind = CostRowKind.Note, Label = text };

        /// <summary>
        /// Recursive renderer for ancillary lines. Kit headers become
        /// expandable parent nodes with their fixings nested
        /// underneath; flat lines render as a single leaf. Mirrors the
        /// Fab UI's "Kit (Flange Kit - …)" → "Bolt / Nut / Gasket"
        /// hierarchy.
        /// </summary>
        private static CostNode BuildAncillaryNode(AncillaryLine a)
        {
            if (a.IsKitHeader)
            {
                var node = new CostNode
                {
                    Kind       = CostRowKind.Group,
                    Label      = $"Kit: {a.Name}",
                    Detail     = "per qty",
                    Amount     = EstimateCards.Money(a.DisplayTotal),
                    IsExpanded = true,
                };
                foreach (var child in a.Children)
                    node.Children.Add(BuildAncillaryNode(child));
                return node;
            }
            return LineRow(a.Name, $"${a.UnitPrice:N4} × {a.Quantity:0.##}", a.Total);
        }

        // ── Pick / Refresh / Save buttons ───────────────────────────────────

        private void PickParts_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            EstimatingToolsApp.PickHandler!.SetAction(uiApp =>
            {
                List<Element> picked = new();
                try
                {
                    var uiDoc = uiApp.ActiveUIDocument;
                    var refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        new CostablePartFilter(),
                        "Pick fabrication parts or pipe accessories to break down. " +
                        "Press Finish or Esc when done.");
                    foreach (var r in refs)
                    {
                        var el = uiDoc.Document.GetElement(r);
                        if (el is FabricationPart || el is FamilyInstance)
                            picked.Add(el);
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => StatusText.Text = $"Pick failed: {ex.Message}");
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (picked.Count > 0)
                        {
                            _parts = picked;
                            Rebuild();
                        }
                        Show();
                        Activate();
                    });
                }
            });
            EstimatingToolsApp.PickEvent!.Raise();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _parts = _uiDoc.Selection.GetElementIds()
                .Select(id => _uiDoc.Document.GetElement(id))
                .Where(e => e is FabricationPart ||
                            (e is FamilyInstance fi
                             && fi.Category?.Id?.Value ==
                                (long)BuiltInCategory.OST_PipeAccessory))
                .ToList();
            Rebuild();
        }

        private void SaveReport_Click(object sender, RoutedEventArgs e)
        {
            if (_parts.Count == 0)
            {
                MessageBox.Show(this,
                    "No parts to save. Pick or select parts first.",
                    "Cost Breakdown", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "Save cost-breakdown report",
                Filter   = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"CostBreakdown_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            };
            if (sfd.ShowDialog(this) != true) return;

            try
            {
                string body = CostBreakdownCommand.WriteTextReport(_parts, _ctx);
                File.WriteAllText(sfd.FileName, body, Encoding.UTF8);
                var openIt = MessageBox.Show(this,
                    $"Saved to:\n{sfd.FileName}\n\nOpen now?",
                    "Cost Breakdown", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (openIt == MessageBoxResult.Yes)
                {
                    try { Process.Start(new ProcessStartInfo(sfd.FileName) { UseShellExecute = true }); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"Could not open file:\n{ex.Message}",
                            "Cost Breakdown", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Save failed:\n{ex.Message}",
                    "Cost Breakdown", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
            => SetExpansion(_rootNodes, expanded: true);

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
            => SetExpansion(_rootNodes, expanded: false);

        private static void SetExpansion(IEnumerable<CostNode> nodes, bool expanded)
        {
            foreach (var n in nodes)
            {
                n.IsExpanded = expanded;
                SetExpansion(n.Children, expanded);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ── Pick filter ─────────────────────────────────────────────────────

        // Allows FabricationParts (the original surface) AND RFA
        // pipe-accessory FamilyInstances (the new surface — valves the
        // estimator placed as native Revit content with a Product Code
        // type parameter populated).
        private sealed class CostablePartFilter : ISelectionFilter
        {
            public bool AllowElement(Element e)
            {
                if (e is FabricationPart) return true;
                if (e is FamilyInstance fi)
                {
                    return fi.Category?.Id?.Value ==
                           (long)BuiltInCategory.OST_PipeAccessory;
                }
                return false;
            }
            public bool AllowReference(Reference r, XYZ pt) => true;
        }
    }

    // ── Tree-node view model ────────────────────────────────────────────────

    /// <summary>How a <see cref="CostNode"/> row is styled.</summary>
    public enum CostRowKind { Item, Section, Group, Line, Info, Total, Note }

    /// <summary>
    /// One row in the cost-breakdown TreeView: label, muted detail, and a
    /// right-aligned amount. Styling derives from <see cref="Kind"/> using
    /// the same palette as the estimate result cards. IsExpanded is two-way
    /// bound so Expand-All / Collapse-All drive the tree directly.
    /// </summary>
    public sealed class CostNode : System.ComponentModel.INotifyPropertyChanged
    {
        private static readonly Brush ItemBg    = Hex("#DCE8F2");
        private static readonly Brush SectionBg = Hex("#F4F8FB");
        private static readonly Brush HeaderFg  = Hex("#1F4E79");
        private static readonly Brush TotalFg   = Hex("#1F2A38");
        private static readonly Brush BodyFg    = Hex("#333B44");
        private static readonly Brush MutedFg   = Hex("#6B7580");

        private bool _isExpanded;

        public CostRowKind Kind { get; init; } = CostRowKind.Line;
        public string Label { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Amount { get; init; } = "";
        /// <summary>Total row without the rule above it (second of a pair).</summary>
        public bool NoRule { get; init; }
        /// <summary>Depth in the tree; drives indentation. Set before binding.</summary>
        public int Level { get; set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnChanged(nameof(IsExpanded)); }
        }
        public ObservableCollection<CostNode> Children { get; } = new();

        public Brush RowBackground => Kind switch
        {
            CostRowKind.Item    => ItemBg,
            CostRowKind.Section => SectionBg,
            _                   => Brushes.Transparent,
        };
        public CornerRadius RowCorner =>
            Kind == CostRowKind.Item ? new CornerRadius(3) : new CornerRadius(0);
        public Thickness RowMargin => Kind switch
        {
            CostRowKind.Item    => new Thickness(0, 6, 0, 0),
            CostRowKind.Section => new Thickness(0, 2, 0, 0),
            _                   => new Thickness(0),
        };
        public Thickness RowPadding => Kind switch
        {
            CostRowKind.Item    => new Thickness(0, 6, 0, 6),
            CostRowKind.Section => new Thickness(0, 4, 0, 4),
            _                   => new Thickness(0, 2, 0, 2),
        };
        public Thickness IndentMargin => new(8 + Level * 20, 0, 10, 0);

        public Brush LabelForeground => Kind switch
        {
            CostRowKind.Item or CostRowKind.Section => HeaderFg,
            CostRowKind.Total                       => TotalFg,
            CostRowKind.Info or CostRowKind.Note    => MutedFg,
            _                                       => BodyFg,
        };
        public Brush DetailForeground => Kind == CostRowKind.Info ? BodyFg : MutedFg;
        public FontWeight LabelWeight =>
            Kind is CostRowKind.Item or CostRowKind.Section or CostRowKind.Total
                ? FontWeights.SemiBold
                : FontWeights.Normal;
        public double LabelSize => Kind == CostRowKind.Item ? 13 : 12;
        public FontStyle LabelStyle =>
            Kind == CostRowKind.Note ? FontStyles.Italic : FontStyles.Normal;
        public Thickness RuleThickness =>
            Kind == CostRowKind.Total && !NoRule ? new Thickness(0, 1, 0, 0) : new Thickness(0);
        public Thickness RulePadding =>
            Kind == CostRowKind.Total && !NoRule ? new Thickness(0, 4, 0, 0) : new Thickness(0);

        private static Brush Hex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            b.Freeze();
            return b;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) =>
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
