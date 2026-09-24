using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Builds a {ProductCode → <see cref="ItmFileParser.ItmEntry"/>} index by
    /// walking every <c>*.ITM</c> file under a Fabrication "Items" root and
    /// parsing each with <see cref="ItmFileParser"/>. Provides two lookup
    /// pathways:
    /// <list type="bullet">
    /// <item><description><see cref="GetByItemPath"/> — exact lookup by the
    /// part's <c>FabricationPartType.ItemPath</c>. ALWAYS PREFER THIS
    /// when available. Parsing is lazy + cached.</description></item>
    /// <item><description><see cref="GetByProductCode"/> — fallback global
    /// lookup. ProductCodes are NOT globally unique in Fab content (the
    /// same ADSK_xxxxxxx can appear in unrelated ITMs), so first-wins
    /// here can claim the wrong ITM. Only safe when ItemPath isn't
    /// available.</description></item>
    /// </list>
    ///
    /// Construction is eager — all ITMs are parsed up front to populate
    /// the global product-code map. On Imperial 4.02 (~5,200 ITMs) this
    /// takes ~6–10 s. Failures on individual files are swallowed.
    /// </summary>
    public sealed class ItmFileIndex
    {
        // First-wins per code (each value is the FIRST ITM that defined it).
        private readonly Dictionary<string, ItmFileParser.ItmEntry> _byCode;

        // Lazy parse cache keyed by the .ITM file path (case-insensitive).
        // ItemPath lookups populate this; subsequent lookups are O(1).
        private readonly Dictionary<string, ItmFileParser> _parsersByPath;

        // Map of .ITM filename (without extension, case-insensitive)
        // to its full path. Used by GetByFileName for family-name lookups.
        // Collisions are rare but possible (the same filename in two
        // sub-trees) — first-wins.
        private readonly Dictionary<string, string> _pathsByFileName;

        /// <summary>Items-root folder used to populate this index.</summary>
        public string ItemsRoot { get; }

        /// <summary>Total number of .ITM files visited (incl. failures).</summary>
        public int FilesScanned { get; }

        /// <summary>Files that failed to parse and were skipped.</summary>
        public int FilesFailed { get; }

        /// <summary>Subfolders skipped via the skip-list.</summary>
        public IReadOnlyList<string> SkippedFolders { get; }

        /// <summary>Total number of part-code → entry bindings indexed.</summary>
        public int EntryCount => _byCode.Count;

        /// <summary>Time taken to build the index.</summary>
        public TimeSpan BuildDuration { get; }

        /// <summary>
        /// Default subfolders skipped under the Items root — domains we
        /// don't price (electrical/structural) and template scaffolding.
        /// Names are matched case-insensitively against any path segment.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultSkipFolders =
            new[] { "Electrical", "Structural", "zTemplates" };

        public ItmFileIndex(string itemsRoot)
            : this(itemsRoot, DefaultSkipFolders) { }

        public ItmFileIndex(string itemsRoot,
            IReadOnlyList<string> skipFolderNames)
            : this(itemsRoot, skipFolderNames,
                   loadedItmPaths: null) { }

        /// <summary>
        /// Builds the index from a SPECIFIC list of .ITM file paths
        /// instead of walking the entire Items tree. This is the fast
        /// path used in Revit, where
        /// <see cref="Autodesk.Revit.DB.Fabrication.FabricationConfiguration.GetAllLoadedItemFiles"/>
        /// gives us only the few dozen ITMs actually referenced by the
        /// project's loaded services — typically &lt;200 files instead
        /// of the 15,000+ that live under the Items root.
        ///
        /// When <paramref name="loadedItmPaths"/> is null, falls back to
        /// the full directory walk (skip-folder rules apply). When it's
        /// non-null but empty, builds an empty index without scanning.
        /// </summary>
        public ItmFileIndex(string itemsRoot,
            IReadOnlyList<string> skipFolderNames,
            IReadOnlyList<string>? loadedItmPaths)
        {
            if (string.IsNullOrWhiteSpace(itemsRoot))
                throw new ArgumentException("itemsRoot is empty", nameof(itemsRoot));
            if (!Directory.Exists(itemsRoot))
                throw new DirectoryNotFoundException(
                    $"Items root not found: {itemsRoot}");

            ItemsRoot        = itemsRoot;
            SkippedFolders   = skipFolderNames ?? Array.Empty<string>();
            _byCode          = new Dictionary<string, ItmFileParser.ItmEntry>(
                                   StringComparer.OrdinalIgnoreCase);
            _parsersByPath   = new Dictionary<string, ItmFileParser>(
                                   StringComparer.OrdinalIgnoreCase);
            _pathsByFileName = new Dictionary<string, string>(
                                   StringComparer.OrdinalIgnoreCase);

            // Pick the path enumerator: explicit-list (fast) vs.
            // directory walk (slow fallback).
            IEnumerable<string> paths;
            if (loadedItmPaths != null)
            {
                paths = loadedItmPaths;
            }
            else
            {
                var skipSet = new HashSet<string>(SkippedFolders,
                    StringComparer.OrdinalIgnoreCase);
                paths = Directory.EnumerateFiles(
                            itemsRoot, "*.ITM", SearchOption.AllDirectories)
                        .Where(p => !IsUnderSkippedFolder(p, itemsRoot, skipSet));
            }

            var sw = Stopwatch.StartNew();
            int scanned = 0, failed = 0;
            foreach (var rawPath in paths)
            {
                if (string.IsNullOrWhiteSpace(rawPath)) continue;
                // Normalize relative paths (Fab's GetAllLoadedItemFiles
                // and FabricationPartType.ItemPath both return paths
                // relative to ItemsRoot — e.g. "Imperial Content/.../X.ITM").
                string path = NormalizeAgainstRoot(rawPath, itemsRoot);
                if (!File.Exists(path)) { failed++; continue; }
                scanned++;

                // Reuse a previously-parsed ITM from the session cache
                // when available — that's the second-open speedup.
                ItmFileParser? parser = SessionCache.GetCached(path);
                if (parser == null)
                {
                    try { parser = new ItmFileParser(path); }
                    catch { failed++; continue; }
                    SessionCache.Add(path, parser);
                }
                _parsersByPath[path] = parser;

                // Index by filename (no extension) for family-name lookup.
                string nameKey = Path.GetFileNameWithoutExtension(path);
                if (!_pathsByFileName.ContainsKey(nameKey))
                    _pathsByFileName[nameKey] = path;

                foreach (var entry in parser.EntriesInOrder)
                {
                    // First occurrence wins — NOT globally unique. Only
                    // used as a fallback when ItemPath isn't available.
                    if (!_byCode.ContainsKey(entry.ProductCode))
                        _byCode[entry.ProductCode] = entry;
                }
            }
            sw.Stop();

            FilesScanned  = scanned;
            FilesFailed   = failed;
            BuildDuration = sw.Elapsed;
        }

        // ── Session-level parsed-ITM cache ──────────────────────────────────

        /// <summary>
        /// Holds parsed <see cref="ItmFileParser"/> instances across
        /// <see cref="ItmFileIndex"/> constructions within the same
        /// Revit session. Repeat dialog opens reuse parsed files — only
        /// brand-new paths are parsed afresh.
        /// </summary>
        private static class SessionCache
        {
            private static readonly Dictionary<string, ItmFileParser> _cache =
                new(StringComparer.OrdinalIgnoreCase);
            private static readonly object _lock = new();

            public static ItmFileParser? GetCached(string path)
            {
                lock (_lock)
                {
                    return _cache.TryGetValue(path, out var p) ? p : null;
                }
            }
            public static void Add(string path, ItmFileParser parser)
            {
                lock (_lock)
                {
                    _cache[path] = parser;
                }
            }
        }

        /// <summary>
        /// Last diagnostic message from the most recent
        /// <see cref="GetLoadedItmPaths"/> call. Empty on success,
        /// describes which reflection step failed on failure. Surfaced in
        /// the Cost Breakdown header so we can see why the fast path
        /// didn't engage.
        /// </summary>
        public static string LastLoadedQueryNote { get; private set; } = "";

        /// <summary>
        /// Queries the document's <c>FabricationConfiguration</c> for the
        /// list of .ITM files currently loaded into the project's
        /// services. The compile-time symbol
        /// <c>Autodesk.Revit.DB.Fabrication.FabricationConfiguration</c>
        /// IS visible — we use a direct call there. Inside the loop the
        /// <c>Identifier</c> property is read via reflection (same
        /// as <c>FabricationPartType.ItemPath</c>: declared in the API
        /// XML, not always exposed on the compile-time surface in 2026).
        /// </summary>
        public static IReadOnlyList<string> GetLoadedItmPaths(object document)
        {
            LastLoadedQueryNote = "";
            var result = new List<string>();
            if (document == null) { LastLoadedQueryNote = "doc=null"; return result; }

            try
            {
                // Direct call (compile-time symbol).
                var doc = (Autodesk.Revit.DB.Document)document;
                var cfg = Autodesk.Revit.DB.FabricationConfiguration
                    .GetFabricationConfiguration(doc);
                if (cfg == null)
                {
                    LastLoadedQueryNote = "no FabricationConfiguration for this doc";
                    return result;
                }

                // GetAllLoadedItemFiles via reflection — same reason as
                // FabricationPartType.ItemPath: API XML claims it exists
                // since 2019 but it's not always on the public surface.
                var getLoaded = cfg.GetType().GetMethod("GetAllLoadedItemFiles",
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance);
                if (getLoaded == null)
                {
                    // List every method that contains "Loaded" or "Item"
                    // so we can pick the actual API name from the report.
                    var names = cfg.GetType().GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance)
                        .Select(m => m.Name)
                        .Where(n => n.Contains("Loaded") || n.Contains("Item"))
                        .Distinct().OrderBy(n => n).Take(20);
                    LastLoadedQueryNote =
                        $"GetAllLoadedItemFiles not found on {cfg.GetType().FullName}. " +
                        $"Candidate method names: {string.Join(", ", names)}";
                    return result;
                }

                var loaded = getLoaded.Invoke(cfg, null) as System.Collections.IEnumerable;
                if (loaded == null)
                {
                    LastLoadedQueryNote = "GetAllLoadedItemFiles returned null";
                    return result;
                }

                int seen = 0;
                int withId = 0;
                foreach (var f in loaded)
                {
                    seen++;
                    if (f == null) continue;
                    var idProp = f.GetType().GetProperty("Identifier",
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance);
                    if (idProp == null) continue;
                    var id = idProp.GetValue(f)?.ToString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result.Add(id!.Trim());
                        withId++;
                    }
                }
                if (seen == 0)
                    LastLoadedQueryNote = "GetAllLoadedItemFiles returned 0 entries";
                else if (withId == 0)
                    LastLoadedQueryNote =
                        $"got {seen} item files but no .Identifier property " +
                        $"(item-file type: {(loaded.Cast<object>().FirstOrDefault()?.GetType().FullName ?? "?")})";
            }
            catch (Exception ex)
            {
                LastLoadedQueryNote = $"exception: {ex.GetType().Name}: {ex.Message}";
            }
            return result;
        }

        /// <summary>
        /// Authoritative lookup by the part's <c>FabricationPartType.ItemPath</c>.
        /// Accepts either an absolute path OR a path relative to
        /// <see cref="ItemsRoot"/> (Fab's <c>ItemPath</c> property returns
        /// relative — e.g. <c>"Imperial Content/…/X.ITM"</c>).
        /// Returns the first part entry from that specific .ITM (every
        /// size shares the same install/fab table refs so any size's
        /// entry is equivalent). Returns null when the path is empty,
        /// the file doesn't exist, the parse fails, or the .ITM has no
        /// part-level brackets.
        /// </summary>
        public ItmFileParser.ItmEntry? GetByItemPath(string? itemPath)
        {
            if (string.IsNullOrWhiteSpace(itemPath)) return null;
            string normalized = NormalizeAgainstRoot(itemPath.Trim(), ItemsRoot);
            if (!File.Exists(normalized)) return null;

            if (!_parsersByPath.TryGetValue(normalized, out var parser))
            {
                parser = SessionCache.GetCached(normalized);
                if (parser == null)
                {
                    try { parser = new ItmFileParser(normalized); }
                    catch { return null; }
                    SessionCache.Add(normalized, parser);
                }
                _parsersByPath[normalized] = parser;
            }
            return parser.EntriesInOrder.Count > 0
                ? parser.EntriesInOrder[0]
                : null;
        }

        /// <summary>
        /// Resolves <paramref name="path"/> against <paramref name="root"/>:
        /// returns <paramref name="path"/> unchanged when absolute, else
        /// joins it onto <paramref name="root"/>. Also normalises slashes
        /// to the platform separator. Used at every entry point so the
        /// rest of the index can treat all paths uniformly.
        /// </summary>
        private static string NormalizeAgainstRoot(string path, string root)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string normalized = path.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized)) return normalized;
            return Path.Combine(root, normalized);
        }

        /// <summary>
        /// Returns the indexed entry for a product code, or null when not
        /// found. Case-insensitive. Whitespace-trimmed.
        ///
        /// CAUTION: ProductCodes are not globally unique. Two unrelated
        /// ITMs can share the same ADSK_xxxxxxx code (e.g.
        /// ADSK_30111286 appears in both a Milwaukee gate valve AND a
        /// strainer). First-wins here can yield the wrong install table.
        /// Prefer <see cref="GetByItemPath"/> or <see cref="GetByFileName"/>.
        /// </summary>
        public ItmFileParser.ItmEntry? GetByProductCode(string? productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode)) return null;
            return _byCode.TryGetValue(productCode.Trim(), out var e) ? e : null;
        }

        /// <summary>
        /// Returns the entry for an .ITM filename (without extension).
        /// Designed for matching against Revit's family / type name,
        /// which by convention equals the source .ITM's filename for
        /// stock Fabrication content (e.g. family name
        /// <c>"1550CB2 (FLG)"</c> → <c>1550CB2 (FLG).ITM</c>).
        /// Case-insensitive.
        /// </summary>
        public ItmFileParser.ItmEntry? GetByFileName(string? fileNameNoExt)
        {
            if (string.IsNullOrWhiteSpace(fileNameNoExt)) return null;
            if (!_pathsByFileName.TryGetValue(fileNameNoExt.Trim(), out var path))
                return null;
            return GetByItemPath(path);
        }

        /// <summary>
        /// True when <paramref name="path"/> contains any segment that
        /// matches a name in <paramref name="skipSet"/>. Compared
        /// case-insensitively, anchored on full path segments (so
        /// "Electrical" matches the folder, not a file called
        /// "ElectricalSomething.ITM").
        /// </summary>
        private static bool IsUnderSkippedFolder(string path, string itemsRoot,
            HashSet<string> skipSet)
        {
            if (skipSet.Count == 0) return false;
            string relative;
            try { relative = Path.GetRelativePath(itemsRoot, path); }
            catch { return false; }
            foreach (var segment in relative.Split(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (skipSet.Contains(segment)) return true;
            }
            return false;
        }

        // ── Items-folder location ───────────────────────────────────────────

        /// <summary>
        /// Best-effort: locate the Fabrication "Items" folder relative to
        /// a known Database folder. Imperial 4.02 layout is
        /// <c>…\&lt;config&gt;\Database\</c> + <c>…\&lt;config&gt;\Items\Imperial Content\</c>,
        /// so Items lives as a sibling of Database. Walks up two levels
        /// to be tolerant of nested overrides. Returns null when no
        /// Items folder is found.
        /// </summary>
        public static string? TryLocateItemsRoot(string databaseFolder)
        {
            if (string.IsNullOrWhiteSpace(databaseFolder)) return null;
            if (!Directory.Exists(databaseFolder)) return null;

            // Try ../Items, then ../../Items.
            string? cursor = Path.GetFullPath(databaseFolder);
            for (int level = 0; level < 3 && !string.IsNullOrEmpty(cursor); level++)
            {
                string? parent = Path.GetDirectoryName(cursor);
                if (string.IsNullOrEmpty(parent)) break;
                string candidate = Path.Combine(parent, "Items");
                if (Directory.Exists(candidate))
                    return candidate;
                cursor = parent;
            }
            return null;
        }
    }
}
