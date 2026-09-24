using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Parses Autodesk Fabrication's <c>Material.MAP</c> into a
    /// {(MaterialName, WireGauge) → <see cref="MaterialRow"/>} index.
    /// The file holds the per-gauge sheet-metal data shown in Fab's
    /// Material Database UI: cost per pound, weight per square foot,
    /// thickness, default sheet size, etc.
    ///
    /// Used to plug the missing material side of duct cost — Fab's
    /// duct items have <c>M-Rate = None</c> in supplier.map; instead
    /// the per-foot $ is computed as
    /// <code>
    ///   SheetMetalArea (sq ft/ft) × WeightPerSqft × CostPerLb
    /// </code>
    /// where WeightPerSqft and CostPerLb come from this map, keyed by
    /// the part's <c>Material</c> name and <c>MaterialGauge</c>.
    ///
    /// Binary format (reverse-engineered against
    /// <c>Fabrication Imperial 4.02\Database\Material.MAP</c>):
    /// MAP-zlib envelope (header <c>"MAP Compressed File 2005"</c>,
    /// zlib stream at offset 30). Decompressed payload is a series of
    /// material sections, each starting with three consecutive
    /// length-prefixed UTF-16 strings:
    /// <list type="bullet">
    /// <item><description>Material name (e.g. <c>"Galvanized"</c>)</description></item>
    /// <item><description>Abbreviation (e.g. <c>"GALV"</c>)</description></item>
    /// <item><description>Machine (e.g. <c>"None"</c>)</description></item>
    /// </list>
    /// followed by ~316-byte gauge rows with these field offsets relative
    /// to the row's first byte (= start of thickness double):
    /// <list type="bullet">
    /// <item><description>+0:  thickness, double (e.g. 0.0276)</description></item>
    /// <item><description>+8:  wire gauge, int32 (e.g. 24)</description></item>
    /// <item><description>+36: cost per lb, double (e.g. 0.53)</description></item>
    /// <item><description>+86: weight per sqft, double (e.g. 1.156)</description></item>
    /// </list>
    /// Verified: Galvanized 24ga → thickness 0.0276, $0.53/lb, 1.156 lb/sqft.
    /// Galvanized 22ga → thickness 0.0336, $0.53/lb, 1.406 lb/sqft.
    /// </summary>
    public sealed class MaterialMapReader
    {
        public sealed record MaterialRow(
            string MaterialName,
            int    WireGauge,
            double Thickness,
            double CostPerLb,
            double WeightPerSqft);

        // Per-row field offsets within a gauge record (relative to the
        // thickness double's first byte).
        private const int FieldOff_Gauge          = 8;
        private const int FieldOff_CostPerLb      = 36;
        private const int FieldOff_WeightPerSqft  = 86;
        private const int RecordLengthGuess       = 316;

        // Plausibility bounds for the per-row doubles. Used to filter
        // out runs of binary that aren't gauge records.
        private const double MinThickness   = 0.005;
        private const double MaxThickness   = 0.5;
        private const int    MinWireGauge   = 10;
        private const int    MaxWireGauge   = 32;
        private const double MinCostPerLb   = 0.01;
        private const double MaxCostPerLb   = 100.0;
        private const double MinWeightPerSqft = 0.05;
        private const double MaxWeightPerSqft = 20.0;

        private readonly List<MaterialRow> _rows;
        private readonly Dictionary<(string, int), MaterialRow> _byKey;

        public IReadOnlyList<MaterialRow> AllRows => _rows;
        public int RowCount => _rows.Count;

        public MaterialMapReader(string mapPath)
        {
            if (!File.Exists(mapPath))
                throw new FileNotFoundException(
                    $"{Path.GetFileName(mapPath)} not found", mapPath);
            var raw = File.ReadAllBytes(mapPath);
            var buf = MapFileHelper.TryDecompress(raw, out _, out _)
                ?? throw new InvalidOperationException(
                    $"Could not decompress '{mapPath}'.");
            _rows  = ScanAll(buf);
            _byKey = new Dictionary<(string, int), MaterialRow>();
            foreach (var r in _rows)
            {
                var key = (r.MaterialName, r.WireGauge);
                // First occurrence wins — Fab can list the same gauge
                // multiple times for different sheet-application sub-tabs
                // (Duct / Pipework / Electrical Containment / Other).
                // Without a tab discriminator in the binary yet, the
                // first row is typically the Duct row.
                if (!_byKey.ContainsKey(key)) _byKey[key] = r;
            }
        }

        /// <summary>
        /// Returns the row for a given material name + wire gauge, or null
        /// when no match. Material name matching is case-insensitive.
        /// </summary>
        public MaterialRow? Lookup(string materialName, int wireGauge)
        {
            if (string.IsNullOrWhiteSpace(materialName)) return null;
            return _byKey.TryGetValue((materialName.Trim(), wireGauge), out var r)
                ? r : null;
        }

        // ── Parsing ─────────────────────────────────────────────────────────

        private static List<MaterialRow> ScanAll(byte[] buf)
        {
            var rows = new List<MaterialRow>();

            // Anchor on gauge integers (4-byte int32 in range 10..32) and
            // verify the preceding 8 bytes form a plausible thickness
            // double + the following bytes contain plausible cost +
            // weight doubles at known offsets. Then walk backward to the
            // nearest material name (length-prefixed UTF-16) and bind.
            //
            // Anchoring on gauge (instead of material name) is more
            // robust against odd headers / padding around the material
            // strings — a valid (thickness, gauge, cost, weight)
            // quadruple at the right relative offsets is a strong
            // signal of a real record.

            string currentMaterial = "";
            int    lastMaterialOff = -1;

            for (int i = 0; i + RecordLengthGuess < buf.Length; i++)
            {
                // Quick gauge check at offset i + 8 (treating i as
                // potential thickness start).
                int gaugePos = i + FieldOff_Gauge;
                if (gaugePos + 4 > buf.Length) continue;
                int gauge = BitConverter.ToInt32(buf, gaugePos);
                if (gauge < MinWireGauge || gauge > MaxWireGauge) continue;

                // Thickness double at offset i.
                double thickness = BitConverter.ToDouble(buf, i);
                if (double.IsNaN(thickness) || double.IsInfinity(thickness)) continue;
                if (thickness < MinThickness || thickness > MaxThickness) continue;

                // Cost double at offset i + 36.
                int costPos = i + FieldOff_CostPerLb;
                if (costPos + 8 > buf.Length) continue;
                double cost = BitConverter.ToDouble(buf, costPos);
                if (double.IsNaN(cost) || double.IsInfinity(cost)) continue;
                if (cost < MinCostPerLb || cost > MaxCostPerLb) continue;

                // Weight double at offset i + 86.
                int weightPos = i + FieldOff_WeightPerSqft;
                if (weightPos + 8 > buf.Length) continue;
                double weight = BitConverter.ToDouble(buf, weightPos);
                if (double.IsNaN(weight) || double.IsInfinity(weight)) continue;
                if (weight < MinWeightPerSqft || weight > MaxWeightPerSqft) continue;

                // Find the nearest preceding material name (length-prefixed
                // UTF-16 with all-letters/spaces content), cached as
                // currentMaterial. Refresh when we've moved past the
                // cached one's region.
                if (i > lastMaterialOff + 50_000 || string.IsNullOrEmpty(currentMaterial))
                {
                    if (TryFindMaterialNameBefore(buf, i, out string? name, out int off))
                    {
                        currentMaterial = name!;
                        lastMaterialOff = off;
                    }
                }
                if (string.IsNullOrEmpty(currentMaterial)) continue;

                rows.Add(new MaterialRow(currentMaterial, gauge,
                    thickness, cost, weight));

                // Skip past this record to avoid re-scanning its inner
                // bytes (the int32 at +8 could match a different anchor
                // if we don't advance).
                i += RecordLengthGuess - 1;
            }
            return rows;
        }

        /// <summary>
        /// Walks BACKWARD from <paramref name="beforeOffset"/> looking
        /// for a length-prefixed UTF-16 string that looks like a material
        /// name — at least 3 chars, letters / spaces / hyphen only, no
        /// digits or punctuation (filters out things like "GALV" or
        /// machine names like "None"). The closest one preceding the
        /// gauge record is the material this record belongs to.
        /// </summary>
        private static bool TryFindMaterialNameBefore(byte[] buf, int beforeOffset,
            out string? name, out int foundOffset)
        {
            name = null;
            foundOffset = -1;
            // Material names sit at most a few hundred bytes before the
            // first gauge record. 1 KB window is plenty.
            int searchStart = Math.Max(0, beforeOffset - 1024);

            string? bestName = null;
            int bestOff = -1;
            for (int i = searchStart; i + 6 < beforeOffset; i++)
            {
                if (!TryReadLengthPrefixedString(buf, i, out string? s, out int afterEnd))
                    continue;
                if (s == null) continue;
                if (!LooksLikeMaterialName(s)) continue;
                bestName = s;
                bestOff  = i;
                i = afterEnd - 1;
            }
            if (bestName == null) return false;
            name = bestName;
            foundOffset = bestOff;
            return true;
        }

        private static bool LooksLikeMaterialName(string s)
        {
            if (s.Length < 3 || s.Length > 50) return false;
            foreach (char c in s)
            {
                if (char.IsLetter(c)) continue;
                if (c == ' ' || c == '-' || c == '/') continue;
                return false;
            }
            // Exclude short abbreviations like "GALV", "None", "Any" —
            // these aren't material display names. Material display
            // names always contain a lowercase letter (capitalized
            // word, not all caps). "GALV" has no lowercase. "None"
            // does, but is too generic — explicitly excluded.
            bool hasLower = false;
            foreach (char c in s) if (char.IsLower(c)) { hasLower = true; break; }
            if (!hasLower) return false;
            if (string.Equals(s, "None", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(s, "Any",  StringComparison.OrdinalIgnoreCase)) return false;
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
            if (chars < 2 || chars > 100) return false;

            int strBytes = chars * 2;
            int end = i + 4 + strBytes;
            if (end + 2 > buf.Length) return false;

            for (int k = 0; k < strBytes; k += 2)
            {
                byte lo = buf[i + 4 + k];
                byte hi = buf[i + 4 + k + 1];
                if (hi != 0x00) return false;
                if (lo < 0x20 || lo > 0x7E) return false;
            }
            if (buf[end] != 0x00 || buf[end + 1] != 0x00) return false;

            s = Encoding.Unicode.GetString(buf, i + 4, strBytes);
            afterEnd = end + 2;
            return true;
        }
    }
}
