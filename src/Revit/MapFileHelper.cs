using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Shared helpers for reading Autodesk Fabrication "MAP Compressed File 2005"
    /// files: detect the header, locate the zlib stream, decompress, hex-dump,
    /// and search for ASCII needles in the decompressed bytes.
    /// </summary>
    internal static class MapFileHelper
    {
        /// <summary>
        /// Sniffs the header and tries every candidate zlib magic byte pair in
        /// the first 1 KB. Returns the decompressed payload or null on failure.
        /// </summary>
        public static byte[]? TryDecompress(byte[] bytes, out string headerInfo, out string zlibInfo)
        {
            string header = Encoding.ASCII.GetString(bytes, 0, Math.Min(32, bytes.Length));
            headerInfo = header.StartsWith("MAP Compressed File", StringComparison.Ordinal)
                ? "MAP Compressed File detected"
                : Truncate(header, 30);
            zlibInfo = "";

            int scanLimit = Math.Min(1024, bytes.Length - 2);
            byte[] valid2nd = { 0x01, 0x5E, 0x9C, 0xDA };
            for (int i = 0; i < scanLimit; i++)
            {
                if (bytes[i] != 0x78) continue;
                if (Array.IndexOf(valid2nd, bytes[i + 1]) < 0) continue;
                try
                {
                    using var ms     = new MemoryStream(bytes, i, bytes.Length - i);
                    using var zlib   = new ZLibStream(ms, CompressionMode.Decompress);
                    using var outBuf = new MemoryStream();
                    zlib.CopyTo(outBuf);
                    var data = outBuf.ToArray();
                    if (data.Length > 0)
                    {
                        zlibInfo = $"zlib at offset {i} (0x{i:X}), 2nd byte 0x{bytes[i + 1]:X2}";
                        return data;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Hex+ASCII dump of the first <paramref name="len"/> bytes.
        /// </summary>
        public static void DumpHex(TextWriter w, byte[] buf, int len)
        {
            int n = Math.Min(len, buf.Length);
            for (int i = 0; i < n; i += 16)
            {
                w.Write($"{i:X4}: ");
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < n) w.Write($"{buf[i + j]:X2} ");
                    else           w.Write("   ");
                }
                w.Write(" ");
                for (int j = 0; j < 16 && i + j < n; j++)
                {
                    byte b = buf[i + j];
                    w.Write(b >= 32 && b < 127 ? (char)b : '.');
                }
                w.WriteLine();
            }
        }

        /// <summary>
        /// Finds every ASCII occurrence of <paramref name="needle"/> (case-
        /// insensitive) in <paramref name="haystack"/> and dumps ±contextBytes
        /// of hex+ASCII around each. Returns the number of hits.
        /// </summary>
        public static int FindAsciiOccurrences(byte[] haystack, string needle,
                                               TextWriter w, int contextBytes = 48)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            byte[] needleBytes = Encoding.ASCII.GetBytes(needle.ToLowerInvariant());
            int hits = 0;
            for (int i = 0; i <= haystack.Length - needleBytes.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needleBytes.Length; j++)
                {
                    byte hb = haystack[i + j];
                    if (hb >= 'A' && hb <= 'Z') hb = (byte)(hb - 'A' + 'a');
                    if (hb != needleBytes[j]) { ok = false; break; }
                }
                if (!ok) continue;

                hits++;
                w.WriteLine($"── Match #{hits} at offset {i} (0x{i:X}) — ±{contextBytes} bytes ──");
                int start = Math.Max(0, i - contextBytes);
                int end   = Math.Min(haystack.Length, i + needleBytes.Length + contextBytes);
                int len   = end - start;
                byte[] ctx = new byte[len];
                Array.Copy(haystack, start, ctx, 0, len);
                DumpHex(w, ctx, len);
                w.WriteLine();
            }
            return hits;
        }

        public static string Truncate(string s, int n) =>
            s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
