using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace EstimatingTools.Revit.Zones
{
    /// <summary>
    /// Point-in-zone + curve-crosses-zone geometry math for Sync
    /// Zones. Reads Revit <see cref="Area"/> elements out of the
    /// Estimating Zones scheme, precomputes their boundary polygons,
    /// captures each Area's optional Z-range, and answers "which
    /// zone(s) does this part fall in?"
    ///
    /// Two matching modes per zone (per-Area, not global):
    ///   • <b>Level-based</b> — used when the Area has no elevation
    ///     range set. The part's LevelId must equal the Area's LevelId;
    ///     Z is not tested. Backward-compatible with the original
    ///     horizontal-only workflow.
    ///   • <b>Absolute-Z-based</b> — used when the Area has either
    ///     <see cref="EstimateZoneScheme.ZoneBottomElevationParamName"/>
    ///     or <see cref="EstimateZoneScheme.ZoneTopElevationParamName"/>
    ///     set, or has bands listed in
    ///     <see cref="EstimateZoneScheme.ZoneExtraBandsParamName"/>.
    ///     Level filter is ignored (a Z-band can legitimately cross
    ///     levels or share a footprint with another band on the same
    ///     level); the part's Z must fall inside one of the Area's
    ///     bands. A single Area can carry several bands at once — each
    ///     resolves to its own entry here sharing the Area's footprint.
    ///
    /// Write-model: each part gets exactly one zone — whichever holds
    /// the majority of its centerline samples (see CenterlineSamples).
    /// "" (UNASSIGNED) when most of the part sits outside every zone.
    /// </summary>
    internal sealed class ZoneAssignment
    {
        /// <summary>Written by earlier versions of Sync Zones for parts
        /// crossing zones. Classify no longer produces it; kept so stale
        /// tags still surface in Diagnose and the rollup until re-sync.</summary>
        public const string MultipleTag = "MULTIPLE";

        private readonly List<ZoneVolume> _zones;

        private ZoneAssignment(List<ZoneVolume> zones)
        {
            _zones = zones;
        }

        /// <summary>Snapshots every zone in the Estimating Zones
        /// scheme. Cheap — a few polygon builds per zone. Callers
        /// reuse one instance for the whole Sync Zones pass.</summary>
        public static ZoneAssignment Load(Document doc, ElementId schemeId)
        {
            var zones = new List<ZoneVolume>();
            if (doc == null || schemeId == ElementId.InvalidElementId)
                return new ZoneAssignment(zones);

            var opts = new SpatialElementBoundaryOptions();
            foreach (var area in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Areas)
                .WhereElementIsNotElementType()
                .OfClass(typeof(SpatialElement))
                .OfType<Area>())
            {
                if (area.AreaScheme == null || area.AreaScheme.Id != schemeId)
                    continue;
                if (area.Location == null) continue;
                if (area.Area <= 0) continue;

                var polygon = BuildPolygon(area, opts);
                if (polygon == null || polygon.Count < 3) continue;

                string zoneName = EstimateZoneScheme.AreaZoneName(area);

                // One Area can carry several Z-bands (the primary
                // Bottom/Top pair plus any in Zone Extra Bands) — each
                // becomes its own ZoneVolume sharing the footprint.
                // Unnamed bands come back auto-named "{Area}-1", "{Area}-2".
                var bands = EstimateZoneScheme.ReadNamedZoneBands(area);
                if (bands.Count == 0)
                {
                    zones.Add(new ZoneVolume
                    {
                        Id      = area.Id,
                        Name    = zoneName,
                        Polygon = polygon,
                        LevelId = area.LevelId,
                        Bottom  = null,
                        Top     = null,
                    });
                }
                else
                {
                    foreach (var (bottomFt, topFt, bandName) in bands)
                    {
                        zones.Add(new ZoneVolume
                        {
                            Id      = area.Id,
                            Name    = bandName,
                            Polygon = polygon,
                            LevelId = area.LevelId,
                            Bottom  = bottomFt,
                            Top     = topFt,
                        });
                    }
                }
            }
            return new ZoneAssignment(zones);
        }

        /// <summary>Total zone count across all levels + bands.</summary>
        public int TotalZoneCount => _zones.Count;

        /// <summary>Count of zones with an elevation range set (either
        /// bottom or top). Surfaced in the Sync summary so users can
        /// tell how much of the model is under vertical filtering.</summary>
        public int VerticalZoneCount =>
            _zones.Count(z => z.Bottom.HasValue || z.Top.HasValue);

        /// <summary>Spacing between centerline samples on straights.
        /// Each sample stands for an equal slice of length, so the vote
        /// count is proportional to how much of the run sits in each zone.</summary>
        private const double SampleSpacingFt = 0.5;
        private const int MinSamples = 5;
        private const int MaxSamples = 200;

        /// <summary>Returns exactly one zone name for
        /// <paramref name="part"/>, or "" when most of it sits outside
        /// every zone. Never MULTIPLE — a part spanning zones goes to the
        /// zone holding the majority of its centerline, so each part's
        /// cost lands in exactly one bucket.</summary>
        public string Classify(Element part)
        {
            if (part == null || _zones.Count == 0) return "";
            var (samples, anchor) = CenterlineSamples(part);
            if (samples.Count == 0) return "";
            var levelId = part.LevelId;

            var votes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in samples)
            {
                string z = ZoneAt(p, levelId);
                votes[z] = votes.TryGetValue(z, out int n) ? n + 1 : 1;
            }
            int top = votes.Values.Max();
            var tied = votes.Where(kv => kv.Value == top).Select(kv => kv.Key).ToList();
            if (tied.Count == 1) return tied[0];

            // Exact tie: the zone at the part's middle wins, else a real
            // zone beats "outside", else alphabetical for determinism.
            string atAnchor = ZoneAt(anchor, levelId);
            if (tied.Contains(atAnchor)) return atAnchor;
            return tied.Where(s => s.Length > 0)
                       .OrderBy(s => s, StringComparer.Ordinal)
                       .FirstOrDefault() ?? "";
        }

        /// <summary>Points on the part's centerline, most reliable source
        /// first, plus an anchor (the part's middle) for tie-breaks:
        /// hanger → the point on its host's centerline under it;
        /// straights → evenly spaced along the curve; fittings / valves →
        /// End-connector origins plus their centroid; then LocationPoint;
        /// then bbox centre.</summary>
        private static (List<XYZ> Samples, XYZ Anchor) CenterlineSamples(Element part)
        {
            var bbox = part.get_BoundingBox(null);
            XYZ? centre = bbox == null ? null : (bbox.Min + bbox.Max) * 0.5;

            if (part is FabricationPart hanger && centre != null && IsHanger(hanger))
            {
                var onHost = ProjectOntoHostCenterline(hanger, centre);
                if (onHost != null) return (new List<XYZ> { onHost }, onHost);
            }

            if (part.Location is LocationCurve lc && lc.Curve != null)
            {
                var c = lc.Curve;
                int n = Math.Clamp((int)Math.Ceiling(c.Length / SampleSpacingFt),
                    MinSamples, MaxSamples);
                var pts = new List<XYZ>(n);
                for (int i = 0; i < n; i++)
                    pts.Add(c.Evaluate((i + 0.5) / n, true));
                return (pts, c.Evaluate(0.5, true));
            }

            if (part is FabricationPart fp)
            {
                var ends = EndConnectorOrigins(fp);
                if (ends.Count > 0)
                {
                    // The centroid is an extra vote, so a two-connector
                    // valve straddling a line goes where its body is.
                    var mid = ends.Aggregate(XYZ.Zero, (s, p) => s + p) / ends.Count;
                    ends.Add(mid);
                    return (ends, mid);
                }
            }

            if (part.Location is LocationPoint lp)
                return (new List<XYZ> { lp.Point }, lp.Point);
            return centre != null
                ? (new List<XYZ> { centre }, centre)
                : (new List<XYZ>(), XYZ.Zero);
        }

        private static bool IsHanger(FabricationPart fp)
        {
            try { return fp.IsAHanger(); } catch { return false; }
        }

        private static XYZ? ProjectOntoHostCenterline(FabricationPart hanger, XYZ near)
        {
            try
            {
                var info = hanger.GetHostedInfo();
                if (info == null || !info.IsValidObject) return null;
                var host = hanger.Document.GetElement(info.HostId);
                if (host?.Location is not LocationCurve hlc || hlc.Curve == null)
                    return null;
                return hlc.Curve.Project(near)?.XYZPoint;
            }
            catch { return null; }
        }

        private static List<XYZ> EndConnectorOrigins(FabricationPart fp)
        {
            var pts = new List<XYZ>();
            try
            {
                var cm = fp.ConnectorManager;
                if (cm == null) return pts;
                foreach (Connector c in cm.Connectors)
                    if (c.ConnectorType == ConnectorType.End) pts.Add(c.Origin);
            }
            catch { pts.Clear(); }
            return pts;
        }

        /// <summary>All zone names known to this assignment, sorted
        /// alphabetically. Used by the report to enumerate buckets.</summary>
        public IReadOnlyList<string> AllZoneNames() =>
            _zones
                .Select(z => z.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // ── Internals ────────────────────────────────────────────────

        /// <summary>The one zone containing <paramref name="pt"/>, or ""
        /// when none does. Where zones overlap, the most specific wins:
        /// a vertical band beats a whole-level zone, a narrower band
        /// beats a taller one, then name order.</summary>
        private string ZoneAt(XYZ pt, ElementId partLevelId)
        {
            ZoneVolume? best = null;
            foreach (var z in _zones)
            {
                if (!IsPointInPolygon(z.Polygon, pt)) continue;

                if (z.IsVertical)
                {
                    // Half-open [bottom, top) so a point exactly on a
                    // shared boundary lands in the upper band.
                    if (z.Bottom.HasValue && pt.Z <  z.Bottom.Value) continue;
                    if (z.Top.HasValue    && pt.Z >= z.Top.Value)    continue;
                }
                else if (z.LevelId != partLevelId)
                {
                    continue;
                }
                if (best == null || IsMoreSpecific(z, best)) best = z;
            }
            return best?.Name ?? "";
        }

        private static bool IsMoreSpecific(ZoneVolume a, ZoneVolume b)
        {
            if (a.IsVertical != b.IsVertical) return a.IsVertical;
            double ha = BandHeight(a), hb = BandHeight(b);
            if (ha != hb) return ha < hb;
            return string.CompareOrdinal(a.Name, b.Name) < 0;
        }

        private static double BandHeight(ZoneVolume z) =>
            z.Bottom.HasValue && z.Top.HasValue
                ? z.Top.Value - z.Bottom.Value
                : double.PositiveInfinity;

        /// <summary>Standard even-odd (crossing-number) point-in-
        /// polygon in XY. Uses feet — Revit's internal units. Robust
        /// enough for building-scale polygons where nobody's testing
        /// pathological input.</summary>
        private static bool IsPointInPolygon(List<XYZ> poly, XYZ pt)
        {
            if (poly == null || poly.Count < 3) return false;
            double x = pt.X, y = pt.Y;
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;
                bool crosses = ((yi > y) != (yj > y)) &&
                    (x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi);
                if (crosses) inside = !inside;
            }
            return inside;
        }

        /// <summary>Flattens an Area's boundary loops into a single
        /// outer polygon. Ignores inner holes for V1 — an Area with a
        /// donut hole would still classify a point in the hole as
        /// "inside", which is fine for zone-classification purposes
        /// (an actual hole would be a rare authoring pattern here).</summary>
        private static List<XYZ>? BuildPolygon(Area area,
            SpatialElementBoundaryOptions opts)
        {
            var loops = area.GetBoundarySegments(opts);
            if (loops == null || loops.Count == 0) return null;

            var outer = loops.OrderByDescending(l => l.Count).First();
            var poly = new List<XYZ>();
            foreach (var seg in outer)
            {
                var curve = seg.GetCurve();
                if (curve == null) continue;
                if (curve is Line ln)
                {
                    poly.Add(ln.GetEndPoint(0));
                }
                else
                {
                    const int samples = 8;
                    for (int i = 0; i < samples; i++)
                    {
                        double t = i / (double)samples;
                        poly.Add(curve.Evaluate(t, true));
                    }
                }
            }
            return poly.Count >= 3 ? poly : null;
        }

        /// <summary>One zone footprint + its filter mode. IsVertical
        /// flips true as soon as either elevation param is set, so the
        /// two modes are mutually exclusive per-zone (though the
        /// project can freely mix horizontal + vertical zones).</summary>
        private sealed class ZoneVolume
        {
            public ElementId Id      = ElementId.InvalidElementId;
            public string    Name    = "";
            public List<XYZ> Polygon = new();
            public ElementId LevelId = ElementId.InvalidElementId;
            public double?   Bottom;
            public double?   Top;

            public bool IsVertical => Bottom.HasValue || Top.HasValue;
        }
    }
}
