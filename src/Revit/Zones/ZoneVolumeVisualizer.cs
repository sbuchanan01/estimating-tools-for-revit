using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace EstimatingTools.Revit.Zones
{
    /// <summary>
    /// Generates semi-transparent DirectShape prisms for the zones in
    /// the Estimating Zones scheme so the user can visually confirm
    /// the sketch in a 3D view. Show creates the shapes; Hide deletes
    /// them. Shapes are tagged with a stable Comments prefix so we
    /// only touch our own — hand-authored DirectShapes are safe.
    ///
    /// Height rules per zone:
    ///   • Both elevation params set → use those (world Z, feet).
    ///   • Only one set → the other defaults to the Area's Level
    ///     elevation (bottom) or +10 ft above bottom (top).
    ///   • Neither set → bottom = Level elevation, top = next-higher
    ///     Level's elevation, or +10 ft when no higher Level exists.
    ///     This is what horizontal-only zones look like — one prism
    ///     per level, filling the story.
    ///   • An Area with bands listed in Zone Extra Bands gets one
    ///     extra prism per band, all sharing its footprint — e.g. a
    ///     single sketch can show three stacked slabs.
    ///
    /// Colors cycle from a small palette keyed by zone-name hash so
    /// adjacent zones distinguish visually. Same zone name across
    /// runs gets the same colour — helpful when re-showing after a
    /// sketch tweak.
    /// </summary>
    internal static class ZoneVolumeVisualizer
    {
        /// <summary>Stamp on every generated DirectShape's Comments
        /// parameter. Hide filters on this exact prefix so shapes
        /// authored by other tools or by hand are left alone.</summary>
        public const string CommentsTag = "EstimatingTools Zone Volume";

        /// <summary>Six-colour palette — light, distinguishable at
        /// 60% transparency. Chosen for zone-comparison contrast, not
        /// aesthetic subtlety. Kept explicit rather than parsing hex
        /// strings so a future author can eye the colours without a
        /// mental lookup.</summary>
        private static readonly (byte R, byte G, byte B)[] PaletteRgb =
        {
            (0x66, 0xAA, 0xE0),  // Blue
            (0x7C, 0xCE, 0x8E),  // Green
            (0xF2, 0xA6, 0x5C),  // Orange
            (0xB4, 0x8C, 0xE0),  // Purple
            (0x66, 0xC2, 0xB8),  // Teal
            (0xE0, 0x7E, 0x93),  // Rose
        };
        private const int TransparencyPercent = 60;

        /// <summary>Create DirectShape prisms for every zone in the
        /// Estimating Zones scheme. Returns the created ElementIds so
        /// the caller can report a count. Caller wraps in a Transaction.</summary>
        public static IList<ElementId> Show(Document doc, ElementId schemeId)
        {
            var created = new List<ElementId>();
            if (doc == null || schemeId == ElementId.InvalidElementId)
                return created;

            // Pre-compute a Level-elevation lookup so we can resolve
            // defaults without hitting the collector inside every loop.
            var levelsByElev = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            // Materials are shared across zones with the same palette
            // slot — keyed by index so we make at most six.
            var matByIndex = new Dictionary<int, ElementId>();

            var opts = new SpatialElementBoundaryOptions();
            var areas = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Areas)
                .WhereElementIsNotElementType()
                .OfClass(typeof(SpatialElement))
                .OfType<Area>()
                .Where(a => a.AreaScheme != null && a.AreaScheme.Id == schemeId)
                .Where(a => a.Location != null && a.Area > 0)
                .ToList();

            foreach (var area in areas)
            {
                var loop = BuildOuterLoop(area, opts);
                if (loop == null) continue;

                string zoneName = area.get_Parameter(BuiltInParameter.ROOM_NAME)
                    ?.AsString() ?? "(unnamed zone)";

                var bands = ResolveBands(area, levelsByElev);
                bool multiband = bands.Count > 1;
                for (int bandIdx = 0; bandIdx < bands.Count; bandIdx++)
                {
                    var (bottomFt, topFt, bandName) = bands[bandIdx];
                    if (topFt <= bottomFt) continue;   // nothing to extrude

                    string displayName = bandName ?? zoneName;

                    // Translate the loop so it sits at bottomFt. Area
                    // boundary segments live at the Area's Level
                    // elevation by default; the difference tells us
                    // how far to shift.
                    double loopZ = loop.GetPlane()?.Origin.Z ?? 0.0;
                    var shiftedLoop = TranslateLoop(loop, bottomFt - loopZ);
                    if (shiftedLoop == null) continue;

                    int paletteIdx = ColorSlot(displayName);
                    if (!matByIndex.TryGetValue(paletteIdx, out var matId))
                    {
                        matId = EnsureMaterial(doc, paletteIdx);
                        matByIndex[paletteIdx] = matId;
                    }

                    Solid? solid = null;
                    try
                    {
                        var solidOpts = new SolidOptions(matId, ElementId.InvalidElementId);
                        solid = GeometryCreationUtilities.CreateExtrusionGeometry(
                            new[] { shiftedLoop },
                            XYZ.BasisZ,
                            topFt - bottomFt,
                            solidOpts);
                    }
                    catch
                    {
                        // Skip bands whose polygon Revit refuses to
                        // extrude (self-intersecting, tiny slivers)
                        // rather than aborting the whole Show.
                        continue;
                    }
                    if (solid == null || solid.Volume <= 0) continue;

                    var ds = DirectShape.CreateElement(doc,
                        new ElementId(BuiltInCategory.OST_GenericModel));
                    ds.SetShape(new GeometryObject[] { solid });
                    ds.Name = multiband
                        ? $"{displayName} (Zone Volume {bandIdx + 1})"
                        : $"{displayName} (Zone Volume)";
                    var commentsParam = ds.LookupParameter("Comments")
                        ?? ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (commentsParam != null && !commentsParam.IsReadOnly)
                        commentsParam.Set($"{CommentsTag}: {displayName}");
                    created.Add(ds.Id);
                }
            }
            return created;
        }

        /// <summary>Delete every DirectShape whose Comments starts
        /// with our tag prefix. Returns the count removed. Caller
        /// wraps in a Transaction.</summary>
        public static int Hide(Document doc)
        {
            if (doc == null) return 0;
            var toRemove = new List<ElementId>();
            foreach (var el in new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape))
                .WhereElementIsNotElementType())
            {
                var p = el.LookupParameter("Comments")
                    ?? el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                string s = p?.AsString() ?? "";
                if (s.StartsWith(CommentsTag, StringComparison.Ordinal))
                    toRemove.Add(el.Id);
            }
            if (toRemove.Count > 0) doc.Delete(toRemove);
            return toRemove.Count;
        }

        // ── Internals ────────────────────────────────────────────────

        /// <summary>Every band to extrude for this Area, in world Z
        /// (feet), using the resolution rules described on the class
        /// summary. When the Area has no elevation params set at all,
        /// returns a single classic level-to-next-level band. Otherwise
        /// returns one entry per band read via
        /// <see cref="EstimateZoneScheme.ReadZoneBands"/> — the first
        /// (primary Bottom/Top pair) gets the same defaulting rules as
        /// before; any extra bands parsed from
        /// <see cref="EstimateZoneScheme.ZoneExtraBandsParamName"/>
        /// always carry both sides already, so no defaulting is
        /// needed for them.</summary>
        private static List<(double Bottom, double Top, string? Name)> ResolveBands(
            Area area, List<Level> levelsSortedByElev)
        {
            var result = new List<(double Bottom, double Top, string? Name)>();

            // Base "story bottom" is the Area's Level elevation.
            double levelZ = 0;
            var lvl = area.Document.GetElement(area.LevelId) as Level;
            if (lvl != null) levelZ = lvl.Elevation;

            double NextLevelTop() =>
                levelsSortedByElev.FirstOrDefault(l => l.Elevation > levelZ + 1e-6)
                    is Level next ? next.Elevation : levelZ + 10.0;

            var raw = EstimateZoneScheme.ReadNamedZoneBands(area);
            if (raw.Count == 0)
            {
                // Neither set anywhere → classic single band spanning
                // this Level to the next Level up.
                result.Add((levelZ, NextLevelTop(), null));
                return result;
            }

            for (int i = 0; i < raw.Count; i++)
            {
                var (bOpt, tOpt, name) = raw[i];
                double bottom, top;
                if (i == 0)
                {
                    // Primary pair — same defaulting as the original
                    // single-band behavior.
                    bottom = bOpt ?? levelZ;
                    top = tOpt ?? (bOpt.HasValue ? bottom + 10.0 : NextLevelTop());
                }
                else
                {
                    // Extra-band text pairs always carry both sides.
                    bottom = bOpt ?? levelZ;
                    top = tOpt ?? bottom + 10.0;
                }
                result.Add((bottom, top, name));
            }
            return result;
        }

        /// <summary>Pull the largest boundary loop as a CurveLoop.
        /// Skips the arc-densification the ZoneAssignment classifier
        /// does — DirectShape prefers real Arc segments over 8-sample
        /// line-strip approximations, and CurveLoop supports arcs
        /// natively.</summary>
        private static CurveLoop? BuildOuterLoop(Area area,
            SpatialElementBoundaryOptions opts)
        {
            var loops = area.GetBoundarySegments(opts);
            if (loops == null || loops.Count == 0) return null;
            var outer = loops.OrderByDescending(l => l.Count).First();
            var curves = new List<Curve>();
            foreach (var seg in outer)
            {
                var c = seg.GetCurve();
                if (c != null && c.Length > 1e-6) curves.Add(c);
            }
            if (curves.Count < 3) return null;
            try { return CurveLoop.Create(curves); }
            catch { return null; }
        }

        private static CurveLoop? TranslateLoop(CurveLoop loop, double dz)
        {
            if (Math.Abs(dz) < 1e-9) return loop;
            var xform = Transform.CreateTranslation(new XYZ(0, 0, dz));
            var moved = new List<Curve>();
            foreach (Curve c in loop)
                moved.Add(c.CreateTransformed(xform));
            try { return CurveLoop.Create(moved); }
            catch { return null; }
        }

        /// <summary>Stable palette index for a zone name — same name
        /// gets the same colour across runs, adjacent zones with
        /// different names get different colours most of the time.</summary>
        private static int ColorSlot(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return 0;
            int hash = 0;
            foreach (char ch in zoneName)
                hash = (hash * 31 + ch) & 0x7FFFFFFF;
            return hash % PaletteRgb.Length;
        }

        /// <summary>Find or create the semi-transparent material for a
        /// palette slot. Names are stable so a re-Show doesn't spam the
        /// project with duplicates.</summary>
        private static ElementId EnsureMaterial(Document doc, int paletteIdx)
        {
            var (r, g, b) = PaletteRgb[paletteIdx];
            string name = $"EstimatingTools Zone Volume {paletteIdx}";

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => string.Equals(m.Name, name,
                    StringComparison.Ordinal));
            if (existing != null) return existing.Id;

            var id = Material.Create(doc, name);
            var mat = doc.GetElement(id) as Material;
            if (mat != null)
            {
                mat.Color = new Color(r, g, b);
                mat.SurfaceForegroundPatternColor = new Color(r, g, b);
                mat.Transparency = TransparencyPercent;
            }
            return id;
        }
    }
}
