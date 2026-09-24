using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace EstimatingTools.UI
{
    /// <summary>One label + amount line inside an <see cref="EstimateCard"/>.
    /// IsTotal rows render bold under a rule.</summary>
    public sealed class EstimateRow
    {
        public string Label { get; init; } = "";
        public string Amount { get; init; } = "";
        public bool IsTotal { get; init; }
    }

    /// <summary>A colored card in the estimate result dialogs: header band
    /// + rows. Build through <see cref="EstimateCards"/> so every dialog
    /// uses the same palette.</summary>
    public sealed class EstimateCard
    {
        public string Title { get; init; } = "";
        public Brush HeaderBackground { get; init; } = Brushes.White;
        public Brush HeaderForeground { get; init; } = Brushes.Black;
        public Brush BodyBackground { get; init; } = Brushes.White;
        public List<EstimateRow> Rows { get; init; } = new();
    }

    /// <summary>Card factory + palette shared by Material, Labor,
    /// Material + Labor and Zone estimate dialogs.</summary>
    public static class EstimateCards
    {
        private static readonly Brush SectionHeaderBg = Hex("#DCE8F2");
        private static readonly Brush SectionHeaderFg = Hex("#1F4E79");
        private static readonly Brush SectionBodyA    = Hex("#FFFFFF");
        private static readonly Brush SectionBodyB    = Hex("#F4F8FB");
        private static readonly Brush FlagHeaderBg    = Hex("#FBE9CC");
        private static readonly Brush FlagHeaderFg    = Hex("#7A4B00");
        private static readonly Brush FlagBody        = Hex("#FFFBF3");
        private static readonly Brush TotalHeaderBg   = Hex("#1F2A38");
        private static readonly Brush TotalHeaderFg   = Hex("#FFFFFF");
        private static readonly Brush TotalBody       = Hex("#E9EEF3");

        public static string Money(double v) =>
            v.ToString("$#,##0.00", CultureInfo.InvariantCulture);

        /// <summary>Blue-banded card; <paramref name="index"/> alternates
        /// the body tint so neighbouring cards separate.</summary>
        public static EstimateCard Section(string title, int index,
            IEnumerable<(string Label, double Amount)> rows,
            string totalLabel, double total) => new()
        {
            Title = title,
            HeaderBackground = SectionHeaderBg,
            HeaderForeground = SectionHeaderFg,
            BodyBackground = index % 2 == 0 ? SectionBodyA : SectionBodyB,
            Rows = BuildRows(rows, totalLabel, total),
        };

        /// <summary>Amber card for buckets that need attention
        /// (UNASSIGNED / MULTIPLE zones).</summary>
        public static EstimateCard Flag(string title,
            IEnumerable<(string Label, double Amount)> rows,
            string totalLabel, double total) => new()
        {
            Title = title,
            HeaderBackground = FlagHeaderBg,
            HeaderForeground = FlagHeaderFg,
            BodyBackground = FlagBody,
            Rows = BuildRows(rows, totalLabel, total),
        };

        /// <summary>Dark navy card for the overall total, pinned under
        /// the section list.</summary>
        public static EstimateCard Total(string title,
            IEnumerable<(string Label, double Amount)> rows,
            string totalLabel, double total) => new()
        {
            Title = title,
            HeaderBackground = TotalHeaderBg,
            HeaderForeground = TotalHeaderFg,
            BodyBackground = TotalBody,
            Rows = BuildRows(rows, totalLabel, total),
        };

        private static List<EstimateRow> BuildRows(
            IEnumerable<(string Label, double Amount)> rows,
            string totalLabel, double total)
        {
            var list = rows
                .Select(r => new EstimateRow { Label = r.Label, Amount = Money(r.Amount) })
                .ToList();
            list.Add(new EstimateRow { Label = totalLabel, Amount = Money(total), IsTotal = true });
            return list;
        }

        private static Brush Hex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            b.Freeze();
            return b;
        }
    }
}
