using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Runtime icon generator for ribbon buttons. Uses
    /// <see cref="DrawingVisual"/> + <see cref="RenderTargetBitmap"/>
    /// to render glyphs into 16/32 px ImageSources at startup — no bitmap
    /// files ship alongside the DLL and the icons stay crisp at both
    /// ribbon sizes.
    ///
    /// Glyph source: <b>Segoe MDL2 Assets</b> (bundled with Windows 10+).
    /// Glyphs are written as \u escapes because they live in the Unicode
    /// private-use area and are invisible in most editors.
    /// </summary>
    internal static class RibbonIconFactory
    {
        // ── Color palette ──────────────────────────────────────────────────
        private static readonly Color MoneyGreen   = Color.FromRgb(0x1B, 0x8E, 0x2F); // $
        private static readonly Color ActionBlue   = Color.FromRgb(0x2D, 0x6B, 0x9E); // settings
        private static readonly Color SyncTeal     = Color.FromRgb(0x1E, 0x9D, 0x9D); // sync
        private static readonly Color ReportOrange = Color.FromRgb(0xD2, 0x69, 0x1E); // estimate
        private static readonly Color TagAmber     = Color.FromRgb(0xDA, 0xA5, 0x20); // place zone
        private static readonly Color MetalGray    = Color.FromRgb(0x55, 0x55, 0x55); // zone boundary
        private static readonly Color ToolGray     = Color.FromRgb(0x46, 0x59, 0x6E); // estimate zones
        private static readonly Color WarnRed      = Color.FromRgb(0xB0, 0x3A, 0x2E); // wipe / destructive

        // ── Public API — one method per ribbon icon ────────────────────────

        /// <summary>Block-style "$" — Cost Breakdown.</summary>
        public static ImageSource DollarSign(int size) =>
            Render(size, (dc, s) => DrawText(dc, s, "$",
                fontFamily: "Arial Black", weight: FontWeights.Black,
                color: MoneyGreen, sizeFraction: 0.95));

        /// <summary>Gear (U+E713) — Pricing Setup.</summary>
        public static ImageSource Gear(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ActionBlue));

        /// <summary>Sync arrows (U+E117) — Pricing Sync.</summary>
        public static ImageSource SyncArrows(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", SyncTeal));

        /// <summary>Eraser (U+E75C) — Pricing Wipe. Red flags the
        /// destructive action; an eraser reads as "clear values" rather
        /// than "delete".</summary>
        public static ImageSource Eraser(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", WarnRed));

        /// <summary>Report / document (U+E9F9) — Generate Estimate.</summary>
        public static ImageSource ReportDocument(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ReportOrange));

        /// <summary>Magnifying glass (U+E721) — Estimate Zones.</summary>
        public static ImageSource MagnifyingGlass(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", ToolGray));

        /// <summary>Wrench (U+E90F) — Zone Boundary.</summary>
        public static ImageSource Wrench(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", MetalGray));

        /// <summary>Tag (U+E8EC) — Place Zone.</summary>
        public static ImageSource Tag(int size) =>
            Render(size, (dc, s) => DrawGlyph(dc, s, "", TagAmber));

        // ── Shared rendering plumbing ──────────────────────────────────────

        /// <summary>Open a DrawingVisual, run the per-icon draw action,
        /// rasterize to a frozen ARGB bitmap at 96 DPI.</summary>
        private static ImageSource Render(int size, Action<DrawingContext, int> draw)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                draw(dc, size);
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();   // immutable → safe to assign from any UI thread
            return bmp;
        }

        /// <summary>A Segoe MDL2 Assets glyph centered at ~85% of the box,
        /// leaving a small margin so it doesn't touch neighbouring
        /// ribbon items at 32 px.</summary>
        private static void DrawGlyph(DrawingContext dc, int size,
                                      string text, Color color) =>
            DrawText(dc, size, text,
                fontFamily: "Segoe MDL2 Assets",
                weight: FontWeights.Normal,
                color: color,
                sizeFraction: 0.85);

        private static void DrawText(DrawingContext dc, int size, string text,
            string fontFamily, FontWeight weight, Color color, double sizeFraction)
        {
            var typeface = new Typeface(
                new FontFamily(fontFamily),
                FontStyles.Normal, weight, FontStretches.Normal);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var ft = new FormattedText(
                text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                size * sizeFraction,
                brush,
                pixelsPerDip: 1.0);
            double x = (size - ft.Width)  / 2.0;
            double y = (size - ft.Height) / 2.0;
            dc.DrawText(ft, new Point(x, y));
        }
    }
}
