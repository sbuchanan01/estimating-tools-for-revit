using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace EstimatingTools.Revit.Zones
{
    /// <summary>
    /// One-time-per-project bootstrap for the Estimate Zones feature.
    /// Creates + owns three pieces:
    ///
    /// <list type="number">
    ///   <item>The <b>"Estimating Zones"</b> Area Scheme — a dedicated
    ///     scheme so estimating zones don't collide with any Gross
    ///     Building / Rentable schemes users already have.</item>
    ///   <item>One <b>Area Plan</b> view per Level (created on demand;
    ///     idempotent).</item>
    ///   <item>The <b>Estimate Zone</b> instance-scoped project
    ///     parameter, bound to the Fabrication categories the
    ///     estimating tools already cover.</item>
    /// </list>
    ///
    /// Every method here is idempotent — running twice is a no-op. The
    /// setup dialog treats this as a "click Set up, we'll create what's
    /// missing" flow rather than a strict install.
    /// </summary>
    internal static class EstimateZoneScheme
    {
        /// <summary>Name we look up + create for the estimating-only
        /// Area Scheme. Case-sensitive to Revit's search.</summary>
        public const string SchemeName = "Estimating Zones";

        /// <summary>Instance-scoped Text parameter that Sync Zones
        /// writes each part's resolved zone name (or MULTIPLE) into.
        /// Bound to Fabrication Pipework / Ductwork / Hangers so the
        /// same categories the estimating tools cover carry the zone
        /// data.</summary>
        public const string ZoneParamName = "Estimate Zone";

        /// <summary>Optional LENGTH parameter on each Area — bottom of
        /// the vertical band this zone covers (world Z in feet). When
        /// either this OR <see cref="ZoneTopElevationParamName"/> is
        /// set, Sync Zones filters parts by absolute Z instead of by
        /// matching the part's Level to the Area's Level, so the same
        /// XY footprint can be split into stacked bands (e.g. A1 = 0'
        /// to 10', A2 = 10' to 14'). Empty on both = level-based match
        /// (original single-band behavior).</summary>
        public const string ZoneBottomElevationParamName = "Zone Bottom Elevation";

        /// <summary>Optional LENGTH parameter on each Area — top of
        /// the vertical band. See <see cref="ZoneBottomElevationParamName"/>.
        /// Missing top = +∞; missing bottom = -∞.</summary>
        public const string ZoneTopElevationParamName = "Zone Top Elevation";

        /// <summary>Optional TEXT parameter on each Area — additional
        /// bottom-top bands beyond the primary
        /// <see cref="ZoneBottomElevationParamName"/> /
        /// <see cref="ZoneTopElevationParamName"/> pair, so one sketched
        /// footprint can represent several discontiguous vertical bands
        /// without re-sketching it once per band. Format is
        /// semicolon- or comma-separated "bottom-top" pairs in feet,
        /// e.g. "0-7;7-12;12-14" (' and " marks are stripped, so typing
        /// them is harmless but not required). Each pair can optionally
        /// carry its own zone name with a "Name:" prefix, e.g.
        /// "Level 1:0-7;Level 2:7-12". A name-only token ("Low:") names
        /// the primary Bottom/Top band. Unnamed bands on a multi-band
        /// Area get "{Area}-1", "{Area}-2", … (see
        /// <see cref="AutoBandName"/>). Malformed tokens are skipped
        /// rather than failing the whole read.</summary>
        public const string ZoneExtraBandsParamName = "Zone Extra Bands";

        private static readonly Regex ExtraBandTokenPattern = new Regex(
            @"^(?:(?<name>[^:]+):)?\s*(?<bottom>-?\d+(?:\.\d+)?)\s*-\s*(?<top>-?\d+(?:\.\d+)?)\s*$",
            RegexOptions.Compiled);

        private static readonly Regex PrimaryNameTokenPattern = new Regex(
            @"^(?<name>[^:]+):\s*$", RegexOptions.Compiled);

        /// <summary>Characters the Zone Extra Bands format uses as
        /// delimiters (or strips), so they can't appear in a band name.</summary>
        public static readonly char[] ReservedBandNameChars = { ':', ';', ',', '\'', '"' };

        /// <summary>Reads a zone name off <paramref name="part"/> if
        /// the parameter is present. Returns "" when the parameter is
        /// missing (Fabrication categories without the shared param
        /// yet) or empty (part outside every zone).</summary>
        public static string ReadZone(Element part)
        {
            var p = part?.LookupParameter(ZoneParamName);
            if (p == null) return "";
            return p.AsString() ?? p.AsValueString() ?? "";
        }

        /// <summary>Reads the optional bottom/top elevations off an
        /// Area. A missing parameter or an unset value returns null on
        /// that side, which the classifier treats as ±∞.</summary>
        public static (double? BottomFt, double? TopFt) ReadZoneElevations(
            Element area)
        {
            double? Read(string name)
            {
                var p = area?.LookupParameter(name);
                if (p == null || p.StorageType != StorageType.Double) return null;
                if (!p.HasValue) return null;
                // Revit stores lengths in feet internally.
                return p.AsDouble();
            }
            return (Read(ZoneBottomElevationParamName),
                    Read(ZoneTopElevationParamName));
        }

        /// <summary>Returns every vertical band this Area covers: the
        /// primary Bottom/Top pair (if either is set, Name always null
        /// — it uses the Area's own name) followed by any bands parsed
        /// out of <see cref="ZoneExtraBandsParamName"/> (Name non-null
        /// only when the "Name:" prefix was supplied for that band).
        /// Empty list means "no vertical restriction" — the caller
        /// should fall back to level-based matching (or, for the
        /// visualizer, the classic one-band-per-level default).
        /// Bottom/Top on entries after the first are never null — the
        /// text-parsed pairs always carry both sides.</summary>
        public static List<(double? Bottom, double? Top, string? Name)> ReadZoneBands(
            Element area)
        {
            var bands = new List<(double? Bottom, double? Top, string? Name)>();

            var (bottom, top) = ReadZoneElevations(area);
            bool hasPrimary = bottom.HasValue || top.HasValue;
            if (hasPrimary) bands.Add((bottom, top, null));

            var extraParam = area?.LookupParameter(ZoneExtraBandsParamName);
            string text = extraParam?.AsString() ?? "";
            if (string.IsNullOrWhiteSpace(text)) return bands;

            foreach (var rawToken in text.Split(new[] { ';', ',' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                // Strip ' and " marks defensively — users type feet
                // marks out of habit and the parser should still work.
                string token = rawToken.Replace("'", "").Replace("\"", "").Trim();
                var nameOnly = PrimaryNameTokenPattern.Match(token);
                if (nameOnly.Success)
                {
                    string primaryName = nameOnly.Groups["name"].Value.Trim();
                    if (hasPrimary && bands[0].Name == null && primaryName.Length > 0)
                        bands[0] = (bottom, top, primaryName);
                    continue;
                }
                var m = ExtraBandTokenPattern.Match(token);
                if (!m.Success) continue;
                double b = double.Parse(m.Groups["bottom"].Value, CultureInfo.InvariantCulture);
                double t = double.Parse(m.Groups["top"].Value, CultureInfo.InvariantCulture);
                if (t <= b) continue;
                string? name = m.Groups["name"].Success
                    ? m.Groups["name"].Value.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(name)) name = null;
                bands.Add((b, t, name));
            }
            return bands;
        }

        /// <summary>The Area's own name (ROOM_NAME, not Element.Name,
        /// which Revit suffixes with the number).</summary>
        public static string AreaZoneName(Element area)
        {
            string n = area?.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
            return string.IsNullOrWhiteSpace(n) ? "(unnamed zone)" : n.Trim();
        }

        /// <summary>Default name for an unnamed band: the Area's name when
        /// it's the only band, otherwise "{Area}-{n}" (1-based).</summary>
        public static string AutoBandName(string areaName, int index, int count) =>
            count <= 1 ? areaName : $"{areaName}-{index + 1}";

        /// <summary><see cref="ReadZoneBands"/> with every unnamed band
        /// given its <see cref="AutoBandName"/>, so each band on a
        /// multi-band Area reports as its own zone.</summary>
        public static List<(double? Bottom, double? Top, string Name)> ReadNamedZoneBands(
            Element area)
        {
            var raw = ReadZoneBands(area);
            string areaName = AreaZoneName(area);
            return raw
                .Select((b, i) => (b.Bottom, b.Top,
                    b.Name ?? AutoBandName(areaName, i, raw.Count)))
                .ToList();
        }

        /// <summary>True once either primary elevation param holds a
        /// value. Revit can't blank these again (ClearValue needs
        /// HideWhenNoValue on the definition), so such an Area always
        /// keeps at least one band.</summary>
        public static bool HasPrimaryBand(Element area)
        {
            var (b, t) = ReadZoneElevations(area);
            return b.HasValue || t.HasValue;
        }

        /// <summary>Replaces every band on the Area. Band 1 goes into the
        /// Bottom/Top params, the rest into Zone Extra Bands. A null or
        /// empty Name means "use the auto name". Caller wraps in a
        /// Transaction.</summary>
        public static void WriteZoneBands(Element area,
            IReadOnlyList<(double Bottom, double Top, string? Name)> bands)
        {
            var extra = area.LookupParameter(ZoneExtraBandsParamName)
                ?? throw new InvalidOperationException(
                    $"\"{ZoneExtraBandsParamName}\" isn't bound to Areas. " +
                    "Run Estimate Zones → Setup first.");

            if (bands.Count == 0)
            {
                if (HasPrimaryBand(area))
                    throw new InvalidOperationException(
                        "This Area already has elevations set, and Revit can't " +
                        "blank them. Keep at least one band.");
                extra.Set("");
                return;
            }

            var bottomP = area.LookupParameter(ZoneBottomElevationParamName);
            var topP    = area.LookupParameter(ZoneTopElevationParamName);
            if (bottomP == null || topP == null)
                throw new InvalidOperationException(
                    "Zone Bottom / Top Elevation aren't bound to Areas. " +
                    "Run Estimate Zones → Setup first.");
            bottomP.Set(bands[0].Bottom);
            topP.Set(bands[0].Top);

            static string F(double v) =>
                Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);
            static string Prefix(string? name) =>
                string.IsNullOrWhiteSpace(name) ? "" : name!.Trim() + ":";

            var tokens = new List<string>();
            if (!string.IsNullOrWhiteSpace(bands[0].Name))
                tokens.Add(Prefix(bands[0].Name));
            for (int i = 1; i < bands.Count; i++)
                tokens.Add(Prefix(bands[i].Name) + F(bands[i].Bottom) + "-" + F(bands[i].Top));
            extra.Set(string.Join(";", tokens));
        }

        /// <summary>Returns the Estimating Zones AreaScheme id, or
        /// <see cref="ElementId.InvalidElementId"/> when the scheme
        /// hasn't been created yet.</summary>
        public static ElementId FindSchemeId(Document doc)
        {
            if (doc == null) return ElementId.InvalidElementId;
            var scheme = new FilteredElementCollector(doc)
                .OfClass(typeof(AreaScheme))
                .Cast<AreaScheme>()
                .FirstOrDefault(a => string.Equals(a.Name, SchemeName,
                    StringComparison.OrdinalIgnoreCase));
            return scheme?.Id ?? ElementId.InvalidElementId;
        }

        /// <summary>True when both the scheme AND the Estimate Zone
        /// parameter are set up. The setup dialog uses this to render
        /// its status line.</summary>
        public static bool IsFullySetUp(Document doc) =>
            FindSchemeId(doc) != ElementId.InvalidElementId &&
            IsParameterBound(doc);

        /// <summary>Sentinel returned by
        /// <see cref="EnsureSchemeExists"/> when the scheme is
        /// missing — Revit's API doesn't expose AreaScheme creation
        /// programmatically, so callers show instructions instead of
        /// silently succeeding.</summary>
        public const string ManualSchemeCreationInstructions =
            "The Estimating Zones Area Scheme doesn't exist yet. " +
            "Revit's API doesn't allow creating Area Schemes " +
            "programmatically — please create it once manually:\n\n" +
            "  1. Architecture (or Home) tab → Room & Area panel → " +
            "     Area and Volume Computations button (small arrow).\n" +
            "  2. Switch to the Area Schemes tab.\n" +
            "  3. Click New. Rename the new scheme to " +
            "     \"Estimating Zones\" (exact spelling matters).\n" +
            "  4. Click OK, then re-open Estimate Zones → Setup.";

        /// <summary>Returns the scheme id if it already exists,
        /// otherwise <see cref="ElementId.InvalidElementId"/>.
        /// Actual creation is a manual UI step — see
        /// <see cref="ManualSchemeCreationInstructions"/>.</summary>
        public static ElementId EnsureSchemeExists(Document doc)
        {
            return FindSchemeId(doc);
        }

        /// <summary>Returns every Level in the document ordered by
        /// elevation, so callers iterate lowest floor first.</summary>
        public static List<Level> ListLevels(Document doc)
        {
            if (doc == null) return new List<Level>();
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();
        }

        /// <summary>Returns the Area Plan for the given level under our
        /// scheme, or null when none has been created yet.</summary>
        public static ViewPlan? FindAreaPlanFor(Document doc,
            ElementId schemeId, Level level)
        {
            if (doc == null || level == null ||
                schemeId == ElementId.InvalidElementId) return null;
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => v.ViewType == ViewType.AreaPlan
                    && v.GenLevel != null && v.GenLevel.Id == level.Id
                    && v.AreaScheme != null && v.AreaScheme.Id == schemeId
                    && !v.IsTemplate);
        }

        /// <summary>Creates an Area Plan for <paramref name="level"/>
        /// under the Estimating Zones scheme. Idempotent — returns the
        /// existing plan if one is already there. Caller wraps in a
        /// Transaction.</summary>
        public static ViewPlan EnsureAreaPlanFor(Document doc,
            ElementId schemeId, Level level)
        {
            var existing = FindAreaPlanFor(doc, schemeId, level);
            if (existing != null) return existing;
            var plan = ViewPlan.CreateAreaPlan(doc, schemeId, level.Id);
            // Rename so it's obvious what it's for. Revit's default is
            // "Area Plan (<scheme>) - <level>" which is fine but long.
            try { plan.Name = $"Estimate Zones — {level.Name}"; }
            catch { /* name clash — fall back to Revit's default */ }
            return plan;
        }

        /// <summary>True when the Estimate Zone shared / project
        /// parameter is bound to at least the Fab Piping category.
        /// (One category is a proxy for "user ran setup" — if that's
        /// bound, all our other Fab categories are bound too.)</summary>
        public static bool IsParameterBound(Document doc)
        {
            if (doc == null) return false;
            var binding = FindZoneParameterBinding(doc, out _);
            return binding != null;
        }

        /// <summary>True when all three vertical-band params (the
        /// Bottom/Top pair + the Extra Bands text field) are bound to
        /// the Area category. Used by the Setup dialog to render the
        /// vertical-band row's status. Separate from
        /// <see cref="IsParameterBound"/> because these are optional
        /// (the tool works fine with just level-based zones) but the
        /// Setup button binds them alongside the main Estimate Zone
        /// param so users don't have to think about it.</summary>
        public static bool AreElevationParamsBound(Document doc)
        {
            if (doc == null) return false;
            return FindDefinition(doc, ZoneBottomElevationParamName) != null &&
                   FindDefinition(doc, ZoneTopElevationParamName)    != null &&
                   FindDefinition(doc, ZoneExtraBandsParamName)      != null;
        }

        private static Definition? FindDefinition(Document doc, string name)
        {
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is Definition d &&
                    string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        /// <summary>Ensures the Estimate Zone project parameter exists
        /// and is bound to Fab Piping / Ductwork / Hangers as an
        /// INSTANCE parameter. Uses a private shared-parameter file so
        /// the definition survives; Revit needs a real
        /// ExternalDefinition to bind at the project level.
        /// Idempotent. Caller wraps in a Transaction.</summary>
        public static void EnsureParameterBound(Autodesk.Revit.ApplicationServices.Application app, Document doc)
        {
            if (doc == null) return;

            // Load / create the private shared-parameter file the
            // estimating add-in owns. We keep it under %APPDATA% so
            // multiple projects share the same definition + GUID.
            string sharedPath = ResolvePrivateSharedParamsFile(app);
            var oldPath = app.SharedParametersFilename;
            app.SharedParametersFilename = sharedPath;
            try
            {
                var file = app.OpenSharedParameterFile();
                if (file == null)
                    throw new InvalidOperationException(
                        "Failed to open the private shared-parameter file.");

                var group = file.Groups.get_Item("EstimatingTools Zones")
                    ?? file.Groups.Create("EstimatingTools Zones");

                // ── Main Estimate Zone (Text, on Fab categories) ────────
                if (!IsParameterBound(doc))
                {
                    var def = GetOrCreateDef(group, ZoneParamName,
                        SpecTypeId.String.Text);
                    var cats = app.Create.NewCategorySet();
                    foreach (var bic in _fabCategories)
                    {
                        var cat = Category.GetCategory(doc, bic);
                        if (cat != null && cat.AllowsBoundParameters) cats.Insert(cat);
                    }
                    var binding = app.Create.NewInstanceBinding(cats);
                    doc.ParameterBindings.Insert(def, binding,
                        GroupTypeId.IdentityData);
                }

                // ── Optional vertical-band params (on Areas) ────────────
                // Bound together in one call so a user who runs Set-up
                // once ends up with all three. Users who prefer
                // horizontal-only zones simply leave them blank.
                if (!AreElevationParamsBound(doc))
                {
                    var areaCats = app.Create.NewCategorySet();
                    var areaCat = Category.GetCategory(doc, BuiltInCategory.OST_Areas);
                    if (areaCat != null && areaCat.AllowsBoundParameters)
                        areaCats.Insert(areaCat);
                    if (areaCats.Size > 0)
                    {
                        foreach (var (name, spec) in new[] {
                            (ZoneBottomElevationParamName, SpecTypeId.Length),
                            (ZoneTopElevationParamName,    SpecTypeId.Length),
                            (ZoneExtraBandsParamName,       SpecTypeId.String.Text) })
                        {
                            var def = GetOrCreateDef(group, name, spec);
                            // Skip if already bound (partial-prior-run case).
                            if (FindDefinition(doc, name) != null) continue;
                            var binding = app.Create.NewInstanceBinding(areaCats);
                            doc.ParameterBindings.Insert(def, binding,
                                GroupTypeId.Geometry);
                        }
                    }
                }
            }
            finally
            {
                app.SharedParametersFilename = oldPath ?? "";
            }
        }

        /// <summary>Find or create an ExternalDefinition in the shared
        /// param file's group. Split out so all three params
        /// (Estimate Zone + two elevations) go through one code path.</summary>
        private static ExternalDefinition GetOrCreateDef(DefinitionGroup group,
            string name, ForgeTypeId specType)
        {
            var def = group.Definitions.get_Item(name) as ExternalDefinition;
            if (def != null) return def;
            var opt = new ExternalDefinitionCreationOptions(name, specType)
            {
                UserModifiable = true,
                Visible = true,
            };
            def = group.Definitions.Create(opt) as ExternalDefinition
                  ?? throw new InvalidOperationException(
                      $"Could not create the {name} shared parameter.");
            return def;
        }

        /// <summary>Returns the Definition + Binding for the Estimate
        /// Zone parameter, or (null,null) when not bound. Kept
        /// separate so the presence check + the write path can share
        /// the lookup.</summary>
        public static Definition? FindZoneParameterBinding(Document doc,
            out Binding? binding)
        {
            binding = null;
            if (doc == null) return null;
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is Definition d &&
                    string.Equals(d.Name, ZoneParamName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    binding = it.Current as Binding;
                    return d;
                }
            }
            return null;
        }

        // ── Internals ────────────────────────────────────────────────

        // Fab categories we bind the Estimate Zone parameter to. Every
        // category the material + labor tools already pull from should
        // be here so the same parts get zone-tagged.
        private static readonly BuiltInCategory[] _fabCategories = new[]
        {
            BuiltInCategory.OST_FabricationPipework,
            BuiltInCategory.OST_FabricationDuctwork,
            BuiltInCategory.OST_FabricationHangers,
        };

        /// <summary>Path to the shared-parameter file this add-in
        /// owns. Under %APPDATA% so it persists across Revit versions
        /// and doesn't collide with the user's own shared-param file.
        /// Created empty on first call.</summary>
        private static string ResolvePrivateSharedParamsFile(Autodesk.Revit.ApplicationServices.Application app)
        {
            string root = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
            string dir = System.IO.Path.Combine(root, "EstimatingTools");
            System.IO.Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir,
                "EstimatingTools-SharedParameters.txt");
            if (!System.IO.File.Exists(file))
            {
                // Revit will populate this file on first
                // OpenSharedParameterFile() call — but ONLY if it
                // exists and is empty. Create as UTF-16 (Revit's
                // format) so it accepts the file cleanly.
                System.IO.File.WriteAllText(file, "",
                    new System.Text.UnicodeEncoding(false, true));
            }
            return file;
        }
    }
}
