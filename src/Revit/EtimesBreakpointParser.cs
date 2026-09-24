using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Parses Autodesk Fabrication's etimes.map (Installation Times) and
    /// the structurally-identical ftimes.map (Fabrication Times) into a
    /// {TableName → [(Size, Minutes), …]} dictionary.
    ///
    /// Binary format (reverse-engineered from a wide hex dump):
    /// Each breakpoint table is a self-contained block anchored on a
    /// 4-byte signature `7B 27 2F 00`. Layout from the signature byte:
    ///
    ///   sig+0   7B 27 2F 00                signature
    ///   sig+4   block_size_1   int32       bytes from sig+4 to end of block
    ///   sig+8   block_size_2   int32       = block_size_1 - 4
    ///   sig+12  N              int32       breakpoint count
    ///   sig+16  flag1          int32       = 1
    ///   sig+20  flag2          int32       = 1
    ///   sig+24  [N × 8-byte SIZES]         e.g., 0.125, 0.25, …, 8.0
    ///   …       [8 bytes 0x00 separator]
    ///   …       [N × 8-byte MINUTES]       e.g., 8.0, 9.0, …, 220.0
    ///
    /// Each block is preceded (just before sig) by length-prefixed UTF-16
    /// NAME and CATEGORY strings, plus a ~60-byte header. The parser walks
    /// the buffer for every signature occurrence, reads its sizes+minutes,
    /// and walks back to the nearest preceding (name, category) pair to
    /// label the table.
    /// </summary>
    public sealed class EtimesBreakpointParser
    {
        public sealed record TableEntry(string Name, string Category,
            IReadOnlyList<(double Size, double Minutes)> Breakpoints)
        {
            /// <summary>
            /// Returns the minutes for a target size: exact match wins,
            /// else picks the smallest breakpoint ≥ size (Fab convention:
            /// "size or up to"). Returns null when size exceeds every
            /// breakpoint.
            ///
            /// Handles the etimes-vs-ftimes unit difference: etimes
            /// tables store MINUTES (e.g., Copper - Type L at 2" → 5.4
            /// mins), ftimes tables store HOURS (Tube End - Cut Threads
            /// at 4" → 0.066 hrs). The fab-config UI shows the unit per
            /// table; the binary doesn't expose it directly. Detect via
            /// magnitude heuristic — if the table's maximum value is
            /// below 5, assume hours and convert to minutes. (Installation
            /// tables have max values 10–500+ mins; fabrication tables
            /// stay under 1 hr/unit for typical pipe sizes.)
            /// </summary>
            public double? GetMinutesForSize(double size)
            {
                if (Breakpoints == null || Breakpoints.Count == 0) return null;
                foreach (var (s, m) in Breakpoints)
                {
                    if (s + 1e-6 >= size)
                        return IsHoursTable ? m * 60.0 : m;
                }
                return null;
            }

            /// <summary>
            /// True when the table's values are HOURS rather than MINUTES.
            ///
            /// Detection rules (in priority order):
            ///   1. Categories starting with <c>"Duct "</c> are ALWAYS
            ///      in hours per Fab UI's Units dropdown. Bumping the
            ///      heuristic threshold doesn't help because large ducts
            ///      can store hours values &gt; 5 (e.g. 7.625 hrs/ft for a
            ///      320" duct in <c>Rectangular Straight</c>).
            ///   2. Otherwise: max value &lt; 5 → hours. Works for the
            ///      pipe fab table (<c>Tube End - Cut Threads</c> tops
            ///      out around 0.066 hrs) and for the small etimes
            ///      install tables (mins ~10-500 range).
            /// </summary>
            public bool IsHoursTable
            {
                get
                {
                    if (Breakpoints == null || Breakpoints.Count == 0) return false;
                    if (!string.IsNullOrEmpty(Category) &&
                        Category.StartsWith("Duct ", StringComparison.OrdinalIgnoreCase))
                        return true;
                    double max = 0;
                    foreach (var (_, m) in Breakpoints)
                        if (m > max) max = m;
                    return max < 5.0;
                }
            }
        }

        private readonly Dictionary<string, TableEntry> _byName;
        private readonly Dictionary<string, double> _ancillaryMinsByProductCode;
        public IReadOnlyDictionary<string, TableEntry> Tables => _byName;
        public int TableCount => _byName.Count;

        /// <summary>
        /// Per-ancillary fixing minutes, keyed by Product Code (e.g.
        /// <c>ADSK_80001383</c> → <c>0.25</c> mins/qty for a 3/4" bolt).
        /// Parsed from the flat-list records in etimes.map that sit
        /// alongside the breakpoint tables — these hold the per-unit
        /// labour for bolts/nuts/gaskets/washers/etc. that Fab ESTmep
        /// shows under the "Fixings" group in a Cost Breakdown view.
        /// </summary>
        public IReadOnlyDictionary<string, double> AncillaryMinsByProductCode
            => _ancillaryMinsByProductCode;
        public int AncillaryCount => _ancillaryMinsByProductCode.Count;

        public EtimesBreakpointParser(string mapPath)
        {
            if (!File.Exists(mapPath))
                throw new FileNotFoundException(
                    $"{Path.GetFileName(mapPath)} not found", mapPath);
            var raw = File.ReadAllBytes(mapPath);
            var decompressed = MapFileHelper.TryDecompress(raw, out _, out _)
                ?? throw new InvalidOperationException(
                    $"Could not decompress '{mapPath}'.");
            _byName = ScanAllTables(decompressed);
            _ancillaryMinsByProductCode = ScanAncillaryRecords(decompressed);
        }

        public TableEntry? GetTable(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName)) return null;
            return _byName.TryGetValue(tableName.Trim(), out var t) ? t : null;
        }

        /// <summary>
        /// Direct per-qty minutes lookup for an ancillary's Product Code.
        /// Returns null when the code isn't in the flat-list section.
        /// </summary>
        public double? GetAncillaryMinsForProductCode(string? productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode)) return null;
            return _ancillaryMinsByProductCode.TryGetValue(productCode.Trim(), out var v)
                ? v : (double?)null;
        }

        // ── Parsing ─────────────────────────────────────────────────────────

        // Signature bytes that anchor every breakpoint table.
        private static readonly byte[] Signature = { 0x7B, 0x27, 0x2F, 0x00 };

        private const int MinBreakpoints = 2;
        private const int MaxBreakpoints = 100;
        private const int MinStringChars = 4;
        // Bumped from 100 → 1000 to handle multi-line description strings
        // sandwiched between NAME and CATEGORY. Sample: "Strainer - Flanged"
        // has a 137-char description "Data extracted from:\nSpons Mechanical
        // and Electrical Services Price Book 2011\n...". Cap at 1000 to
        // still reject runaway false-positive scans into random data.
        private const int MaxStringChars = 1000;
        private const double MaxPlausibleMinutes = 10_000.0;
        private const double MaxPlausibleSize    = 500.0;     // inches/mm range

        private static Dictionary<string, TableEntry> ScanAllTables(byte[] buf)
        {
            var result = new Dictionary<string, TableEntry>(StringComparer.OrdinalIgnoreCase);
            int n = buf.Length;

            // Sweep for signature occurrences. Each is the start of one
            // breakpoint table.
            for (int sig = 0; sig + 24 < n; sig++)
            {
                if (buf[sig]     != Signature[0]) continue;
                if (buf[sig + 1] != Signature[1]) continue;
                if (buf[sig + 2] != Signature[2]) continue;
                if (buf[sig + 3] != Signature[3]) continue;

                // Read N from the header. Two binary layouts seen:
                //
                //   PIPES / VALVES / FITTINGS  (sig+12 = N, sig+16 = 1)
                //     N sizes  | 8-byte zero separator | N minutes
                //
                //   DUCTS                       (sig+12 = 1, sig+16 = N)
                //     1 zero double (sentinel)  | N sizes | N minutes
                //
                // Both layouts produce identical block sizes (16N+8 bytes
                // of data) but the data interpretation differs.
                //
                // Discriminator: whichever of sig+12 / sig+16 equals 1
                // tells us the "non-breakpoint" axis. The OTHER field is
                // the real breakpoint count.
                if (sig + 20 > n) break;
                int n12 = BitConverter.ToInt32(buf, sig + 12);
                int n16 = BitConverter.ToInt32(buf, sig + 16);

                int N;
                int sizesStart;
                int minutesStart;
                if (n16 == 1 && n12 >= MinBreakpoints && n12 <= MaxBreakpoints)
                {
                    // Pipe layout.
                    N            = n12;
                    sizesStart   = sig + 24;
                    minutesStart = sizesStart + N * 8 + 8;   // skip separator
                }
                else if (n12 == 1 && n16 >= MinBreakpoints && n16 <= MaxBreakpoints)
                {
                    // Duct layout.
                    N            = n16;
                    sizesStart   = sig + 24 + 8;             // skip leading 0.0
                    minutesStart = sizesStart + N * 8;       // no separator
                }
                else continue;

                int minutesEnd = minutesStart + N * 8;
                if (minutesEnd > n) continue;

                // Sizes: must be N positive monotonically-increasing doubles.
                var sizes = new List<double>(N);
                if (!ReadAndValidateMonotonicArray(buf, sizesStart, N,
                        0.0, MaxPlausibleSize, sizes)) continue;

                // Minutes: N doubles, plausible (≥ 0, ≤ MaxPlausibleMinutes).
                // Don't require strict monotonicity here — labour tables
                // are usually monotonic but a flat plateau between sizes
                // (same minutes for two consecutive breakpoints) is legal.
                var minutes = new List<double>(N);
                if (!ReadPlausibleArray(buf, minutesStart, N,
                        0.0, MaxPlausibleMinutes, minutes)) continue;

                // Find the nearest (NAME, CATEGORY) preceding the signature.
                if (!TryFindHeaderBefore(buf, sig, out string? name,
                        out string? category))
                    continue;

                var pairs = new List<(double Size, double Minutes)>(N);
                for (int k = 0; k < N; k++) pairs.Add((sizes[k], minutes[k]));

                // First occurrence wins on duplicate names.
                if (!result.ContainsKey(name!))
                    result[name!] = new TableEntry(name!, category!, pairs);

                // Skip past consumed block to avoid double-counting.
                sig = minutesEnd - 1;
            }
            return result;
        }

        /// <summary>
        /// Walks BACKWARD from <paramref name="beforeOffset"/> looking
        /// for the nearest <i>run</i> of consecutive length-prefixed
        /// UTF-16 strings — each within ~12 bytes of the previous one.
        /// The run's FIRST entry is NAME, LAST is CATEGORY.
        ///
        /// Two formats are seen in etimes.map:
        /// <list type="bullet">
        /// <item><description><b>2-string</b> run (name, category) —
        /// most tables, e.g. <c>VAL_Welded</c> →
        /// <c>Mechanical Valves</c>.</description></item>
        /// <item><description><b>3-string</b> run (name, description,
        /// category) — tables with an embedded comment, e.g.
        /// <c>VAL_Flange-150# (ASTM 16.5)</c> +
        /// <c>"Time allowed for alligning flange faces."</c> +
        /// <c>Mechanical Valves</c>. The middle string is discarded.</description></item>
        /// </list>
        /// Walking the run from first to last lets the same code handle
        /// both — we never confuse a description for the name.
        /// </summary>
        private static bool TryFindHeaderBefore(byte[] buf, int beforeOffset,
            out string? name, out string? category)
        {
            name = null;
            category = null;

            const int SearchWindow = 600;
            int searchStart = Math.Max(0, beforeOffset - SearchWindow);

            // The LAST run that ends before beforeOffset is the table's
            // header. Track per-run first + last; reset on a gap > 12B.
            string? runFirst = null;
            string? runLast  = null;
            int     runLen   = 0;
            int     prevAfter = -1;

            string? lastFirst    = null;
            string? lastLast     = null;
            int     lastRunLen   = 0;

            int i = searchStart;
            while (i + 6 < beforeOffset)
            {
                if (TryReadLengthPrefixedString(buf, i, out string? s, out int afterEnd) &&
                    afterEnd <= beforeOffset)
                {
                    bool consecutive = prevAfter >= 0 && (i - prevAfter <= 12);
                    if (!consecutive)
                    {
                        runFirst = s;
                        runLen   = 1;
                    }
                    else
                    {
                        runLen++;
                    }
                    runLast = s;
                    if (runLen >= 2)
                    {
                        lastFirst  = runFirst;
                        lastLast   = runLast;
                        lastRunLen = runLen;
                    }
                    prevAfter = afterEnd;
                    i = afterEnd;
                }
                else i++;
            }
            name = lastFirst;
            category = lastLast;
            return name != null && category != null;
        }

        // ── Ancillary flat-list records ─────────────────────────────────────

        // Plausibility window for per-ancillary fixing minutes. Observed
        // values in Imperial 4.02 cluster at 0.25 (bolts, nuts, washers),
        // 1.5 (gaskets), up to ~5 for larger fixings. Cap at 100 to filter
        // garbage bytes that happen to decode to a finite double.
        private const double MinAncillaryMins = 0.0;
        private const double MaxAncillaryMins = 100.0;
        private const int    MinProductCodeChars = 4;
        private const int    MaxProductCodeChars = 40;

        /// <summary>
        /// Sweeps the decompressed buffer for per-ancillary records of the
        /// shape:
        /// <code>
        ///   int32  len           = char count incl. null  (4 ≤ len ≤ 41)
        ///   wchar  code[len-1]   product code, ASCII subset
        ///   wchar  null          terminator
        ///   double minsPerQty    8-byte LE, finite, 0 ≤ v ≤ 100
        /// </code>
        /// First occurrence wins on duplicate codes (matches Fab's "first
        /// override" behaviour seen elsewhere in these files).
        ///
        /// Confirmed against ETimes.MAP @ 2026-05-16 scan:
        ///   ADSK_80001383 (3/4" Bolt) = 0.25 mins/qty
        ///   ADSK_80001390 (3/4" Nut)  = 0.25 mins/qty
        ///   ADSK_80001542 (3/4" Gasket) = 1.5  mins/qty (extrapolated from
        ///                              the 6.6 mins Fab UI shows × 2 conn)
        /// </summary>
        private static Dictionary<string, double> ScanAncillaryRecords(byte[] buf)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            int n = buf.Length;

            // Each record starts at a 4-byte length prefix. Scan every
            // possible offset; the read-and-validate gate is cheap (length
            // bounds + char-range walk) and rejects non-records fast.
            for (int i = 0; i + 4 + MinProductCodeChars * 2 + 2 + 8 <= n; i++)
            {
                if (!TryReadProductCode(buf, i, out string? code, out int afterEnd))
                    continue;
                if (afterEnd + 8 > n) continue;

                double v = BitConverter.ToDouble(buf, afterEnd);
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (v < MinAncillaryMins || v > MaxAncillaryMins) continue;

                // First occurrence wins. Skip past the consumed bytes so
                // we don't waste cycles re-scanning inside the record.
                if (!result.ContainsKey(code!))
                {
                    result[code!] = v;
                    i = afterEnd + 7;
                }
            }
            return result;
        }

        /// <summary>
        /// Like <see cref="TryReadLengthPrefixedString"/> but with the
        /// stricter char-set we expect for a Product Code: ASCII letters,
        /// digits, <c>_</c>, <c>-</c>. Rejects table names (which contain
        /// spaces / parens / hashes), keeping the scan focused on
        /// per-ancillary records.
        /// </summary>
        private static bool TryReadProductCode(byte[] buf, int i,
            out string? code, out int afterEnd)
        {
            code = null;
            afterEnd = 0;
            if (i + 4 > buf.Length) return false;

            int len = BitConverter.ToInt32(buf, i);
            int chars = len - 1;
            if (chars < MinProductCodeChars || chars > MaxProductCodeChars) return false;

            int strBytes = chars * 2;
            int end = i + 4 + strBytes;
            if (end + 2 > buf.Length) return false;

            for (int k = 0; k < strBytes; k += 2)
            {
                byte lo = buf[i + 4 + k];
                byte hi = buf[i + 4 + k + 1];
                if (hi != 0x00) return false;
                // Letters, digits, underscore, hyphen only.
                bool letter = (lo >= 'A' && lo <= 'Z') || (lo >= 'a' && lo <= 'z');
                bool digit  = lo >= '0' && lo <= '9';
                bool punct  = lo == '_' || lo == '-';
                if (!letter && !digit && !punct) return false;
            }
            if (buf[end] != 0x00 || buf[end + 1] != 0x00) return false;

            code = Encoding.Unicode.GetString(buf, i + 4, strBytes);
            afterEnd = end + 2;
            return true;
        }

        private static bool TryReadLengthPrefixedString(byte[] buf, int i,
            out string? s, out int afterEnd)
        {
            s = null;
            afterEnd = 0;
            if (i + 4 > buf.Length) return false;

            int len = BitConverter.ToInt32(buf, i);
            int chars = len - 1;
            if (chars < MinStringChars || chars > MaxStringChars) return false;

            int strBytes = chars * 2;
            int end = i + 4 + strBytes;
            if (end + 2 > buf.Length) return false;

            for (int k = 0; k < strBytes; k += 2)
            {
                byte lo = buf[i + 4 + k];
                byte hi = buf[i + 4 + k + 1];
                if (hi != 0x00) return false;
                // Printable ASCII OR common whitespace (tab, LF, CR).
                // Description strings in etimes/ftimes are multi-line —
                // e.g. "Data extracted from:\nSpons Mechanical..." for
                // Strainer - Flanged. Rejecting 0x0A would drop the
                // whole table's NAME→DESCRIPTION→CATEGORY run.
                bool printable = lo >= 0x20 && lo <= 0x7E;
                bool ws        = lo == 0x09 || lo == 0x0A || lo == 0x0D;
                if (!printable && !ws) return false;
            }
            if (buf[end] != 0x00 || buf[end + 1] != 0x00) return false;

            s = Encoding.Unicode.GetString(buf, i + 4, strBytes);
            afterEnd = end + 2;
            return true;
        }

        /// <summary>
        /// Reads <paramref name="count"/> 8-byte LE doubles starting at
        /// <paramref name="offset"/>. Validates each is finite, > minValue,
        /// ≤ maxValue, AND non-decreasing relative to the previous value.
        ///
        /// Equal-adjacent breakpoints ARE legal — Fab uses them to encode
        /// multiple labour values at the same size (e.g.
        /// "Ball Valve - Flanged" has sizes 0.5, 0.5, 0.75, 0.75, 2.5, 2.5
        /// for paired condition rules — same size, different mins). A
        /// strict-greater-than check rejected the whole table here, but
        /// non-decreasing keeps the garbage-filter strong enough since
        /// random byte sequences almost never produce a monotonic run of
        /// 25+ plausible doubles.
        /// </summary>
        private static bool ReadAndValidateMonotonicArray(byte[] buf, int offset,
            int count, double minValue, double maxValue, List<double> outArr)
        {
            outArr.Clear();
            double last = -1;
            for (int i = 0; i < count; i++)
            {
                double v = BitConverter.ToDouble(buf, offset + i * 8);
                if (double.IsNaN(v) || double.IsInfinity(v)) return false;
                if (v <= minValue || v > maxValue)            return false;
                if (v < last)                                  return false;
                outArr.Add(v);
                last = v;
            }
            return true;
        }

        /// <summary>
        /// Reads <paramref name="count"/> 8-byte LE doubles, validating
        /// each is finite, ≥ minValue, ≤ maxValue. Does NOT require
        /// monotonicity — labour-minute arrays are usually monotonic but
        /// not always, and we don't want to reject genuine tables.
        /// </summary>
        private static bool ReadPlausibleArray(byte[] buf, int offset,
            int count, double minValue, double maxValue, List<double> outArr)
        {
            outArr.Clear();
            for (int i = 0; i < count; i++)
            {
                double v = BitConverter.ToDouble(buf, offset + i * 8);
                if (double.IsNaN(v) || double.IsInfinity(v))   return false;
                if (v < minValue || v > maxValue)              return false;
                outArr.Add(v);
            }
            return true;
        }
    }
}
