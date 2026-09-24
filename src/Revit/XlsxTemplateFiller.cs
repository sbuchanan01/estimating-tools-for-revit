using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Fills an Excel template (.xlsx) with estimate data by replacing
    /// <c>{{Placeholder}}</c> markers in cells. Dependency-free —
    /// uses <see cref="ZipArchive"/> + <see cref="XDocument"/>, both
    /// in .NET, no NuGet additions (per the codebase decision to avoid
    /// shipping ClosedXML / OpenXML SDK alongside Revit's assembly cache).
    ///
    /// Two kinds of placeholders are supported:
    /// <list type="bullet">
    /// <item><b>Scalar</b> — a cell containing exactly <c>{{Name}}</c>
    /// gets its value replaced with the entry from
    /// <see cref="FillRequest.Scalars"/>. Cell style (currency format,
    /// font, etc.) is preserved — only the value+type changes.</item>
    /// <item><b>Table anchor</b> — a row whose cells contain
    /// <c>{{TableName.FieldName}}</c> placeholders becomes the table's
    /// header. The tool reads the column-to-field mapping from the
    /// anchor row, then writes one data row per entry in
    /// <see cref="FillRequest.Tables"/>, starting at the anchor row
    /// and overwriting any content below.</item>
    /// </list>
    /// Cell styles in the anchor row propagate to data rows via Excel's
    /// implicit row/column style inheritance — the user formats the
    /// anchor row cells once (currency, alignment, borders) and every
    /// generated data row picks up the same formatting.
    ///
    /// Cells with placeholder names that aren't in the request dictionaries
    /// are left untouched — the literal <c>{{Name}}</c> text remains in
    /// the output, which makes mis-typed placeholders visible to the user
    /// for debugging.
    /// </summary>
    public static class XlsxTemplateFiller
    {
        /// <summary>Inputs for <see cref="Fill"/> — keeps the signature
        /// manageable as scalar/table dicts grow.</summary>
        public sealed class FillRequest
        {
            /// <summary>Scalar placeholders. Key = name (matched against
            /// <c>{{Name}}</c> in cells), value = string / double / int /
            /// DateTime. Numbers and dates write as numeric/date-formatted
            /// cells (cell style preserved from the template).</summary>
            public Dictionary<string, object?> Scalars { get; }
                = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Table anchors. Key = table name (matched against
            /// <c>{{TableName.FieldName}}</c>), value = the data rows.
            /// Each row is a field-name → value dictionary; rows that
            /// don't have a value for a given field write empty cells.</summary>
            public Dictionary<string, List<Dictionary<string, object?>>> Tables { get; }
                = new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly XNamespace XlNs =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PkgRelNs =
            "http://schemas.openxmlformats.org/package/2006/relationships";

        /// <summary>Sheet name the filler never touches. The sample
        /// template's placeholder reference lives there; filling it would
        /// swap the documented {{Names}} for values and expand the listed
        /// table anchors onto it.</summary>
        public const string ReferenceSheetName = "Instructions";

        private static readonly Regex ScalarRx =
            new(@"^\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}$",
                RegexOptions.Compiled);
        private static readonly Regex FieldRx =
            new(@"^\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}$",
                RegexOptions.Compiled);

        /// <summary>
        /// Fills the template at <paramref name="templatePath"/> using
        /// <paramref name="request"/> and saves the result to
        /// <paramref name="outputPath"/>. The output is a complete copy
        /// of the template — original file is never modified.
        /// </summary>
        public static void Fill(string templatePath, string outputPath,
                                FillRequest request)
        {
            if (!File.Exists(templatePath))
                throw new FileNotFoundException("Template not found.", templatePath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Copy(templatePath, outputPath);

            using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Update);
            var sharedStrings = ReadSharedStrings(zip);
            var referenceSheets = SheetPathsNamed(zip, ReferenceSheetName);

            // Snapshot sheet entries — we'll delete + recreate each, which
            // mutates the entries collection mid-iteration.
            var sheetEntries = zip.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet",
                    StringComparison.OrdinalIgnoreCase)
                    && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    && !referenceSheets.Contains(e.FullName))
                .Select(e => e.FullName)
                .ToList();

            foreach (var name in sheetEntries)
            {
                var entry = zip.GetEntry(name);
                if (entry == null) continue;

                XDocument doc;
                using (var s = entry.Open())
                    doc = XDocument.Load(s);

                ProcessSheet(doc, sharedStrings, request);

                entry.Delete();
                var fresh = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var ws = fresh.Open();
                doc.Save(ws);
            }
        }

        // ── Sheet processing ────────────────────────────────────────────────

        private static void ProcessSheet(XDocument doc, List<string> sharedStrings,
                                         FillRequest request)
        {
            var sheetData = doc.Root?.Element(XlNs + "sheetData");
            if (sheetData == null) return;

            // Pass 1: identify table-anchor rows by scanning for any
            // {{TableName.FieldName}} cell whose TableName is in the
            // request. Build a row → (table, column→field map) lookup.
            var anchorByRow = new Dictionary<int, (string Table, Dictionary<int, string> ColField)>();
            foreach (var row in sheetData.Elements(XlNs + "row"))
            {
                int rowNum = (int?)row.Attribute("r") ?? 0;
                if (rowNum <= 0) continue;
                Dictionary<int, string>? colField = null;
                string? tableName = null;
                foreach (var cell in row.Elements(XlNs + "c"))
                {
                    string text = GetCellText(cell, sharedStrings) ?? "";
                    var m = FieldRx.Match(text);
                    if (!m.Success) continue;
                    string table = m.Groups[1].Value;
                    string field = m.Groups[2].Value;
                    if (!request.Tables.ContainsKey(table)) continue;
                    // Only the first table-name we see in the row counts —
                    // mixing tables within one row isn't supported and
                    // would be confusing anyway.
                    if (tableName == null) tableName = table;
                    else if (!string.Equals(tableName, table,
                        StringComparison.OrdinalIgnoreCase)) continue;

                    int col = ColumnFromCellRef((string?)cell.Attribute("r") ?? "");
                    if (col <= 0) continue;
                    colField ??= new Dictionary<int, string>();
                    colField[col] = field;
                }
                if (tableName != null && colField != null)
                    anchorByRow[rowNum] = (tableName, colField);
            }

            // Pass 2: scalar replacement. Skip cells that participate in
            // an anchor row (those get rewritten in pass 3 with data).
            var anchorRowSet = new HashSet<int>(anchorByRow.Keys);
            foreach (var cell in doc.Descendants(XlNs + "c"))
            {
                int rowNum = ParseRow((string?)cell.Attribute("r") ?? "");
                if (anchorRowSet.Contains(rowNum)) continue;

                string text = GetCellText(cell, sharedStrings) ?? "";
                var m = ScalarRx.Match(text);
                if (!m.Success) continue;
                string name = m.Groups[1].Value;
                if (request.Scalars.TryGetValue(name, out var value))
                    WriteCellValue(cell, value);
            }

            // Pass 3: expand each anchor row into N data rows. Existing
            // rows below the anchor are REMOVED first so we never leave
            // dangling content (or worse, duplicate row numbers).
            // Anchors are processed top-down so earlier-row anchors
            // don't get clobbered by later-row expansions.
            foreach (var rowNum in anchorByRow.Keys.OrderBy(r => r))
            {
                var (tableName, colField) = anchorByRow[rowNum];
                var data = request.Tables[tableName];
                ExpandTableAnchor(sheetData, rowNum, colField, data);
            }

            // Merge cell ranges below the anchor are now invalid — Excel
            // will complain. Strip them defensively.
            if (anchorByRow.Count > 0)
            {
                int firstAnchorRow = anchorByRow.Keys.Min();
                var mergeCells = doc.Root?.Element(XlNs + "mergeCells");
                mergeCells?.Elements(XlNs + "mergeCell").ToList().ForEach(mc =>
                {
                    string @ref = (string?)mc.Attribute("ref") ?? "";
                    var parts = @ref.Split(':');
                    if (parts.Length == 2 &&
                        (ParseRow(parts[0]) >= firstAnchorRow ||
                         ParseRow(parts[1]) >= firstAnchorRow))
                        mc.Remove();
                });
            }
        }

        private static void ExpandTableAnchor(XElement sheetData, int anchorRow,
            Dictionary<int, string> colField,
            List<Dictionary<string, object?>> data)
        {
            // Anchor row's <row> element + cell template — preserves
            // styles (cell.s attribute), row height (row.ht), etc.
            var anchor = sheetData.Elements(XlNs + "row")
                .FirstOrDefault(r => ((int?)r.Attribute("r") ?? 0) == anchorRow);
            if (anchor == null) return;

            // Remove every existing row at anchorRow and below — they're
            // either the anchor (we're replacing) or template content
            // that would conflict with our generated rows. Snapshot
            // first because we're modifying the collection.
            var toRemove = sheetData.Elements(XlNs + "row")
                .Where(r => ((int?)r.Attribute("r") ?? 0) >= anchorRow)
                .ToList();

            // Save the anchor's cell templates by column so we can clone
            // their style attributes onto generated cells. Anchor cells
            // that aren't placeholders are kept verbatim (e.g. a row
            // label in column A with placeholders in B+).
            var cellTemplates = new Dictionary<int, XElement>();
            foreach (var c in anchor.Elements(XlNs + "c"))
            {
                int col = ColumnFromCellRef((string?)c.Attribute("r") ?? "");
                if (col > 0) cellTemplates[col] = c;
            }

            foreach (var r in toRemove) r.Remove();

            for (int i = 0; i < data.Count; i++)
            {
                int rowNum = anchorRow + i;
                var newRow = new XElement(XlNs + "row",
                    new XAttribute("r", rowNum.ToString(CultureInfo.InvariantCulture)));

                // Copy the anchor row's height attribute if any (preserves
                // user-set row heights on the table style).
                var ht = anchor.Attribute("ht");
                if (ht != null) newRow.Add(new XAttribute("ht", ht.Value));
                var customHeight = anchor.Attribute("customHeight");
                if (customHeight != null)
                    newRow.Add(new XAttribute("customHeight", customHeight.Value));

                foreach (var kv in cellTemplates.OrderBy(k => k.Key))
                {
                    int col = kv.Key;
                    var template = kv.Value;
                    var cell = new XElement(XlNs + "c",
                        new XAttribute("r", $"{ColumnLetters(col)}{rowNum}"));

                    // Carry the style attribute forward so the column
                    // formatting (currency, alignment, etc.) applies to
                    // every generated row.
                    var styleAttr = template.Attribute("s");
                    if (styleAttr != null)
                        cell.Add(new XAttribute("s", styleAttr.Value));

                    if (colField.TryGetValue(col, out var field) &&
                        data[i].TryGetValue(field, out var value))
                    {
                        FillCellContent(cell, value);
                    }
                    // Cells with no field mapping stay empty (preserves
                    // the column's spacing/style without injecting data).

                    newRow.Add(cell);
                }
                sheetData.Add(newRow);
            }
        }

        // ── Cell I/O ────────────────────────────────────────────────────────

        /// <summary>Reads a cell's effective text value. For shared-string
        /// cells, looks up the index in the sharedStrings table. For
        /// inline-string cells, concatenates all <c>&lt;t&gt;</c> child
        /// values. Numeric/empty/other cells return null.</summary>
        private static string? GetCellText(XElement cell, List<string> sharedStrings)
        {
            string type = (string?)cell.Attribute("t") ?? "";
            if (type == "s")
            {
                var v = cell.Element(XlNs + "v")?.Value;
                if (int.TryParse(v, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int idx) &&
                    idx >= 0 && idx < sharedStrings.Count)
                    return sharedStrings[idx];
                return null;
            }
            if (type == "inlineStr" || type == "str")
            {
                return string.Concat(cell.Descendants(XlNs + "t")
                    .Select(t => t.Value));
            }
            return null;
        }

        /// <summary>Writes <paramref name="value"/> into <paramref name="cell"/>,
        /// updating the <c>t=</c> attribute and inner content to match
        /// the value's type. Preserves the cell's <c>s=</c> style.</summary>
        private static void WriteCellValue(XElement cell, object? value)
        {
            FillCellContent(cell, value);
        }

        private static void FillCellContent(XElement cell, object? value)
        {
            // Clear existing inner content first.
            cell.Elements().Remove();

            if (value == null)
            {
                cell.Attribute("t")?.Remove();
                return;
            }

            switch (value)
            {
                case double d:    WriteNumber(cell, d); break;
                case float f:     WriteNumber(cell, f); break;
                case decimal m:   WriteNumber(cell, (double)m); break;
                case int i:       WriteNumber(cell, i); break;
                case long l:      WriteNumber(cell, l); break;
                case bool b:
                    cell.SetAttributeValue("t", "b");
                    cell.Add(new XElement(XlNs + "v", b ? "1" : "0"));
                    break;
                case DateTime dt:
                    // Excel stores dates as serial numbers (days since 1899-12-30).
                    cell.Attribute("t")?.Remove();
                    cell.Add(new XElement(XlNs + "v",
                        (dt - new DateTime(1899, 12, 30)).TotalDays
                            .ToString("R", CultureInfo.InvariantCulture)));
                    break;
                default:
                    WriteString(cell, value.ToString() ?? "");
                    break;
            }
        }

        private static void WriteNumber(XElement cell, double v)
        {
            // No t= attribute => numeric (default cell type).
            cell.Attribute("t")?.Remove();
            cell.Add(new XElement(XlNs + "v",
                v.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static void WriteString(XElement cell, string v)
        {
            // Inline string — avoids having to update the shared-strings
            // table (which requires recomputing every other cell's index
            // if we touched it).
            cell.SetAttributeValue("t", "inlineStr");
            cell.Add(new XElement(XlNs + "is",
                new XElement(XlNs + "t", v)));
        }

        /// <summary>Zip paths (e.g. "xl/worksheets/sheet4.xml") of the
        /// sheets whose tab name equals <paramref name="sheetName"/>.</summary>
        private static HashSet<string> SheetPathsNamed(ZipArchive zip, string sheetName)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wbEntry = zip.GetEntry("xl/workbook.xml");
            var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (wbEntry == null || relsEntry == null) return result;

            XDocument wb, rels;
            using (var s = wbEntry.Open()) wb = XDocument.Load(s);
            using (var s = relsEntry.Open()) rels = XDocument.Load(s);

            var targetById = rels.Root?.Elements(PkgRelNs + "Relationship")
                .ToDictionary(r => (string?)r.Attribute("Id") ?? "",
                              r => (string?)r.Attribute("Target") ?? "")
                ?? new Dictionary<string, string>();

            foreach (var sheet in wb.Descendants(XlNs + "sheet"))
            {
                if (!string.Equals((string?)sheet.Attribute("name"), sheetName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                string id = (string?)sheet.Attribute(RelNs + "id") ?? "";
                if (!targetById.TryGetValue(id, out var target) || target.Length == 0) continue;
                result.Add(target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target);
            }
            return result;
        }

        // ── Shared-strings table ────────────────────────────────────────────

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return new List<string>();
            using var s = entry.Open();
            var doc = XDocument.Load(s);
            var root = doc.Root;
            if (root == null) return new List<string>();
            // Each <si> element's effective text is the concatenation of
            // all its <t> descendants (handles rich-text runs too).
            return root.Elements(XlNs + "si")
                .Select(si => string.Concat(si.Descendants(XlNs + "t")
                    .Select(t => t.Value)))
                .ToList();
        }

        // ── Cell reference helpers ──────────────────────────────────────────

        /// <summary>Parses the column letters out of a cell reference
        /// like "AB12" and returns the 1-based column index (A=1, Z=26,
        /// AA=27, …). Returns 0 when <paramref name="cellRef"/> is empty
        /// or malformed.</summary>
        private static int ColumnFromCellRef(string cellRef)
        {
            int col = 0;
            foreach (char ch in cellRef)
            {
                if (!char.IsLetter(ch)) break;
                col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            return col;
        }

        /// <summary>Parses the row number out of a cell reference like
        /// "AB12". Returns 0 when no digits present.</summary>
        private static int ParseRow(string cellRef)
        {
            int i = 0;
            while (i < cellRef.Length && char.IsLetter(cellRef[i])) i++;
            return int.TryParse(cellRef.Substring(i), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int r) ? r : 0;
        }

        /// <summary>Converts a 1-based column index back to letters
        /// (1→A, 26→Z, 27→AA, …) for building cell references.</summary>
        private static string ColumnLetters(int col)
        {
            string s = "";
            while (col > 0)
            {
                col--;
                s = (char)('A' + col % 26) + s;
                col /= 26;
            }
            return s;
        }
    }
}
