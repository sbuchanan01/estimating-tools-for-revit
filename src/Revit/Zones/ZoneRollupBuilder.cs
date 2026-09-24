using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using EstimatingTools.Models;

namespace EstimatingTools.Revit.Zones
{
    /// <summary>
    /// Buckets a project's Fab parts into Zone → MEP discipline →
    /// product code rows for the Estimate by Zone report. Reuses
    /// <see cref="MaterialEstimateCommand.BuildLines"/> for pricing —
    /// the aggregated LineAccums give us the UnitPrice per part; the
    /// per-zone grouping happens here by re-walking parts and reading
    /// their <see cref="EstimateZoneScheme.ZoneParamName"/>.
    ///
    /// Scope (V1):
    ///   • Material only — labor deferred.
    ///   • Primary parts only (Pipe / Fitting / Valve / Duct /
    ///     DuctFitting / Hanger). Ancillaries skipped.
    ///   • UNASSIGNED + MULTIPLE show as their own zones so nothing
    ///     gets silently dropped.
    /// </summary>
    public static class ZoneRollupBuilder
    {
        public sealed class ZoneLine
        {
            public string ProductCode = "";
            public string Description = "";
            public string FamilyName  = "";
            public string Size        = "";
            public MaterialEstimateCommand.LineKind Kind;
            public string Unit        = "EA";
            public double Quantity;
            public double UnitPrice;
            public double Total => UnitPrice * Quantity;
        }

        public sealed class ZoneDiscipline
        {
            public string Name = "";     // Piping / Ductwork / Hangers
            public List<ZoneLine> Lines = new();
            public double Total => Lines.Sum(l => l.Total);
        }

        public sealed class ZoneBucket
        {
            public string ZoneName = "";
            public List<ZoneDiscipline> Disciplines = new();

            /// <summary>Material subtotal — sum of every discipline's
            /// line totals. Same number the CSV / Push tree emits.</summary>
            public double MaterialCost => Disciplines.Sum(d => d.Total);

            /// <summary>Installation-labour dollars accrued from every
            /// part in this zone. 0 when the labour context has no
            /// installation side configured.</summary>
            public double InstallLaborCost;

            /// <summary>Fabrication-labour dollars accrued from every
            /// part in this zone. 0 when no fabrication side is set.</summary>
            public double FabLaborCost;

            /// <summary>Zone grand total = material + install + fab.</summary>
            public double Total => MaterialCost + InstallLaborCost + FabLaborCost;
        }

        public sealed class ZoneReport
        {
            public List<ZoneBucket> Zones = new();
            public int PartsScanned;
            public int PartsSkippedNoPrice;

            /// <summary>True when Pricing Setup has at least one labour
            /// side configured. Dialog uses this to decide whether to
            /// render the two labour rows per zone.</summary>
            public bool HasLabor;

            public double GrandTotal => Zones.Sum(z => z.Total);
            public double GrandMaterial => Zones.Sum(z => z.MaterialCost);
            public double GrandInstallLabor => Zones.Sum(z => z.InstallLaborCost);
            public double GrandFabLabor => Zones.Sum(z => z.FabLaborCost);
            public string SourceDescription = "";
            public string? Error;
        }

        private const string DiscPiping   = "Piping";
        private const string DiscDuctwork = "Ductwork";
        private const string DiscHangers  = "Hangers";

        public const string UnassignedZone = "UNASSIGNED";
        public const string MultipleZone   = "MULTIPLE";

        public static ZoneReport Build(Document doc, PricingSourceInfo info)
        {
            var report = new ZoneReport();

            var material = MaterialEstimateCommand.BuildLines(doc, info);
            if (material.Error != null)
            {
                report.Error = material.Error;
                return report;
            }
            report.SourceDescription = material.SourceDescription;

            // Labour context is best-effort. When no rates are configured
            // (or the .map files are missing), per-part labour comes back
            // as (0, 0) and the report degrades to material-only — same
            // shape, just with zeros in the Installation / Fabrication
            // rows. Users still see the per-zone material breakdown.
            var laborCtx = LaborEstimateCommand.BuildLabourContext(doc, info);
            report.HasLabor = laborCtx.HasAnySide;

            // Index the aggregated lines by (ProductCode, Kind) so we
            // can look up UnitPrice per part below.
            var priceIndex = new Dictionary<string,
                MaterialEstimateCommand.LineAccum>();
            foreach (var line in material.Lines)
            {
                var key = MakeKey(line.ProductCode, line.Kind);
                priceIndex[key] = line;
            }

            var buckets = new Dictionary<string, ZoneBucket>();
            var parts = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategory(BuiltInCategory.OST_FabricationPipework)
                .OfClass(typeof(FabricationPart))
                .Concat(new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .OfCategory(BuiltInCategory.OST_FabricationDuctwork)
                    .OfClass(typeof(FabricationPart)))
                .Concat(new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .OfCategory(BuiltInCategory.OST_FabricationHangers)
                    .OfClass(typeof(FabricationPart)))
                .Cast<FabricationPart>();

            foreach (var part in parts)
            {
                report.PartsScanned++;

                string codeStr = part.LookupParameter("Product Code")?.AsString()
                                 ?? part.ItemCustomId.ToString();
                var kind = MaterialEstimateCommand.ClassifyKind(part);
                if (kind == MaterialEstimateCommand.LineKind.Ancillary)
                    continue;

                double qty = MaterialEstimateCommand.ComputePartQuantity(part, kind);
                if (qty <= 0) continue;

                // Zone bucket comes first — we accrue labour into it
                // even when the part has no matched material price so a
                // part with only labour contribution still shows up.
                string zone = EstimateZoneScheme.ReadZone(part);
                if (string.IsNullOrEmpty(zone)) zone = UnassignedZone;
                if (!buckets.TryGetValue(zone, out var zb))
                    buckets[zone] = zb = new ZoneBucket { ZoneName = zone };

                if (laborCtx.HasAnySide)
                {
                    var (installCost, fabCost) =
                        LaborEstimateCommand.ComputePartLabourCost(part, laborCtx);
                    zb.InstallLaborCost += installCost;
                    zb.FabLaborCost     += fabCost;
                }

                // Material path — a miss here counts against the
                // "skipped no price" diagnostic but does NOT drop the
                // part from the zone (labour above was already recorded).
                var key = MakeKey(codeStr, kind);
                if (!priceIndex.TryGetValue(key, out var priceLine)
                    || !priceLine.UnitPrice.HasValue
                    || priceLine.UnitPrice.Value <= 0)
                {
                    report.PartsSkippedNoPrice++;
                    continue;
                }

                string disc = DisciplineOf(kind);
                var discipline = zb.Disciplines
                    .FirstOrDefault(d => d.Name == disc);
                if (discipline == null)
                {
                    discipline = new ZoneDiscipline { Name = disc };
                    zb.Disciplines.Add(discipline);
                }

                var existing = discipline.Lines
                    .FirstOrDefault(l => l.ProductCode == codeStr &&
                                         l.Kind == kind);
                if (existing == null)
                {
                    // UnitPrice on the aggregated LineAccum is already
                    // per-unit (per foot for pipes / ducts, per each
                    // otherwise) — BuildLines normalized it before we
                    // captured it. So we can just multiply here.
                    discipline.Lines.Add(new ZoneLine
                    {
                        ProductCode = codeStr,
                        Description = priceLine.Description,
                        FamilyName  = priceLine.FamilyName,
                        Size        = priceLine.Size,
                        Kind        = kind,
                        Unit        = priceLine.Unit,
                        Quantity    = qty,
                        UnitPrice   = priceLine.UnitPrice.Value,
                    });
                }
                else
                {
                    existing.Quantity += qty;
                }
            }

            report.Zones = buckets.Values
                .OrderBy(z => ZoneSortKey(z.ZoneName),
                    System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            return report;
        }

        // Named zones sort A-Z; MULTIPLE + UNASSIGNED cluster at the end.
        private static string ZoneSortKey(string name)
        {
            if (name == UnassignedZone) return "￿￿" + name;
            if (name == MultipleZone)   return "￿" + name;
            return name;
        }

        private static string DisciplineOf(MaterialEstimateCommand.LineKind kind)
        {
            switch (kind)
            {
                case MaterialEstimateCommand.LineKind.Pipe:
                case MaterialEstimateCommand.LineKind.Fitting:
                case MaterialEstimateCommand.LineKind.Valve:
                    return DiscPiping;
                case MaterialEstimateCommand.LineKind.Duct:
                case MaterialEstimateCommand.LineKind.DuctFitting:
                    return DiscDuctwork;
                case MaterialEstimateCommand.LineKind.Hanger:
                    return DiscHangers;
                default:
                    return DiscPiping;
            }
        }

        private static string MakeKey(string productCode,
            MaterialEstimateCommand.LineKind kind) =>
            $"{productCode}|{kind}";
    }
}
