using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Reads Autodesk Fabrication's cost.map — the Labour Rates table —
    /// and surfaces a {LabourTypeName → $/hr} dictionary. Distinct from
    /// supplier.map: cost.map is small (kilobytes) and not keyed by
    /// Product Code. It stores per-hour rates for each labour type
    /// defined in the fab config (Skilled, Low Skilled, High Skilled,
    /// "Tradesman Sheet Metal Avg", "Piping - INSTL", etc.).
    ///
    /// Binary format (reverse-engineered from a wide hex dump):
    /// Each labour-rate record is anchored on the 4-byte signature
    /// `8B 02 12 00`. Layout from the signature byte:
    ///
    ///   sig+0   8B 02 12 00                signature
    ///   sig+4   record_size    int32       total record bytes
    ///   sig+8   record_size-4  int32
    ///   sig+12  flag           int32       varies (usually 0 or 4)
    ///   sig+16  rate           double      $/hr
    ///   sig+24  name_length    int32       chars including null terminator
    ///   sig+28  name           UTF-16-LE   (name_length-1 chars + null)
    ///   …       padding to next record
    ///
    /// The file contains TWO sets of records — Fabrication labour types
    /// followed by Installation labour types — with (in the configs we've
    /// seen) identical names and rates. We dedup by name; if rates ever
    /// diverge between sections this would need to track sections
    /// separately.
    ///
    /// The first record in each section has name_length=1 (empty string).
    /// That's the implicit default labour type — the Fabrication UI
    /// labels it "Skilled". We assign the same name here.
    /// </summary>
    public sealed class CostMapReader
    {
        private readonly List<LabourRate> _all;
        public IReadOnlyList<LabourRate> All => _all;

        public sealed record LabourRate(string Name, double RatePerHour);

        public CostMapReader(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("cost.map not found", path);
            var raw = File.ReadAllBytes(path);
            var decoded = MapFileHelper.TryDecompress(raw, out _, out _)
                ?? throw new InvalidOperationException(
                    $"Could not decompress '{path}'.");
            _all = ScanForRates(decoded);
        }

        public double? GetRate(string labourTypeName)
        {
            if (string.IsNullOrEmpty(labourTypeName)) return null;
            foreach (var r in _all)
            {
                if (string.Equals(r.Name, labourTypeName,
                        StringComparison.OrdinalIgnoreCase))
                    return r.RatePerHour;
            }
            return null;
        }

        // ── Parsing ─────────────────────────────────────────────────────────

        // Default name for the first record's empty-string entry — matches
        // the label the Fab Database UI shows.
        private const string DefaultLabourName = "Skilled";

        private static readonly byte[] Signature = { 0x8B, 0x02, 0x12, 0x00 };

        private static List<LabourRate> ScanForRates(byte[] buf)
        {
            var result = new List<LabourRate>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = buf.Length;

            int i = 0;
            while (i + 28 < n)
            {
                if (buf[i]     != Signature[0] ||
                    buf[i + 1] != Signature[1] ||
                    buf[i + 2] != Signature[2] ||
                    buf[i + 3] != Signature[3])
                { i++; continue; }

                // Rate at sig+16.
                double rate = BitConverter.ToDouble(buf, i + 16);
                if (double.IsNaN(rate) || double.IsInfinity(rate) ||
                    rate < 0 || rate > 10_000.0)
                { i++; continue; }

                // Name length at sig+24 (char-count including null terminator).
                int len = BitConverter.ToInt32(buf, i + 24);
                int chars = len - 1;
                if (chars < 0 || chars > 100)
                { i++; continue; }

                string name;
                int strBytes = chars * 2;
                if (chars == 0)
                {
                    name = DefaultLabourName;
                }
                else
                {
                    if (i + 28 + strBytes + 2 > n) { i++; continue; }
                    if (!IsPrintableUtf16(buf, i + 28, strBytes)) { i++; continue; }
                    name = Encoding.Unicode.GetString(buf, i + 28, strBytes);
                    // The "0" entry in some configs is a literal "-" placeholder
                    // — keep its name as-is for completeness.
                }

                // Dedup on name. First occurrence wins (= Fabrication section,
                // since records are stored Fab-first; Installation section is
                // a redundant copy when rates match).
                if (seen.Add(name))
                    result.Add(new LabourRate(name, rate));

                // Skip past this record using its declared size. Falls back
                // to +1 if the size field looks bogus.
                int recordSize = BitConverter.ToInt32(buf, i + 4);
                if (recordSize > 16 && recordSize < 10_000)
                    i += recordSize;
                else
                    i++;
            }
            return result;
        }

        private static bool IsPrintableUtf16(byte[] buf, int offset, int byteCount)
        {
            for (int j = 0; j < byteCount; j += 2)
            {
                byte lo = buf[offset + j];
                byte hi = buf[offset + j + 1];
                if (hi != 0x00) return false;
                if (lo == 0x00) return false;
                if (lo < 0x20)  return false;
                if (lo > 0x7E)  return false;
            }
            return true;
        }
    }
}
