using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Reads Autodesk Fabrication's supplier.map (a "MAP Compressed File 2005"
    /// zlib-wrapped binary) and extracts per-Product-Code material prices.
    ///
    /// Format observed: UTF-16-LE Product Code + null terminator immediately
    /// followed by an 8-byte IEEE 754 little-endian double (the price). The
    /// same product code can appear in multiple records (e.g. one per category
    /// it's referenced from); all carry the same price.
    /// </summary>
    public sealed class SupplierMapReader
    {
        private readonly byte[] _decompressed;
        private readonly Dictionary<string, double> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public SupplierMapReader(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("supplier.map not found", path);
            var raw = File.ReadAllBytes(path);
            var decoded = MapFileHelper.TryDecompress(raw, out _, out _);
            _decompressed = decoded
                ?? throw new InvalidOperationException(
                    $"Could not decompress '{path}' — file may not be a MAP Compressed File.");
        }

        /// <summary>
        /// Locates the first UTF-16-LE occurrence of the product code (null
        /// terminated) and returns the 8 bytes after it as a double. Result
        /// is cached for subsequent lookups of the same code. Returns null
        /// when the code isn't present.
        /// </summary>
        public double? Lookup(string productCode)
        {
            if (string.IsNullOrEmpty(productCode)) return null;
            if (_cache.TryGetValue(productCode, out double cached))
            {
                // Negative-result sentinel — see end of method.
                return double.IsNaN(cached) ? null : cached;
            }

            // UTF-16-LE encoded code + 2-byte null terminator (one wide char).
            byte[] needle = Encoding.Unicode.GetBytes(productCode);
            int needleLen = needle.Length + 2; // +2 for null wchar

            int max = _decompressed.Length - needleLen - 8;
            for (int i = 0; i <= max; i++)
            {
                if (!MatchesAtOffset(_decompressed, i, needle)) continue;
                // Confirm null terminator follows
                if (_decompressed[i + needle.Length]     != 0x00) continue;
                if (_decompressed[i + needle.Length + 1] != 0x00) continue;

                double v = BitConverter.ToDouble(_decompressed, i + needleLen);
                _cache[productCode] = v;
                return v;
            }
            _cache[productCode] = double.NaN; // mark as "scanned, not found"
            return null;
        }

        private static bool MatchesAtOffset(byte[] hay, int offset, byte[] needle)
        {
            for (int j = 0; j < needle.Length; j++)
                if (hay[offset + j] != needle[j]) return false;
            return true;
        }

        /// <summary>
        /// Walks the entire decompressed buffer and yields every plausible
        /// (Product Code, Price) pair. Heuristic — scans for runs of
        /// UTF-16-LE printable ASCII chars (high byte == 0, low byte
        /// printable) of reasonable length, terminated by a null wchar,
        /// immediately followed by an 8-byte LE double whose value is
        /// finite, non-negative, and under a sanity threshold.
        ///
        /// Codes are deduplicated — the same Product Code can appear in
        /// multiple records (one per category that references it); all
        /// carry the same price, so we keep the first.
        /// </summary>
        public IEnumerable<(string Code, double Price)> EnumerateAll()
        {
            const int    MinCodeChars = 3;
            const int    MaxCodeChars = 64;
            const double MaxPlausiblePrice = 10_000_000.0;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buf  = _decompressed;
            int n    = buf.Length;

            int i = 0;
            while (i < n - (MinCodeChars * 2 + 2 + 8))
            {
                // Find a candidate code start — printable ASCII char in
                // UTF-16-LE encoding. The high byte must be zero; the low
                // byte must be a sensible code character.
                if (buf[i + 1] != 0x00 || !IsCodeStartByte(buf[i]))
                { i++; continue; }

                // Scan ahead through valid UTF-16 ASCII chars.
                int chars = 0;
                int p = i;
                while (p + 1 < n &&
                       buf[p + 1] == 0x00 &&
                       IsCodeByte(buf[p]) &&
                       chars < MaxCodeChars)
                {
                    p += 2;
                    chars++;
                }

                if (chars < MinCodeChars) { i++; continue; }

                // Need a null wchar terminator and 8 bytes of price after.
                if (p + 2 + 8 > n)        { i++; continue; }
                if (buf[p]     != 0x00 || buf[p + 1] != 0x00) { i++; continue; }

                double price = BitConverter.ToDouble(buf, p + 2);
                if (double.IsNaN(price) || double.IsInfinity(price) ||
                    price < 0 || price > MaxPlausiblePrice)
                { i++; continue; }

                string code = Encoding.Unicode.GetString(buf, i, chars * 2);
                if (seen.Add(code))
                {
                    yield return (code, price);
                    _cache[code] = price;
                }

                // Skip past the record we just consumed.
                i = p + 2 + 8;
            }
        }

        // First byte of a Product Code: letter or digit. Excludes
        // underscore as a starter to reduce false positives, but allows
        // it inside the code (see IsCodeByte).
        private static bool IsCodeStartByte(byte b)
            => (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') ||
               (b >= '0' && b <= '9');

        // Subsequent bytes inside a Product Code: alphanumeric, dash,
        // underscore, period, slash. Tight enough to avoid catching
        // narrative text; loose enough for codes like "ADSK_30080696"
        // and "Pipe-B88-CU-L(PE)" (we accept '(' and ')' too).
        private static bool IsCodeByte(byte b)
            => IsCodeStartByte(b) ||
               b == '_' || b == '-' || b == '.' || b == '/' ||
               b == '(' || b == ')' || b == ' ';
    }
}
