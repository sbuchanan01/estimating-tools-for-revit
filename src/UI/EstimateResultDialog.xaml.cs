using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace EstimatingTools.UI
{
    /// <summary>
    /// Shared modal result dialog for the estimate commands (Material /
    /// Labor / Material + Labor). Card mode shows one colored card per
    /// section with the overall total pinned underneath, matching the
    /// Zone Estimate dialog. Text mode shows a monospaced body for plain
    /// logs.
    ///
    /// One "Open" button is wired to <see cref="_outputPath"/>; the
    /// button label is set per-call (e.g. "Open CSV" / "Open XLSX").
    /// </summary>
    public partial class EstimateResultDialog : Window
    {
        private readonly string _outputPath;

        /// <summary>Text mode: <paramref name="body"/> in a monospaced box.</summary>
        public EstimateResultDialog(string title, string heading, string body,
                                    string outputPath, string openButtonLabel)
            : this(title, heading, outputPath, openButtonLabel)
        {
            Width = 640;
            BodyText.Text = body;
            TextBody.Visibility = Visibility.Visible;
        }

        /// <summary>Card mode: <paramref name="sections"/> scroll, the
        /// <paramref name="total"/> card stays pinned below them.</summary>
        public EstimateResultDialog(string title, string heading, string info,
                                    IReadOnlyList<EstimateCard> sections,
                                    EstimateCard total,
                                    IEnumerable<string> notes,
                                    IEnumerable<string> warnings,
                                    string outputPath, string openButtonLabel)
            : this(title, heading, outputPath, openButtonLabel)
        {
            SetText(InfoText, info);
            SectionCards.ItemsSource = sections;
            CardScroll.Visibility = Visibility.Visible;
            TotalCard.DataContext = total;
            TotalCard.Visibility = Visibility.Visible;
            SetText(FootText, string.Join("\n", notes));
            SetText(WarningText, string.Join("\n", warnings.Select(w => "⚠ " + w)));
        }

        private EstimateResultDialog(string title, string heading,
                                     string outputPath, string openButtonLabel)
        {
            InitializeComponent();
            Topmost = true;
            Title              = title;
            HeadingText.Text   = heading;
            _outputPath        = outputPath;
            OpenButton.Content = openButtonLabel;
            PathText.Text      = outputPath;
        }

        private static void SetText(System.Windows.Controls.TextBlock tb, string text)
        {
            tb.Text = text;
            tb.Visibility = string.IsNullOrWhiteSpace(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_outputPath)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Could not open '{_outputPath}':\n{ex.Message}",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
