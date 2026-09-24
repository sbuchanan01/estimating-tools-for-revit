using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using EstimatingTools.Revit.Zones;
using Sw = EstimatingTools.Revit.XlsxStyledWriter;

namespace EstimatingTools.UI
{
    /// <summary>
    /// Zone Estimate result — mirrors the Combined Estimate summary
    /// layout (Material total / Installation labor / Fabrication labor /
    /// Grand total) but repeated per zone. Includes an
    /// <b>Exclude Unassigned Parts</b> checkbox that drops the
    /// UNASSIGNED bucket from both the on-screen breakdown and the
    /// grand total. Toggling the checkbox also rewrites the XLSX file
    /// so what the user opens matches what they see.
    /// </summary>
    public partial class ZoneEstimateResultDialog : Window
    {
        private readonly ZoneRollupBuilder.ZoneReport _report;
        private readonly string _xlsxPath;

        public ZoneEstimateResultDialog(ZoneRollupBuilder.ZoneReport report,
                                        string xlsxPath)
        {
            InitializeComponent();
            Topmost = true;
            _report   = report;
            _xlsxPath = xlsxPath;
            PathText.Text = xlsxPath;
            Render();
        }

        private void Render()
        {
            bool excludeUnassigned = ExcludeUnassignedCheck.IsChecked == true;

            // Zones we're including in the current view. UNASSIGNED filters
            // out on request; MULTIPLE always stays visible (represents
            // real parts that need attention, not "no zone found").
            var zones = _report.Zones
                .Where(z => !excludeUnassigned ||
                            z.ZoneName != ZoneRollupBuilder.UnassignedZone)
                .ToList();

            bool unassignedHidden = excludeUnassigned &&
                _report.Zones.Any(z => z.ZoneName == ZoneRollupBuilder.UnassignedZone);
            RenderCards(_report, zones, unassignedHidden);

            // Rewrite the XLSX so "Open XLSX" reflects the current
            // checkbox state. Pushing the file on every toggle is cheaper
            // than reserving a second path; opening it in Excel will
            // pick up the latest version.
            try { WriteXlsx(_xlsxPath, _report, zones); }
            catch { /* best-effort — dialog still shows the on-screen totals */ }
        }

        private void ExcludeUnassigned_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            Render();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_xlsxPath)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not open '{_xlsxPath}':\n{ex.Message}",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ── On-screen cards — one colored card per zone + an all-zones total ──

        private void RenderCards(ZoneRollupBuilder.ZoneReport report,
            System.Collections.Generic.List<ZoneRollupBuilder.ZoneBucket> zones,
            bool unassignedHidden)
        {
            InfoText.Text =
                $"Source: {report.SourceDescription}   ·   " +
                $"{report.PartsScanned} Fabrication parts scanned" +
                (report.PartsSkippedNoPrice > 0
                    ? $"   ·   {report.PartsSkippedNoPrice} skipped (no price)"
                    : "");

            var cards = new System.Collections.Generic.List<EstimateCard>();
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                var rows = Rows(report.HasLabor, z.MaterialCost, z.InstallLaborCost, z.FabLaborCost);
                // UNASSIGNED / MULTIPLE get an amber band so they read as
                // "needs attention" rather than as another real zone.
                bool flagged = z.ZoneName == ZoneRollupBuilder.UnassignedZone ||
                               z.ZoneName == ZoneRollupBuilder.MultipleZone;
                cards.Add(flagged
                    ? EstimateCards.Flag(z.ZoneName, rows, "Zone total", z.Total)
                    : EstimateCards.Section($"Zone {z.ZoneName}", i, rows, "Zone total", z.Total));
            }
            ZoneCards.ItemsSource = cards;

            string scope = $"{zones.Count} zone{(zones.Count == 1 ? "" : "s")}" +
                (unassignedHidden ? ", UNASSIGNED excluded" : "");
            TotalCard.DataContext = EstimateCards.Total(
                $"All zones — total ({scope})",
                Rows(report.HasLabor,
                    zones.Sum(z => z.MaterialCost),
                    zones.Sum(z => z.InstallLaborCost),
                    zones.Sum(z => z.FabLaborCost)),
                "Grand total",
                zones.Sum(z => z.Total));

            int lineCount = zones.Sum(z => z.Disciplines.Sum(d => d.Lines.Count));
            FootText.Text = $"Unique material line items: {lineCount}";
        }

        private static System.Collections.Generic.List<(string, double)> Rows(
            bool hasLabor, double material, double install, double fab)
        {
            var rows = new System.Collections.Generic.List<(string, double)> { ("Material", material) };
            if (hasLabor)
            {
                rows.Add(("Installation labor", install));
                rows.Add(("Fabrication labor", fab));
            }
            return rows;
        }

        // ── XLSX writer — Summary sheet with shading + borders ───────

        private static void WriteXlsx(string path,
            ZoneRollupBuilder.ZoneReport report,
            System.Collections.Generic.List<ZoneRollupBuilder.ZoneBucket> zones)
        {
            var rows = new System.Collections.Generic.List<
                System.Collections.Generic.IList<Sw.Cell>>();

            // Summary table columns depend on whether labour is on.
            // With labour:    Zone | Material | Install | Fabrication | Total
            // Without:        Zone | Material                          | Total
            bool hasLabor = report.HasLabor;
            int columnCount = hasLabor ? 5 : 3;

            // Report banner — one merged row across every column, tinted
            // like a zone-name row so it doesn't blend into the header.
            rows.Add(new System.Collections.Generic.List<Sw.Cell>
            {
                new Sw.Cell("Zone Estimate", Sw.CellStyle.ZoneName, columnCount),
            });
            rows.Add(EmptyRow(columnCount));

            // Source / diagnostic lines — muted, span full width.
            rows.Add(OneCellRow(
                $"Source: {report.SourceDescription}", Sw.CellStyle.Data, columnCount));
            rows.Add(OneCellRow(
                $"Fabrication parts scanned: {report.PartsScanned}",
                Sw.CellStyle.Data, columnCount));
            if (report.PartsSkippedNoPrice > 0)
                rows.Add(OneCellRow(
                    $"Parts skipped (no price): {report.PartsSkippedNoPrice}",
                    Sw.CellStyle.Data, columnCount));
            rows.Add(EmptyRow(columnCount));

            // Header row — dark navy, white text.
            var header = new System.Collections.Generic.List<Sw.Cell>
            {
                new Sw.Cell("Zone", Sw.CellStyle.Header),
                new Sw.Cell("Material", Sw.CellStyle.Header),
            };
            if (hasLabor)
            {
                header.Add(new Sw.Cell("Installation labor", Sw.CellStyle.Header));
                header.Add(new Sw.Cell("Fabrication labor", Sw.CellStyle.Header));
            }
            header.Add(new Sw.Cell("Zone total", Sw.CellStyle.Header));
            rows.Add(header);

            // Data rows — one per zone, alternating light shading so the
            // eye can scan across a wide row without losing its place.
            double gMat = 0, gInst = 0, gFab = 0, gTotal = 0;
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                bool zebra = (i % 2) == 1;
                var money = zebra ? Sw.CellStyle.MoneyZebra : Sw.CellStyle.Money;
                var data  = zebra ? Sw.CellStyle.DataZebra  : Sw.CellStyle.Data;
                var row = new System.Collections.Generic.List<Sw.Cell>
                {
                    new Sw.Cell(z.ZoneName, data),
                    new Sw.Cell(z.MaterialCost, money),
                };
                if (hasLabor)
                {
                    row.Add(new Sw.Cell(z.InstallLaborCost, money));
                    row.Add(new Sw.Cell(z.FabLaborCost, money));
                }
                row.Add(new Sw.Cell(z.Total, Sw.CellStyle.MoneyBold));
                rows.Add(row);

                gMat   += z.MaterialCost;
                gInst  += z.InstallLaborCost;
                gFab   += z.FabLaborCost;
                gTotal += z.Total;
            }

            // Grand total — inverse header style so it visually anchors
            // the bottom of the summary block.
            var grand = new System.Collections.Generic.List<Sw.Cell>
            {
                new Sw.Cell("GRAND TOTAL", Sw.CellStyle.GrandTotalLabel),
                new Sw.Cell(gMat, Sw.CellStyle.GrandTotalMoney),
            };
            if (hasLabor)
            {
                grand.Add(new Sw.Cell(gInst, Sw.CellStyle.GrandTotalMoney));
                grand.Add(new Sw.Cell(gFab, Sw.CellStyle.GrandTotalMoney));
            }
            grand.Add(new Sw.Cell(gTotal, Sw.CellStyle.GrandTotalMoney));
            rows.Add(grand);

            rows.Add(EmptyRow(columnCount));
            rows.Add(EmptyRow(columnCount));

            // ── Material detail section ─────────────────────────────
            // Same look as the summary; scrolls below it. Helpful for
            // auditing which specific product codes drove each zone total.
            rows.Add(new System.Collections.Generic.List<Sw.Cell>
            {
                new Sw.Cell("Material detail — every priced line",
                    Sw.CellStyle.ZoneName, 8),
            });
            rows.Add(EmptyRow(8));
            rows.Add(new System.Collections.Generic.List<Sw.Cell>
            {
                new Sw.Cell("Zone",         Sw.CellStyle.Header),
                new Sw.Cell("Discipline",   Sw.CellStyle.Header),
                new Sw.Cell("Product Code", Sw.CellStyle.Header),
                new Sw.Cell("Description",  Sw.CellStyle.Header),
                new Sw.Cell("Quantity",     Sw.CellStyle.Header),
                new Sw.Cell("Unit",         Sw.CellStyle.Header),
                new Sw.Cell("Unit Price",   Sw.CellStyle.Header),
                new Sw.Cell("Total",        Sw.CellStyle.Header),
            });
            int lineIdx = 0;
            foreach (var zone in zones)
            {
                foreach (var disc in zone.Disciplines
                    .OrderBy(d => DisciplineOrder(d.Name)))
                {
                    foreach (var line in disc.Lines
                        .OrderBy(l => (int)l.Kind)
                        .ThenBy(l => l.ProductCode,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        bool zebra = (lineIdx++ % 2) == 1;
                        var money = zebra ? Sw.CellStyle.MoneyZebra : Sw.CellStyle.Money;
                        var data  = zebra ? Sw.CellStyle.DataZebra  : Sw.CellStyle.Data;
                        rows.Add(new System.Collections.Generic.List<Sw.Cell>
                        {
                            new Sw.Cell(zone.ZoneName,     data),
                            new Sw.Cell(disc.Name,         data),
                            new Sw.Cell(line.ProductCode,  data),
                            new Sw.Cell(ComposeDescription(line), data),
                            new Sw.Cell(line.Quantity,     money),
                            new Sw.Cell(line.Unit,         data),
                            new Sw.Cell(line.UnitPrice,    money),
                            new Sw.Cell(line.Total,        money),
                        });
                    }
                }
            }

            var widths = hasLabor
                ? new System.Collections.Generic.List<Sw.ColumnWidth>
                {
                    new Sw.ColumnWidth(28),  // Zone
                    new Sw.ColumnWidth(16),  // Material
                    new Sw.ColumnWidth(18),  // Install labor
                    new Sw.ColumnWidth(18),  // Fab labor
                    new Sw.ColumnWidth(16),  // Total
                }
                : new System.Collections.Generic.List<Sw.ColumnWidth>
                {
                    new Sw.ColumnWidth(28),
                    new Sw.ColumnWidth(16),
                    new Sw.ColumnWidth(16),
                };

            Sw.Write(path, "Zone Estimate", widths, rows);
        }

        // Helpers to keep the row-building above readable.
        private static System.Collections.Generic.IList<
            EstimatingTools.Revit.XlsxStyledWriter.Cell>
            EmptyRow(int span)
        {
            return new System.Collections.Generic.List<
                EstimatingTools.Revit.XlsxStyledWriter.Cell>();
        }

        private static System.Collections.Generic.IList<
            EstimatingTools.Revit.XlsxStyledWriter.Cell>
            OneCellRow(string text,
                EstimatingTools.Revit.XlsxStyledWriter.CellStyle style,
                int span)
        {
            return new System.Collections.Generic.List<
                EstimatingTools.Revit.XlsxStyledWriter.Cell>
            {
                new EstimatingTools.Revit.XlsxStyledWriter.Cell(text, style, span),
            };
        }

        private static string ComposeDescription(ZoneRollupBuilder.ZoneLine l)
        {
            string size   = l.Size?.Trim() ?? "";
            string family = l.FamilyName?.Trim() ?? "";
            if (!string.IsNullOrEmpty(size) && !string.IsNullOrEmpty(family))
                return $"{size} {family}";
            if (!string.IsNullOrEmpty(family)) return family;
            if (!string.IsNullOrEmpty(size))   return size;
            return l.Description ?? "";
        }

        private static int DisciplineOrder(string name) => name switch
        {
            "Piping"   => 0,
            "Ductwork" => 1,
            "Hangers"  => 2,
            _          => 99,
        };
    }
}
