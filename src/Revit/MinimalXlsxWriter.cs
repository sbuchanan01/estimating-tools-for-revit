using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Tiny dependency-free XLSX writer. Builds a multi-sheet workbook
    /// by writing the seven mandatory OpenXML parts into a zip archive:
    ///
    ///   [Content_Types].xml
    ///   _rels/.rels
    ///   xl/workbook.xml
    ///   xl/_rels/workbook.xml.rels
    ///   xl/worksheets/sheetN.xml  (one per sheet)
    ///
    /// Cells support inline strings, numbers (int/double/decimal) and
    /// nulls (empty cells). No formatting, no formulas, no shared
    /// strings — sufficient for our estimate exports while avoiding
    /// the deployment risk of bundling ClosedXML / OpenXML SDK alongside
    /// Revit's own assembly cache.
    ///
    /// Usage:
    ///   var x = new MinimalXlsxWriter();
    ///   var s = x.AddSheet("Combined");
    ///   s.AddRow("Header A", "Header B", 123);
    ///   s.AddRow("Skilled", null, 30.0);
    ///   x.Save(@"C:\path\file.xlsx");
    /// </summary>
    public sealed class MinimalXlsxWriter
    {
        private readonly List<Sheet> _sheets = new();

        public Sheet AddSheet(string name)
        {
            // Excel forbids \ / ? * [ ] : in sheet names; truncate at 31 chars.
            var safe = SanitizeSheetName(name);
            var s = new Sheet(safe);
            _sheets.Add(s);
            return s;
        }

        public void Save(string path)
        {
            if (_sheets.Count == 0)
                throw new InvalidOperationException("No sheets to write.");
            if (File.Exists(path)) File.Delete(path);

            using var fs  = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            WriteEntry(zip, "[Content_Types].xml",      BuildContentTypes());
            WriteEntry(zip, "_rels/.rels",              BuildRootRels());
            WriteEntry(zip, "xl/workbook.xml",          BuildWorkbook());
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRels());
            for (int i = 0; i < _sheets.Count; i++)
                WriteEntry(zip, $"xl/worksheets/sheet{i + 1}.xml",
                    BuildSheetXml(_sheets[i]));
        }

        // ── Sheet model ─────────────────────────────────────────────────────

        public sealed class Sheet
        {
            internal string Name;
            internal List<List<object?>> Rows = new();

            internal Sheet(string name) { Name = name; }

            public void AddRow(params object?[] cells) =>
                Rows.Add(new List<object?>(cells));
        }

        // ── XML builders ────────────────────────────────────────────────────

        private string BuildContentTypes()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            for (int i = 0; i < _sheets.Count; i++)
                sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string BuildRootRels()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>";
        }

        private string BuildWorkbook()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheets>");
            for (int i = 0; i < _sheets.Count; i++)
                sb.Append($"<sheet name=\"{XmlEscape(_sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            sb.Append("</sheets>");
            sb.Append("</workbook>");
            return sb.ToString();
        }

        private string BuildWorkbookRels()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < _sheets.Count; i++)
                sb.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildSheetXml(Sheet s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetData>");
            for (int r = 0; r < s.Rows.Count; r++)
            {
                var row = s.Rows[r];
                int rowNum = r + 1;
                sb.Append($"<row r=\"{rowNum}\">");
                for (int c = 0; c < row.Count; c++)
                {
                    string cellRef = $"{ColLetter(c + 1)}{rowNum}";
                    var v = row[c];
                    AppendCell(sb, cellRef, v);
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private static void AppendCell(StringBuilder sb, string cellRef, object? v)
        {
            if (v == null)
            {
                sb.Append($"<c r=\"{cellRef}\"/>");
                return;
            }

            switch (v)
            {
                case string s when string.IsNullOrEmpty(s):
                    sb.Append($"<c r=\"{cellRef}\"/>");
                    break;
                case string s:
                    sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                      .Append(XmlEscape(s))
                      .Append("</t></is></c>");
                    break;
                case bool b:
                    sb.Append($"<c r=\"{cellRef}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>");
                    break;
                case int i:
                    sb.Append($"<c r=\"{cellRef}\"><v>{i.ToString(CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case long l:
                    sb.Append($"<c r=\"{cellRef}\"><v>{l.ToString(CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                    sb.Append($"<c r=\"{cellRef}\"><v>{d.ToString("0.######", CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case decimal dec:
                    sb.Append($"<c r=\"{cellRef}\"><v>{dec.ToString("0.######", CultureInfo.InvariantCulture)}</v></c>");
                    break;
                default:
                    sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                      .Append(XmlEscape(v.ToString() ?? ""))
                      .Append("</t></is></c>");
                    break;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>1-based column number → Excel letters (1=A, 26=Z, 27=AA…).</summary>
        public static string ColLetter(int colNum)
        {
            if (colNum < 1) throw new ArgumentOutOfRangeException(nameof(colNum));
            var sb = new StringBuilder();
            while (colNum > 0)
            {
                int rem = (colNum - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                colNum = (colNum - 1) / 26;
            }
            return sb.ToString();
        }

        private static string SanitizeSheetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Sheet";
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (c == '\\' || c == '/' || c == '?' || c == '*' ||
                    c == '[' || c == ']' || c == ':' || c == '\'')
                    sb.Append(' ');
                else sb.Append(c);
            }
            string trimmed = sb.ToString().Trim();
            if (trimmed.Length == 0) trimmed = "Sheet";
            if (trimmed.Length > 31) trimmed = trimmed.Substring(0, 31);
            return trimmed;
        }

        private static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&':  sb.Append("&amp;");  break;
                    case '<':  sb.Append("&lt;");   break;
                    case '>':  sb.Append("&gt;");   break;
                    case '"':  sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        // Strip control characters except \t \n \r — XML
                        // forbids them and Excel will reject the file.
                        if (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                            sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void WriteEntry(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var s = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }
    }
}
