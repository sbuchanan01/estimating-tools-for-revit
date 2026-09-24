using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// One-shot generator for a sample estimate template (.xlsx): a styled
    /// four-sheet workbook (Cover / Materials / Labor / Instructions) that
    /// works as-is and uses every placeholder <see cref="XlsxTemplateFiller"/>
    /// understands. Users add a logo, tweak branding, save, and point
    /// Pricing Setup at it.
    ///
    /// Written as raw OpenXML (ZipArchive + strings, no NuGet) with its own
    /// stylesheet — number formats, fills, frozen headers, zebra rows via
    /// conditional formatting and print setup all live in the template, so
    /// the filler (which only swaps cell values) carries them into every
    /// estimate. Layout rules the filler imposes:
    ///   • a placeholder must be the whole cell value;
    ///   • a table anchor row deletes everything below it, so each table is
    ///     the last thing on its sheet and its totals sit above it.
    /// </summary>
    internal static class SampleTemplateGenerator
    {
        internal static void Write(string path)
        {
            var sheets = new[] { Cover(), Materials(), Labor(), Instructions() };
            WriteWorkbook(path, sheets);
        }

        // ── Style indexes (must match cellXfs order in StylesXml) ──────────

        private const int XDefault = 0, XTitle = 1, XSubtitle = 2, XLabel = 3,
            XValue = 4, XValueDate = 5, XValueInt = 6, XHeader = 7,
            XHeaderRight = 8, XBand = 9, XBandMoney = 10, XLineLabel = 11,
            XLineMoney = 12, XSubLabel = 13, XSubMoney = 14, XGrandLabel = 15,
            XGrandMoney = 16, XSheetTitle = 17, XHint = 18, XLogo = 19,
            XDataText = 20, XDataMoney = 21, XDataNumber = 22, XDataCenter = 23,
            XCode = 24, XInstrHeading = 25, XInstrText = 26, XInstrCell = 27,
            XHeaderCenter = 28;

        // ── Cover ──────────────────────────────────────────────────────────

        private static Sheet Cover()
        {
            var s = new Sheet("Cover", "FF1F2A38") { Widths = { 2, 34, 20, 3, 26 } };

            // Logo sits above the title so long project names can run the
            // full width of the page without hitting it.
            s.Set("B2", "Your logo here\n(Insert → Pictures)", XLogo);
            s.Blank("B3", XLogo);
            s.Blank("B4", XLogo);
            s.Merge("B2:B4");

            s.Set("B6", "{{ProjectName}}", XTitle);
            s.Height(6, 34);
            s.Set("B7", "Cost estimate", XSubtitle);

            s.Set("B9", "Date", XLabel);              s.Set("C9", "{{Date}}", XValueDate);
            s.Set("B10", "Pricing source", XLabel);   s.Set("C10", "{{Source}}", XValue);
            s.Set("B11", "Fabrication parts", XLabel); s.Set("C11", "{{PartsScanned}}", XValueInt);

            s.Set("B13", "Summary", XHeader);         s.Set("C13", "Amount", XHeaderRight);
            s.Height(13, 20);

            int r = 14;
            void Band(string label) { s.Set($"B{r}", label, XBand); s.Blank($"C{r}", XBandMoney); r++; }
            void Line(string label, string ph) { s.Set($"B{r}", label, XLineLabel); s.Set($"C{r}", ph, XLineMoney); r++; }
            void Sub(string label, string ph) { s.Set($"B{r}", label, XSubLabel); s.Set($"C{r}", ph, XSubMoney); r++; }

            Band("Material");
            Line("Piping", "{{PipingTotal}}");
            Line("Ductwork", "{{DuctworkTotal}}");
            Line("Hangers + ancillaries", "{{HangerAncillaryTotal}}");
            Sub("Material subtotal", "{{MaterialTotal}}");
            Band("Labor");
            Line("Installation", "{{InstallLabor}}");
            Line("Fabrication", "{{FabLabor}}");
            Sub("Labor subtotal", "{{LaborTotal}}");
            s.Set($"B{r}", "Grand total", XGrandLabel);
            s.Set($"C{r}", "{{GrandTotal}}", XGrandMoney);
            s.Height(r, 24);
            r += 2;

            s.Set($"B{r}",
                "Values fill in when you run Generate Estimate with this file selected in " +
                "Pricing Setup. Restyle anything; the tool only replaces {{placeholder}} values. " +
                "See the Instructions sheet for every placeholder.", XHint);
            s.Merge($"B{r}:E{r}");
            s.Height(r, 42);

            s.FitToPage = true;
            return s;
        }

        // ── Materials ──────────────────────────────────────────────────────

        private static Sheet Materials()
        {
            var s = new Sheet("Materials", "FF2D6B9E") { Widths = { 16, 20, 48, 11, 8, 14, 15 } };

            s.Set("A1", "Material BOM", XSheetTitle);
            s.Height(1, 26);
            s.Set("A2", "{{ProjectName}}", XSubtitle);

            // Total sits above the table — rows below the anchor get replaced.
            s.Set("A4", "Material total", XBand);
            foreach (var c in new[] { "B", "C", "D", "E", "F" }) s.Blank($"{c}4", XBand);
            s.Set("G4", "{{MaterialTotal}}", XBandMoney);
            s.Height(4, 20);

            string[] headers = { "Category", "Product code", "Description", "Qty", "Unit", "Unit price", "Total" };
            int[] headerStyles = { XHeader, XHeader, XHeader, XHeaderRight, XHeaderCenter, XHeaderRight, XHeaderRight };
            for (int i = 0; i < headers.Length; i++) s.Set($"{Col(i + 1)}6", headers[i], headerStyles[i]);
            s.Height(6, 20);

            s.Set("A7", "{{Materials.Category}}", XDataText);
            s.Set("B7", "{{Materials.Code}}", XDataText);
            s.Set("C7", "{{Materials.Description}}", XDataText);
            s.Set("D7", "{{Materials.Quantity}}", XDataNumber);
            s.Set("E7", "{{Materials.Unit}}", XDataCenter);
            s.Set("F7", "{{Materials.UnitPrice}}", XDataMoney);
            s.Set("G7", "{{Materials.Total}}", XDataMoney);

            s.FreezeBelowRow = 6;
            s.ZebraRange = "A7:G10000";
            s.PrintTitleRow = 6;
            s.Landscape = true;
            s.FitToPage = true;
            return s;
        }

        // ── Labor ──────────────────────────────────────────────────────────

        private static Sheet Labor()
        {
            var s = new Sheet("Labor", "FF2D6B9E") { Widths = { 16, 44, 12, 11, 12, 15 } };

            s.Set("A1", "Labor estimate", XSheetTitle);
            s.Height(1, 26);
            s.Set("A2", "{{ProjectName}}", XSubtitle);

            s.Set("A4", "Installation", XLineLabel);
            foreach (var c in new[] { "B", "C", "D", "E" }) s.Blank($"{c}4", XLineLabel);
            s.Set("F4", "{{InstallLabor}}", XLineMoney);
            s.Set("A5", "Fabrication", XLineLabel);
            foreach (var c in new[] { "B", "C", "D", "E" }) s.Blank($"{c}5", XLineLabel);
            s.Set("F5", "{{FabLabor}}", XLineMoney);
            s.Set("A6", "Labor total", XBand);
            foreach (var c in new[] { "B", "C", "D", "E" }) s.Blank($"{c}6", XBand);
            s.Set("F6", "{{LaborTotal}}", XBandMoney);
            s.Height(6, 20);

            string[] headers = { "Type", "Labor table", "Minutes", "Hours", "Rate $/hr", "Cost" };
            int[] headerStyles = { XHeader, XHeader, XHeaderRight, XHeaderRight, XHeaderRight, XHeaderRight };
            for (int i = 0; i < headers.Length; i++) s.Set($"{Col(i + 1)}8", headers[i], headerStyles[i]);
            s.Height(8, 20);

            s.Set("A9", "{{Labor.Type}}", XDataText);
            s.Set("B9", "{{Labor.Table}}", XDataText);
            s.Set("C9", "{{Labor.Minutes}}", XDataNumber);
            s.Set("D9", "{{Labor.Hours}}", XDataNumber);
            s.Set("E9", "{{Labor.Rate}}", XDataMoney);
            s.Set("F9", "{{Labor.Cost}}", XDataMoney);

            s.FreezeBelowRow = 8;
            s.ZebraRange = "A9:F10000";
            s.PrintTitleRow = 8;
            s.FitToPage = true;
            return s;
        }

        // ── Instructions ───────────────────────────────────────────────────

        private static Sheet Instructions()
        {
            var s = new Sheet("Instructions", "FF8A94A0") { Widths = { 2, 30, 78 } };
            int r = 2;

            s.Set($"B{r}", "Estimate template — how it works", XSheetTitle);
            s.Height(r++, 26);
            s.Set($"B{r}", "Reference only — estimates never fill this sheet. Hide or delete it before sharing.", XHint);
            s.Merge($"B{r}:C{r}");
            r += 2;

            void Heading(string text) { s.Set($"B{r}", text, XInstrHeading); s.Height(r, 20); r++; }
            void Para(string text, double height = 18)
            {
                s.Set($"B{r}", text, XInstrText);
                s.Merge($"B{r}:C{r}");
                s.Height(r, height);
                r++;
            }
            void TableHead(string a, string b) { s.Set($"B{r}", a, XHeader); s.Set($"C{r}", b, XHeader); r++; }
            void Entry(string code, string what) { s.Set($"B{r}", code, XCode); s.Set($"C{r}", what, XInstrCell); r++; }

            Heading("Getting started");
            Para("1.  Add your logo on the Cover sheet (Insert → Pictures) and restyle fonts or colors anywhere.");
            Para("2.  Save this file somewhere stable, such as the project folder or a shared drive.");
            Para("3.  In Revit: Pricing Setup → Estimate template → Browse, pick this file, then Save.");
            Para("4.  Run Generate Estimate. The output keeps this workbook's formatting and fills in the model's numbers.");
            r++;

            Heading("Values you can place in any cell");
            TableHead("Placeholder", "Fills in");
            Entry("{{ProjectName}}", "Project name from Revit Project Information (file name if blank or still \"Project Name\")");
            Entry("{{Date}}", "Date the estimate was generated");
            Entry("{{Source}}", "Pricing source description");
            Entry("{{PartsScanned}}", "Number of Fabrication parts scanned");
            Entry("{{PipingTotal}}", "Piping material subtotal ($)");
            Entry("{{DuctworkTotal}}", "Ductwork material subtotal ($)");
            Entry("{{HangerAncillaryTotal}}", "Hangers + ancillaries material subtotal ($)");
            Entry("{{MaterialTotal}}", "Material total ($)");
            Entry("{{InstallLabor}}", "Installation labor total ($)");
            Entry("{{FabLabor}}", "Fabrication labor total ($)");
            Entry("{{LaborTotal}}", "Labor total ($)");
            Entry("{{GrandTotal}}", "Material + labor total ($)");
            r++;

            Heading("Material table columns");
            Para("Put these in one row, in any order. The tool writes one row per BOM line starting there " +
                 "and replaces everything below it.", 32);
            TableHead("Placeholder", "Fills in");
            Entry("{{Materials.Category}}", "Pipe / Fitting / Valve / Hanger / Duct / DuctFitting / Ancillary");
            Entry("{{Materials.Code}}", "Product code");
            Entry("{{Materials.Description}}", "Type or family description");
            Entry("{{Materials.Quantity}}", "Linear feet (pipe, duct) or piece count");
            Entry("{{Materials.Unit}}", "ft or EA");
            Entry("{{Materials.UnitPrice}}", "$ per unit");
            Entry("{{Materials.Total}}", "$ extended (unit price × quantity)");
            r++;

            Heading("Labor table columns");
            TableHead("Placeholder", "Fills in");
            Entry("{{Labor.Type}}", "Installation or Fabrication");
            Entry("{{Labor.Table}}", "Labor table name");
            Entry("{{Labor.Minutes}}", "Total minutes for that table across all parts");
            Entry("{{Labor.Hours}}", "Total hours (minutes ÷ 60)");
            Entry("{{Labor.Rate}}", "$/hr applied to that table");
            Entry("{{Labor.Cost}}", "$ for that table (hours × rate)");
            r++;

            Heading("Tips");
            Para("•  A placeholder must be the only thing in its cell. \"Total: {{GrandTotal}}\" stays as plain text.");
            Para("•  Format the placeholder row of a table once (number format, alignment, borders). Every generated row copies it.");
            Para("•  Put totals above a table, never below it. Rows under a table are replaced when it fills.");
            Para("•  Alternating row shading is a conditional format on the table columns (Home → Conditional Formatting).");
            Para("•  A material-only or labor-only estimate leaves the other side's values blank.");
            Para("•  A misspelled placeholder stays in the output as literal text, so typos are easy to spot.");
            Para("•  Leave a placeholder out and that value simply doesn't appear.");
            Para("•  Keep this sheet named \"Instructions\" so it stays untouched when estimates fill.");
            s.FitToPage = true;
            return s;
        }

        // ── Minimal multi-sheet styled writer ──────────────────────────────

        private sealed class Sheet
        {
            public readonly string Name;
            public readonly string TabColor;
            public List<double> Widths { get; } = new();
            public readonly SortedDictionary<int, SortedDictionary<int, (string? Text, int Style)>> Rows = new();
            public readonly Dictionary<int, double> Heights = new();
            public readonly List<string> Merges = new();
            public int FreezeBelowRow;
            public string? ZebraRange;
            public int PrintTitleRow;
            public bool Landscape;
            public bool FitToPage;

            public Sheet(string name, string tabColor) { Name = name; TabColor = tabColor; }

            public void Set(string cellRef, string text, int style) => Put(cellRef, text, style);
            public void Blank(string cellRef, int style) => Put(cellRef, null, style);
            public void Height(int row, double points) => Heights[row] = points;
            public void Merge(string range) => Merges.Add(range);

            private void Put(string cellRef, string? text, int style)
            {
                var (col, row) = ParseRef(cellRef);
                if (!Rows.TryGetValue(row, out var cells))
                    Rows[row] = cells = new SortedDictionary<int, (string?, int)>();
                cells[col] = (text, style);
            }
        }

        private static void WriteWorkbook(string path, IReadOnlyList<Sheet> sheets)
        {
            if (File.Exists(path)) File.Delete(path);
            using var fs = new FileStream(path, FileMode.CreateNew);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            Add(zip, "xl/workbook.xml", WorkbookXml(sheets));
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
            Add(zip, "xl/styles.xml", StylesXml);
            for (int i = 0; i < sheets.Count; i++)
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i], i == 0));
        }

        private static void Add(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        private static string ContentTypes(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string WorkbookXml(IReadOnlyList<Sheet> sheets)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                      "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<bookViews><workbookView activeTab=\"0\"/></bookViews><sheets>");
            for (int i = 0; i < sheets.Count; i++)
                sb.Append($"<sheet name=\"{Esc(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            sb.Append("</sheets>");
            var titles = sheets.Select((s, i) => (s, i)).Where(t => t.s.PrintTitleRow > 0).ToList();
            if (titles.Count > 0)
            {
                sb.Append("<definedNames>");
                foreach (var (s, i) in titles)
                    sb.Append($"<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"{i}\">" +
                              $"'{Esc(s.Name)}'!${s.PrintTitleRow}:${s.PrintTitleRow}</definedName>");
                sb.Append("</definedNames>");
            }
            sb.Append("</workbook>");
            return sb.ToString();
        }

        private static string WorkbookRels(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
            sb.Append($"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string SheetXml(Sheet s, bool selected)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                      "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");

            sb.Append($"<sheetPr><tabColor rgb=\"{s.TabColor}\"/>");
            if (s.FitToPage) sb.Append("<pageSetUpPr fitToPage=\"1\"/>");
            sb.Append("</sheetPr>");

            sb.Append($"<sheetViews><sheetView showGridLines=\"0\" workbookViewId=\"0\"{(selected ? " tabSelected=\"1\"" : "")}>");
            if (s.FreezeBelowRow > 0)
            {
                string top = $"A{s.FreezeBelowRow + 1}";
                sb.Append($"<pane ySplit=\"{s.FreezeBelowRow}\" topLeftCell=\"{top}\" activePane=\"bottomLeft\" state=\"frozen\"/>");
                sb.Append($"<selection pane=\"bottomLeft\" activeCell=\"{top}\" sqref=\"{top}\"/>");
            }
            sb.Append("</sheetView></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");

            if (s.Widths.Count > 0)
            {
                sb.Append("<cols>");
                for (int i = 0; i < s.Widths.Count; i++)
                    sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{s.Widths[i].ToString("0.##", inv)}\" customWidth=\"1\"/>");
                sb.Append("</cols>");
            }

            sb.Append("<sheetData>");
            var rowNums = s.Rows.Keys.Union(s.Heights.Keys).OrderBy(n => n);
            foreach (int row in rowNums)
            {
                sb.Append($"<row r=\"{row}\"");
                if (s.Heights.TryGetValue(row, out double ht))
                    sb.Append($" ht=\"{ht.ToString("0.##", inv)}\" customHeight=\"1\"");
                sb.Append('>');
                if (s.Rows.TryGetValue(row, out var cells))
                {
                    foreach (var (col, (text, style)) in cells)
                    {
                        string cellRef = $"{Col(col)}{row}";
                        if (text == null)
                            sb.Append($"<c r=\"{cellRef}\" s=\"{style}\"/>");
                        else
                            sb.Append($"<c r=\"{cellRef}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");

            if (s.Merges.Count > 0)
            {
                sb.Append($"<mergeCells count=\"{s.Merges.Count}\">");
                foreach (var m in s.Merges) sb.Append($"<mergeCell ref=\"{m}\"/>");
                sb.Append("</mergeCells>");
            }

            if (s.ZebraRange != null)
            {
                sb.Append($"<conditionalFormatting sqref=\"{s.ZebraRange}\">" +
                          "<cfRule type=\"expression\" dxfId=\"0\" priority=\"1\">" +
                          "<formula>MOD(ROW(),2)=0</formula></cfRule></conditionalFormatting>");
            }

            sb.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/>");
            sb.Append($"<pageSetup orientation=\"{(s.Landscape ? "landscape" : "portrait")}\"" +
                      (s.FitToPage ? " fitToWidth=\"1\" fitToHeight=\"0\"" : "") + "/>");
            sb.Append("<headerFooter><oddFooter>&amp;L&amp;8&amp;A&amp;R&amp;8Page &amp;P of &amp;N</oddFooter></headerFooter>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        // Palette matches the estimate dialogs: navy #1F2A38, accent #2D6B9E,
        // band #DCE8F2, zebra #F4F8FB, rules #D5DCE3 / #AAB4BE.
        // cellXfs order MUST match the X* constants above.
        private const string StylesXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <numFmts count="4">
    <numFmt numFmtId="164" formatCode="&quot;$&quot;#,##0.00"/>
    <numFmt numFmtId="165" formatCode="mmmm d, yyyy"/>
    <numFmt numFmtId="166" formatCode="#,##0"/>
    <numFmt numFmtId="167" formatCode="#,##0.00"/>
  </numFmts>
  <fonts count="12">
    <font><sz val="11"/><color rgb="FF333B44"/><name val="Calibri"/><family val="2"/></font>
    <font><b/><sz val="20"/><color rgb="FF1F2A38"/><name val="Calibri"/><family val="2"/></font>
    <font><sz val="12"/><color rgb="FF6B7580"/><name val="Calibri"/><family val="2"/></font>
    <font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Calibri"/><family val="2"/></font>
    <font><b/><sz val="11"/><color rgb="FF1F4E79"/><name val="Calibri"/><family val="2"/></font>
    <font><b/><sz val="11"/><color rgb="FF1F2A38"/><name val="Calibri"/><family val="2"/></font>
    <font><sz val="10"/><color rgb="FF6B7580"/><name val="Calibri"/><family val="2"/></font>
    <font><b/><sz val="12"/><color rgb="FFFFFFFF"/><name val="Calibri"/><family val="2"/></font>
    <font><b/><sz val="16"/><color rgb="FF2D6B9E"/><name val="Calibri"/><family val="2"/></font>
    <font><i/><sz val="10"/><color rgb="FF8A94A0"/><name val="Calibri"/><family val="2"/></font>
    <font><sz val="10"/><color rgb="FF1F4E79"/><name val="Consolas"/><family val="3"/></font>
    <font><b/><sz val="12"/><color rgb="FF2D6B9E"/><name val="Calibri"/><family val="2"/></font>
  </fonts>
  <fills count="5">
    <fill><patternFill patternType="none"/></fill>
    <fill><patternFill patternType="gray125"/></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FF1F2A38"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFDCE8F2"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFF4F6F8"/><bgColor indexed="64"/></patternFill></fill>
  </fills>
  <borders count="4">
    <border><left/><right/><top/><bottom/><diagonal/></border>
    <border><left/><right/><top/><bottom style="thin"><color rgb="FFD5DCE3"/></bottom><diagonal/></border>
    <border><left/><right/><top style="thin"><color rgb="FFAAB4BE"/></top><bottom/><diagonal/></border>
    <border><left style="dashed"><color rgb="FFAAB4BE"/></left><right style="dashed"><color rgb="FFAAB4BE"/></right><top style="dashed"><color rgb="FFAAB4BE"/></top><bottom style="dashed"><color rgb="FFAAB4BE"/></bottom><diagonal/></border>
  </borders>
  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
  <cellXfs count="29">
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
    <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment vertical="center"/></xf>
    <xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/>
    <xf numFmtId="0" fontId="6" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment vertical="center"/></xf>
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
    <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
    <xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1" applyAlignment="1"><alignment horizontal="left" vertical="center"/></xf>
    <xf numFmtId="0" fontId="3" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="left" vertical="center" indent="1"/></xf>
    <xf numFmtId="0" fontId="3" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="right" vertical="center" indent="1"/></xf>
    <xf numFmtId="0" fontId="4" fillId="3" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="left" vertical="center" indent="1"/></xf>
    <xf numFmtId="164" fontId="5" fillId="3" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="right" vertical="center" indent="1"/></xf>
    <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left" vertical="center" indent="2"/></xf>
    <xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center" indent="1"/></xf>
    <xf numFmtId="0" fontId="5" fillId="0" borderId="2" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left" vertical="center" indent="1"/></xf>
    <xf numFmtId="164" fontId="5" fillId="0" borderId="2" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center" indent="1"/></xf>
    <xf numFmtId="0" fontId="7" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="left" vertical="center" indent="1"/></xf>
    <xf numFmtId="164" fontId="7" fillId="2" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="right" vertical="center" indent="1"/></xf>
    <xf numFmtId="0" fontId="8" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment vertical="center"/></xf>
    <xf numFmtId="0" fontId="9" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment horizontal="left" vertical="top" wrapText="1"/></xf>
    <xf numFmtId="0" fontId="9" fillId="4" borderId="3" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
    <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left" vertical="center" indent="1"/></xf>
    <xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center" indent="1"/></xf>
    <xf numFmtId="167" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center" indent="1"/></xf>
    <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="0" fontId="10" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left" vertical="center" indent="1"/></xf>
    <xf numFmtId="0" fontId="11" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment vertical="bottom"/></xf>
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment horizontal="left" vertical="center" wrapText="1"/></xf>
    <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left" vertical="center" indent="1" wrapText="1"/></xf>
    <xf numFmtId="0" fontId="3" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
  </cellXfs>
  <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
  <dxfs count="1">
    <dxf><fill><patternFill patternType="solid"><bgColor rgb="FFF4F8FB"/></patternFill></fill></dxf>
  </dxfs>
</styleSheet>
""";

        // ── Helpers ────────────────────────────────────────────────────────

        private static string Col(int col)
        {
            var sb = new StringBuilder();
            while (col > 0)
            {
                int rem = (col - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                col = (col - 1) / 26;
            }
            return sb.ToString();
        }

        private static (int Col, int Row) ParseRef(string cellRef)
        {
            int i = 0, col = 0;
            while (i < cellRef.Length && char.IsLetter(cellRef[i]))
                col = col * 26 + (char.ToUpperInvariant(cellRef[i++]) - 'A' + 1);
            return (col, int.Parse(cellRef.Substring(i), CultureInfo.InvariantCulture));
        }

        private static string Esc(string s) => s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
