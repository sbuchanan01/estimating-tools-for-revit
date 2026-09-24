using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Writes a styled XLSX from scratch — no template, no NuGet. Same
    /// dependency-free stance as <see cref="XlsxTemplateFiller"/>
    /// (ZipArchive + XDocument). Purpose-built for reports the tool
    /// generates programmatically (Zone Estimate today; other estimates
    /// may adopt it later).
    ///
    /// Style palette hard-coded at the top so callers just pick a
    /// <see cref="CellStyle"/> per cell:
    ///
    /// <list type="bullet">
    ///   <item><b>Header</b> — bold white text, dark-navy fill, borders.</item>
    ///   <item><b>ZoneName</b> — bold, light-blue fill, borders.</item>
    ///   <item><b>SubHeader</b> — bold gray fill, borders.</item>
    ///   <item><b>Data</b> — default font, left-aligned string, borders.</item>
    ///   <item><b>DataZebra</b> — same as Data with a very light fill for
    ///     alternating rows.</item>
    ///   <item><b>Money</b> / <b>MoneyZebra</b> — currency $#,##0.00.</item>
    ///   <item><b>MoneyBold</b> — bold currency for zone-total rows.</item>
    ///   <item><b>GrandTotalLabel</b> / <b>GrandTotalMoney</b> — bold
    ///     white text on dark-navy for the bottom sum row.</item>
    /// </list>
    ///
    /// Cell values are inline strings (<c>t="inlineStr"</c>) for text and
    /// raw numbers otherwise — no sharedStrings.xml, keeping the writer
    /// simple.
    /// </summary>
    public static class XlsxStyledWriter
    {
        public enum CellStyle
        {
            Default          = 0,
            Header           = 1,
            ZoneName         = 2,
            SubHeader        = 3,
            Data             = 4,
            DataZebra        = 5,
            Money            = 6,
            MoneyZebra       = 7,
            MoneyBold        = 8,
            GrandTotalLabel  = 9,
            GrandTotalMoney  = 10,
        }

        /// <summary>One cell.</summary>
        public readonly struct Cell
        {
            public readonly string?  Text;
            public readonly double?  Number;
            public readonly CellStyle Style;
            public readonly int      ColSpan;  // 1 = normal, N = merge across N columns

            public Cell(string text, CellStyle style = CellStyle.Data, int colSpan = 1)
            { Text = text; Number = null; Style = style; ColSpan = colSpan; }

            public Cell(double number, CellStyle style = CellStyle.Money, int colSpan = 1)
            { Text = null; Number = number; Style = style; ColSpan = colSpan; }
        }

        /// <summary>One column's width (in Excel's approximate character
        /// units — 8.43 is the default; use ~20 for money columns,
        /// ~40 for description columns).</summary>
        public readonly struct ColumnWidth
        {
            public readonly double Width;
            public ColumnWidth(double width) { Width = width; }
        }

        /// <summary>Writes the sheet to <paramref name="path"/>.
        /// Overwrites without prompting.</summary>
        /// <param name="sheetName">Tab label (max 31 chars, Excel rule).</param>
        /// <param name="columnWidths">Per-column widths. Missing entries
        /// use Excel's default. Pass empty to skip column sizing.</param>
        /// <param name="rows">One <c>List&lt;Cell&gt;</c> per row.
        /// Empty inner list emits an empty spacer row.</param>
        public static void Write(string path, string sheetName,
            IList<ColumnWidth> columnWidths, IList<IList<Cell>> rows)
        {
            if (File.Exists(path)) File.Delete(path);
            using var fs = new FileStream(path, FileMode.CreateNew);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            AddEntry(zip, "[Content_Types].xml", ContentTypesXml());
            AddEntry(zip, "_rels/.rels", RootRelsXml());
            AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml());
            AddEntry(zip, "xl/workbook.xml", WorkbookXml(sheetName));
            AddEntry(zip, "xl/styles.xml", StylesXml());
            AddEntry(zip, "xl/worksheets/sheet1.xml",
                SheetXml(columnWidths, rows));
        }

        private static void AddEntry(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Fastest);
            using var sw = new StreamWriter(e.Open(), new UTF8Encoding(false));
            sw.Write(content);
        }

        // ── Sheet ──────────────────────────────────────────────────────

        private static string SheetXml(IList<ColumnWidth> columnWidths,
            IList<IList<Cell>> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            // Column widths
            if (columnWidths.Count > 0)
            {
                sb.Append("<cols>");
                for (int i = 0; i < columnWidths.Count; i++)
                {
                    int idx = i + 1;
                    double w = columnWidths[i].Width;
                    if (w <= 0) continue;
                    sb.Append($"<col min=\"{idx}\" max=\"{idx}\" width=\"" +
                        $"{w.ToString("0.##", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
                }
                sb.Append("</cols>");
            }

            sb.Append("<sheetData>");
            var merges = new List<string>();
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                int excelRow = r + 1;
                sb.Append($"<row r=\"{excelRow}\">");
                int col = 1;
                foreach (var cell in row)
                {
                    string cellRef = ColumnLetter(col) + excelRow;
                    if (cell.Number.HasValue)
                    {
                        sb.Append($"<c r=\"{cellRef}\" s=\"{(int)cell.Style}\">");
                        sb.Append("<v>");
                        sb.Append(cell.Number.Value.ToString("R",
                            CultureInfo.InvariantCulture));
                        sb.Append("</v></c>");
                    }
                    else
                    {
                        sb.Append($"<c r=\"{cellRef}\" s=\"{(int)cell.Style}\" t=\"inlineStr\">");
                        sb.Append("<is><t xml:space=\"preserve\">");
                        sb.Append(EscapeXml(cell.Text ?? ""));
                        sb.Append("</t></is></c>");
                    }

                    if (cell.ColSpan > 1)
                    {
                        merges.Add(cellRef + ":" +
                            ColumnLetter(col + cell.ColSpan - 1) + excelRow);
                    }
                    col += cell.ColSpan;
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");

            if (merges.Count > 0)
            {
                sb.Append($"<mergeCells count=\"{merges.Count}\">");
                foreach (var m in merges)
                    sb.Append($"<mergeCell ref=\"{m}\"/>");
                sb.Append("</mergeCells>");
            }

            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private static string ColumnLetter(int col)
        {
            // 1-based: 1=A, 26=Z, 27=AA, …
            var sb = new StringBuilder();
            while (col > 0)
            {
                int rem = (col - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                col = (col - 1) / 26;
            }
            return sb.ToString();
        }

        private static string EscapeXml(string s) => s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

        // ── Styles ─────────────────────────────────────────────────────

        // Palette + Excel's built-in currency format (numFmtId 44 = "_($* #,##0.00…")
        // Custom numFmtId 164 = "$#,##0.00" (simpler; Excel doesn't ship 164 built-in
        //   for that format, so we register it in numFmts).
        // Style cellXfs indexes MUST match the CellStyle enum's integer values.
        private static string StylesXml() => """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <numFmts count="1">
    <numFmt numFmtId="164" formatCode="&quot;$&quot;#,##0.00"/>
  </numFmts>
  <fonts count="4">
    <font><sz val="11"/><name val="Calibri"/></font>                                <!-- 0 default -->
    <font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font>     <!-- 1 white bold -->
    <font><b/><sz val="11"/><name val="Calibri"/></font>                            <!-- 2 bold -->
    <font><sz val="11"/><color rgb="FF57636E"/><name val="Calibri"/></font>         <!-- 3 muted -->
  </fonts>
  <fills count="6">
    <fill><patternFill patternType="none"/></fill>                                  <!-- 0 -->
    <fill><patternFill patternType="gray125"/></fill>                               <!-- 1 (Excel required placeholder) -->
    <fill><patternFill patternType="solid"><fgColor rgb="FF2D6B9E"/></patternFill></fill>  <!-- 2 header navy -->
    <fill><patternFill patternType="solid"><fgColor rgb="FFEAF3FA"/></patternFill></fill>  <!-- 3 zone name light blue -->
    <fill><patternFill patternType="solid"><fgColor rgb="FFF5F5F5"/></patternFill></fill>  <!-- 4 subheader gray -->
    <fill><patternFill patternType="solid"><fgColor rgb="FFFAFBFC"/></patternFill></fill>  <!-- 5 zebra almost-white -->
  </fills>
  <borders count="2">
    <border><left/><right/><top/><bottom/><diagonal/></border>                     <!-- 0 none -->
    <border>                                                                        <!-- 1 all thin -->
      <left style="thin"><color rgb="FFDDDDDD"/></left>
      <right style="thin"><color rgb="FFDDDDDD"/></right>
      <top style="thin"><color rgb="FFDDDDDD"/></top>
      <bottom style="thin"><color rgb="FFDDDDDD"/></bottom>
      <diagonal/>
    </border>
  </borders>
  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
  <cellXfs count="11">
    <!-- 0 Default -->
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
    <!-- 1 Header -->
    <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <!-- 2 ZoneName -->
    <xf numFmtId="0" fontId="2" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
    <!-- 3 SubHeader -->
    <xf numFmtId="0" fontId="2" fillId="4" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
    <!-- 4 Data -->
    <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
    <!-- 5 DataZebra -->
    <xf numFmtId="0" fontId="0" fillId="5" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
    <!-- 6 Money -->
    <xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center"/></xf>
    <!-- 7 MoneyZebra -->
    <xf numFmtId="164" fontId="0" fillId="5" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center"/></xf>
    <!-- 8 MoneyBold -->
    <xf numFmtId="164" fontId="2" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center"/></xf>
    <!-- 9 GrandTotalLabel -->
    <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
    <!-- 10 GrandTotalMoney -->
    <xf numFmtId="164" fontId="1" fillId="2" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center"/></xf>
  </cellXfs>
  <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
</styleSheet>
""";

        // ── Fixed OPC boilerplate ─────────────────────────────────────

        private static string ContentTypesXml() => """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
</Types>
""";

        private static string RootRelsXml() => """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>
""";

        private static string WorkbookXml(string sheetName)
        {
            string safe = SanitizeSheetName(sheetName);
            return $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets>
    <sheet name="{EscapeXml(safe)}" sheetId="1" r:id="rId1"/>
  </sheets>
</workbook>
""";
        }

        private static string WorkbookRelsXml() => """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>
""";

        private static string SanitizeSheetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Sheet1";
            // Excel bans these chars in sheet names + caps length at 31.
            foreach (var bad in new[] { '\\', '/', '?', '*', '[', ']', ':' })
                name = name.Replace(bad, ' ');
            return name.Length > 31 ? name.Substring(0, 31) : name;
        }
    }
}
