using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Parses an Autodesk Fabrication <c>.ITM</c> (per-product item) file
    /// into entries binding each ADSK part code to the install/labour
    /// tables the part references.
    ///
    /// Binary structure (reverse-engineered against
    /// <c>1550CB2 (FLG).ITM</c> and <c>Pipe-B88-CU-L(PE).ITM</c>,
    /// decompressed via <see cref="MapFileHelper.TryDecompress"/>):
    ///
    /// Each .ITM has three macro-regions (offsets vary by part):
    /// <list type="bullet">
    /// <item><description><b>Header</b> — contains the part's content path,
    /// display name, size list, weights, and ONE OR MORE length-prefixed
    /// UTF-16 strings of the form <c>"[Group]Description"</c>, each
    /// naming an install or fabrication table.</description></item>
    /// <item><description><b>Per-size records</b> — N records, each ~350
    /// bytes, holding one ADSK product code (one per size) plus the
    /// size's physical attributes. Bracket-strings are NOT repeated
    /// per record; all sizes share the header's brackets.</description></item>
    /// <item><description><b>Ancillary fixings</b> — records for each
    /// bundled bolt/nut/gasket/washer, prefixed with
    /// <c>"[Ancillaries]Consumables"</c>. These have their own ADSK
    /// codes resolved separately by Phase A's etimes flat-list
    /// (<see cref="EtimesBreakpointParser.AncillaryMinsByProductCode"/>).</description></item>
    /// </list>
    ///
    /// <b>Group-name conventions seen in Fabrication Imperial 4.02:</b>
    /// <list type="bullet">
    /// <item><description><c>[Mechanical Valves]Foo</c> — valve install
    /// table (Foo = e.g. <c>VAL_Flange-150# (ASTM 16.5)</c>,
    /// <c>VAL_Welded</c>).</description></item>
    /// <item><description><c>[Mechanical Tubes]Foo</c> (plural) — pipe
    /// install table (Foo = e.g. <c>Copper - Type L</c>,
    /// <c>Carbon Steel - Sch40 - Plain End</c>).</description></item>
    /// <item><description><c>[Mechanical Tube]Foo</c> (singular) — pipe
    /// fabrication table (Foo = e.g. <c>Tube End - Cut Threads</c>).</description></item>
    /// <item><description><c>[Ancillaries]Consumables</c> — bundled
    /// fixing (skipped here; handled by the etimes flat-list).</description></item>
    /// </list>
    ///
    /// Verified against:
    /// <list type="bullet">
    /// <item><description><c>1550CB2 (FLG).ITM</c>: 1 part bracket
    /// <c>[Mechanical Valves]VAL_Flange-150# (ASTM 16.5)</c>; 14 part
    /// codes ADSK_30111281..30111294 (sizes 2"–30"); 22 fixing records.</description></item>
    /// <item><description><c>Pipe-B88-CU-L(PE).ITM</c>: 2 part brackets
    /// <c>[Mechanical Tube]Tube End - Cut Threads</c> AND
    /// <c>[Mechanical Tubes]Copper - Type L</c>; 18 part codes.</description></item>
    /// </list>
    /// </summary>
    public sealed class ItmFileParser
    {
        /// <summary>
        /// One non-Ancillaries bracket-string from the part header.
        /// </summary>
        public sealed record PartBracket(
            string Raw, string Group, string Description, int Offset);

        /// <summary>
        /// One ADSK part code with the part's table references it binds to.
        /// All sizes of a part share the same install/fab table references.
        /// </summary>
        public sealed record ItmEntry(
            string ProductCode,
            IReadOnlyList<PartBracket> PartBrackets,
            IReadOnlyList<string> AncillaryProductCodes)
        {
            /// <summary>
            /// The install-table key for this part — picks the first
            /// bracket whose group matches an install-table convention
            /// (<c>Mechanical Valves</c>, <c>Mechanical Tubes</c> [plural],
            /// <c>Mechanical Fittings</c>). Returns null for parts whose
            /// only brackets are fab-table refs.
            /// </summary>
            public string? InstallTableKey
            {
                get
                {
                    foreach (var b in PartBrackets)
                    {
                        if (IsInstallGroup(b.Group)) return b.Description;
                    }
                    return null;
                }
            }

            /// <summary>
            /// The fabrication-table key for this part — picks the first
            /// bracket whose group matches a fab-table convention
            /// (currently <c>Mechanical Tube</c> [singular]). Returns null
            /// for parts that aren't fabricated.
            /// </summary>
            public string? FabricationTableKey
            {
                get
                {
                    foreach (var b in PartBrackets)
                    {
                        if (IsFabricationGroup(b.Group)) return b.Description;
                    }
                    return null;
                }
            }

            /// <summary>
            /// The M-Rate price-list / supplier name selected for this
            /// part in the ITM's Costing tab (e.g. <c>"Harrison List
            /// Prices"</c>). Picks the first bracket whose group is NOT
            /// an install / fab / ancillaries group — that's where Fab
            /// appears to store the M-Rate dropdown selection. Returns
            /// null when no candidate bracket is present. Heuristic — if
            /// Fab stores this somewhere other than a bracket, this
            /// returns null.
            /// </summary>
            public string? SupplierName
            {
                get
                {
                    foreach (var b in PartBrackets)
                    {
                        if (IsInstallGroup(b.Group))     continue;
                        if (IsFabricationGroup(b.Group)) continue;
                        // Skip groups that look structural rather than
                        // supplier-ish (Sizes, Weights, etc.) — extend
                        // this list as new false positives surface.
                        if (string.Equals(b.Group, "Sizes",
                                StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(b.Group, "Weights",
                                StringComparison.OrdinalIgnoreCase)) continue;
                        return b.Description;
                    }
                    return null;
                }
            }
        }

        // Install-table group names. Mirrors the conventions observed in
        // Fabrication Imperial 4.02 — extend as new variants surface.
        //
        // Duct ITMs use ONE bracket group (e.g. [Duct Rectangular])
        // shared across install AND fabrication — the SAME table name
        // exists in both etimes.map (install) and ftimes.map (fab) with
        // different values. The lookup file chosen at consumption time
        // disambiguates them, so the group goes in BOTH whitelists.
        private static readonly HashSet<string> InstallGroups = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "Mechanical Valves",
            "Mechanical Tubes",      // plural — pipe install
            "Mechanical Fittings",
            "Mechanical Joints",
            "Mechanical Flanges",
            "Mechanical Hangers",
            // Ductwork: same group in both install + fab.
            "Duct Rectangular",
            "Duct Round",
            "Duct Oval",
            "Duct Flat Oval",
            "Ductboard",
        };

        // Fab-table group names.
        private static readonly HashSet<string> FabricationGroups = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "Mechanical Tube",        // singular — pipe fab (cut threads)
            // Ductwork: same group as install.
            "Duct Rectangular",
            "Duct Round",
            "Duct Oval",
            "Duct Flat Oval",
            "Ductboard",
        };

        private static bool IsInstallGroup(string group)     => InstallGroups.Contains(group);
        private static bool IsFabricationGroup(string group) => FabricationGroups.Contains(group);

        // Length bounds for any UTF-16 LE string we expect to read.
        private const int MinStringChars      = 1;
        private const int MaxStringChars      = 200;
        private const int MinProductCodeChars = 6;   // "ADSK_X" minimum
        private const int MaxProductCodeChars = 40;

        // Bracket-strings under this group identify ancillary FIXINGS
        // (bolts, nuts, washers, etc.) — not the part itself. The part's
        // install-table reference is the FIRST bracket-string that is NOT
        // in this group.
        private const string AncillariesGroup = "Ancillaries";

        private readonly Dictionary<string, ItmEntry> _byProductCode;

        /// <summary>{ProductCode → entry}. Case-insensitive lookup.</summary>
        public IReadOnlyDictionary<string, ItmEntry> Entries => _byProductCode;
        public int EntryCount => _byProductCode.Count;

        /// <summary>All entries in scan order. Useful for diagnostics.</summary>
        public IReadOnlyList<ItmEntry> EntriesInOrder { get; }

        /// <summary>
        /// All non-Ancillaries bracket-strings from the part header, in
        /// file order. A valve typically has one (install table); a pipe
        /// has two (install + fabrication). Empty for pure-fixings files.
        /// </summary>
        public IReadOnlyList<PartBracket> PartBrackets { get; }

        /// <summary>
        /// The first part bracket's group (e.g. <c>"Mechanical Valves"</c>),
        /// or null. Diagnostic shortcut; prefer
        /// <see cref="InstallTableKey"/> for lookup.
        /// </summary>
        public string? PartGroup => PartBrackets.Count > 0 ? PartBrackets[0].Group : null;

        /// <summary>
        /// Raw first part bracket — e.g.
        /// <c>"[Mechanical Valves]VAL_Flange-150# (ASTM 16.5)"</c>.
        /// Diagnostic shortcut.
        /// </summary>
        public string? PartBracketRaw =>
            PartBrackets.Count > 0 ? PartBrackets[0].Raw : null;

        /// <summary>The part's install-table key (etimes lookup) —
        /// e.g. <c>"VAL_Flange-150# (ASTM 16.5)"</c> or
        /// <c>"Copper - Type L"</c>. Null when no install-table bracket
        /// is present.</summary>
        public string? InstallTableKey
        {
            get
            {
                foreach (var b in PartBrackets)
                    if (IsInstallGroup(b.Group)) return b.Description;
                return null;
            }
        }

        /// <summary>The part's fabrication-table key (ftimes lookup) —
        /// e.g. <c>"Tube End - Cut Threads"</c>. Null when the part
        /// isn't fabricated.</summary>
        public string? FabricationTableKey
        {
            get
            {
                foreach (var b in PartBrackets)
                    if (IsFabricationGroup(b.Group)) return b.Description;
                return null;
            }
        }

        public ItmFileParser(string itmPath)
        {
            if (string.IsNullOrWhiteSpace(itmPath))
                throw new ArgumentException("itmPath is empty", nameof(itmPath));
            if (!File.Exists(itmPath))
                throw new FileNotFoundException(
                    $"{Path.GetFileName(itmPath)} not found", itmPath);

            var raw = File.ReadAllBytes(itmPath);
            var buf = MapFileHelper.TryDecompress(raw, out _, out _)
                ?? throw new InvalidOperationException(
                    $"Could not decompress '{itmPath}' — not a MAP-zlib stream?");

            // 1. Find every bracket-string (length-prefixed UTF-16
            //    "[Group]Description") and every ADSK product code.
            var brackets = FindBracketStrings(buf);
            var codes    = FindProductCodes(buf);

            // 2. Collect ALL non-Ancillaries brackets as the part's
            //    table references. A valve typically has one; a pipe
            //    has two (install + fab); a multi-product template
            //    (e.g. GV+ST.ITM) has several with codes interleaved
            //    between them. Track the first [Ancillaries] offset
            //    so we can stop collecting part codes there.
            var partBrackets           = new List<PartBracket>();
            int firstAncBracketOffset  = int.MaxValue;
            foreach (var b in brackets)
            {
                if (string.Equals(b.Group, AncillariesGroup,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (b.Offset < firstAncBracketOffset)
                        firstAncBracketOffset = b.Offset;
                }
                else
                {
                    partBrackets.Add(new PartBracket(
                        b.Raw, b.Group, b.Description, b.Offset));
                }
            }
            PartBrackets = partBrackets;

            // 3. Walk brackets and codes in file order, maintaining a
            //    "current bracket group". Each code binds to whatever
            //    brackets currently belong to its sub-product.
            //
            //    Heuristic for grouping (validated against pipe ITMs +
            //    GV+ST template):
            //      • Consecutive brackets with no codes between them
            //        are siblings (same product). Pipe ITM:
            //        [Mechanical Tube] + [Mechanical Tubes] both apply
            //        to every code that follows.
            //      • A bracket appearing AFTER a code marks a new
            //        sub-product. GV+ST.ITM has [Strainer-Flanged]
            //        then strainer codes, then [VAL_Flange-150#] then
            //        valve codes — each set binds only to its own
            //        bracket, NOT to the previous one.
            //
            //    Per-code filters:
            //      (a) skip codes at-or-after the first [Ancillaries]
            //          (those are fixings — etimes flat-list handles
            //          them);
            //      (b) skip codes immediately followed by an
            //          [Ancillaries] bracket (code-then-bracket fixing
            //          record pattern);
            //      First-wins on duplicate codes.
            const int FixingProximityBytes = 64;
            var ordered = new List<ItmEntry>();
            var byCode  = new Dictionary<string, ItmEntry>(
                StringComparer.OrdinalIgnoreCase);
            // Codes that get filtered out as ancillary fixings — kept
            // here so MaterialEstimateCommand can price native RFA
            // accessories' ancillary kits via supplier.map without
            // FabricationPart.GetPartAncillaryUsage being available.
            // First-wins per code keeps the list compact for ITMs that
            // repeat the same fixing per size.
            var ancCodes    = new List<string>();
            var ancCodesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int bi = 0, ci = 0;
            var currentGroup = new List<PartBracket>();
            bool seenCodeSinceLastBracket = false;
            while (bi < partBrackets.Count || ci < codes.Count)
            {
                int bracketOffset = bi < partBrackets.Count
                    ? partBrackets[bi].Offset : int.MaxValue;
                int codeOffset = ci < codes.Count
                    ? codes[ci].Offset : int.MaxValue;

                if (bracketOffset <= codeOffset)
                {
                    if (seenCodeSinceLastBracket)
                    {
                        currentGroup.Clear();
                        seenCodeSinceLastBracket = false;
                    }
                    currentGroup.Add(partBrackets[bi]);
                    bi++;
                }
                else
                {
                    var (code, off) = codes[ci];
                    ci++;
                    // Past the first [Ancillaries] marker: every code
                    // from here is a fixing. Collect them for the
                    // ancillary kit list and keep iterating (was break).
                    if (off >= firstAncBracketOffset)
                    {
                        if (ancCodesSet.Add(code)) ancCodes.Add(code);
                        continue;
                    }
                    if (currentGroup.Count == 0) continue;
                    // Code immediately followed by an [Ancillaries]
                    // bracket = "code-then-bracket" fixing record.
                    if (HasAncillaryBracketNear(brackets, off,
                            FixingProximityBytes))
                    {
                        if (ancCodesSet.Add(code)) ancCodes.Add(code);
                        continue;
                    }
                    if (byCode.ContainsKey(code)) continue;

                    var entry = new ItmEntry(code,
                        new List<PartBracket>(currentGroup),
                        // Ancillary list isn't fully known yet; attach
                        // empty for now. Backfilled after the loop.
                        Array.Empty<string>());
                    ordered.Add(entry);
                    byCode[code] = entry;
                    seenCodeSinceLastBracket = true;
                }
            }
            // Backfill: rewrite every entry with the final ancillary
            // list. All sizes in an ITM share the same kit (multi-
            // product templates like GV+ST.ITM get the union of all
            // sub-product kits — acceptable overestimate for v1).
            var ancList = (IReadOnlyList<string>)ancCodes;
            for (int i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i];
                if (ancList.Count > 0)
                {
                    var updated = e with { AncillaryProductCodes = ancList };
                    ordered[i] = updated;
                    byCode[e.ProductCode] = updated;
                }
            }
            EntriesInOrder = ordered;
            _byProductCode = byCode;
            AncillaryProductCodes = ancList;
        }

        /// <summary>
        /// All ancillary product codes declared by this ITM file —
        /// bolts, nuts, gaskets, washers, sealants etc. The list is
        /// SHARED across every <see cref="ItmEntry"/> in the file.
        /// </summary>
        public IReadOnlyList<string> AncillaryProductCodes { get; }

        /// <summary>Returns the entry for an ADSK Product Code, or null.</summary>
        public ItmEntry? GetByProductCode(string? productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode)) return null;
            return _byProductCode.TryGetValue(productCode.Trim(), out var e)
                ? e : null;
        }

        // ── Parsing ─────────────────────────────────────────────────────────

        private readonly record struct Bracket(
            int Offset, string Raw, string Group, string Description);

        /// <summary>
        /// True when an <c>[Ancillaries]</c> bracket-string starts within
        /// <paramref name="maxBytesAfter"/> of <paramref name="afterOffset"/>.
        /// Used to detect the "code-then-bracket" pattern that marks each
        /// ancillary fixing record.
        /// </summary>
        private static bool HasAncillaryBracketNear(IReadOnlyList<Bracket> brackets,
            int afterOffset, int maxBytesAfter)
        {
            int upper = afterOffset + maxBytesAfter;
            foreach (var b in brackets)
            {
                if (b.Offset < afterOffset) continue;
                if (b.Offset > upper) break;
                if (string.Equals(b.Group, AncillariesGroup,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Sweeps the buffer for length-prefixed UTF-16 strings of the
        /// form <c>"[Group]Description"</c> — both halves non-empty.
        /// Returns hits in ascending file-offset order.
        /// </summary>
        private static List<Bracket> FindBracketStrings(byte[] buf)
        {
            var hits = new List<Bracket>();
            int n = buf.Length;
            for (int i = 0; i + 6 < n; i++)
            {
                if (!TryReadLengthPrefixedString(buf, i, out string? s, out int afterEnd,
                        printableAscii: true))
                    continue;
                if (s == null || s.Length < 3 || s[0] != '[') continue;
                int close = s.IndexOf(']');
                if (close <= 0 || close >= s.Length - 1) continue;

                hits.Add(new Bracket(i, s, s.Substring(1, close - 1),
                                     s.Substring(close + 1)));
                i = afterEnd - 1; // skip past consumed bytes
            }
            return hits;
        }

        /// <summary>
        /// Sweeps the buffer for ADSK product codes — length-prefixed
        /// UTF-16 strings of the form <c>"ADSK_[0-9]+"</c>. Returns
        /// (code, lengthPrefixOffset) pairs in ascending file-offset order.
        /// </summary>
        private static List<(string Code, int Offset)> FindProductCodes(byte[] buf)
        {
            var hits = new List<(string, int)>();
            int n = buf.Length;
            for (int i = 0; i + 4 + MinProductCodeChars * 2 + 2 <= n; i++)
            {
                if (!TryReadAdskCode(buf, i, out string? code, out int afterEnd))
                    continue;
                hits.Add((code!, i));
                i = afterEnd - 1;
            }
            return hits;
        }

        /// <summary>
        /// Reads a length-prefixed UTF-16 LE string at <paramref name="i"/>
        /// and validates it matches <c>"ADSK_[0-9]+"</c>.
        /// </summary>
        private static bool TryReadAdskCode(byte[] buf, int i,
            out string? code, out int afterEnd)
        {
            code = null;
            afterEnd = 0;
            if (i + 4 > buf.Length) return false;

            int len = BitConverter.ToInt32(buf, i);
            int chars = len - 1;
            if (chars < MinProductCodeChars || chars > MaxProductCodeChars)
                return false;

            int strBytes = chars * 2;
            int end = i + 4 + strBytes;
            if (end + 2 > buf.Length) return false;

            // First 5 chars must be exactly "ADSK_".
            if (chars < 5) return false;
            if (buf[i +  4] != (byte)'A' || buf[i +  5] != 0x00) return false;
            if (buf[i +  6] != (byte)'D' || buf[i +  7] != 0x00) return false;
            if (buf[i +  8] != (byte)'S' || buf[i +  9] != 0x00) return false;
            if (buf[i + 10] != (byte)'K' || buf[i + 11] != 0x00) return false;
            if (buf[i + 12] != (byte)'_' || buf[i + 13] != 0x00) return false;

            // Remaining chars: digits only.
            for (int k = 5; k < chars; k++)
            {
                byte lo = buf[i + 4 + k * 2];
                byte hi = buf[i + 4 + k * 2 + 1];
                if (hi != 0x00) return false;
                if (lo < (byte)'0' || lo > (byte)'9') return false;
            }
            if (buf[end] != 0x00 || buf[end + 1] != 0x00) return false;

            code = Encoding.Unicode.GetString(buf, i + 4, strBytes);
            afterEnd = end + 2;
            return true;
        }

        /// <summary>
        /// Reads a length-prefixed UTF-16 LE string. Format:
        /// <code>
        ///   int32  charCountInclNull
        ///   wchar  text[charCountInclNull-1]
        ///   wchar  0x0000
        /// </code>
        /// When <paramref name="printableAscii"/> is true, each char must
        /// be in <c>0x20..0x7E</c> — the practical character set used in
        /// Fab item descriptions and codes.
        /// </summary>
        private static bool TryReadLengthPrefixedString(byte[] buf, int i,
            out string? s, out int afterEnd, bool printableAscii)
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
                if (printableAscii && (lo < 0x20 || lo > 0x7E)) return false;
            }
            if (buf[end] != 0x00 || buf[end + 1] != 0x00) return false;

            s = Encoding.Unicode.GetString(buf, i + 4, strBytes);
            afterEnd = end + 2;
            return true;
        }
    }
}
