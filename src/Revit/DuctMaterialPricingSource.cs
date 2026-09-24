using System;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Shared utility for sheet-metal duct material costing. Pulls weight
    /// × $/lb from <see cref="MaterialMapReader"/> using the part's
    /// material name + wire gauge (resolved through
    /// <see cref="FabricationConfiguration"/>). Single source of truth for
    /// the calc — both Pricing Sync and Cost Breakdown delegate here so
    /// the M-Rate matches Fab ESTmep's Cost Breakdown view across every
    /// codepath.
    /// </summary>
    public static class DuctMaterialResolver
    {
        /// <summary>
        /// True when the part is a duct (HVAC domain). Walks
        /// <see cref="ConnectorManager.Connectors"/> looking for the first
        /// connector whose <see cref="Connector.Domain"/> resolves —
        /// <c>DomainHvac</c> → true, <c>DomainPiping</c> → false. Works
        /// for both curve-based duct runs (LocationCurve) and duct
        /// fittings (LocationPoint).
        /// </summary>
        public static bool IsDuct(FabricationPart part)
        {
            try
            {
                var cm = part.ConnectorManager;
                if (cm == null) return false;
                foreach (Connector c in cm.Connectors)
                {
                    if (c == null) continue;
                    try
                    {
                        if (c.Domain == Domain.DomainHvac)   return true;
                        if (c.Domain == Domain.DomainPiping) return false;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Per-piece sheet-metal material cost for a duct part:
        /// <c>part.Weight (kg → lb) × $/lb</c>, where $/lb is keyed by
        /// (Material name, WireGauge) in Material.MAP. Returns 0 when:
        /// <list type="bullet">
        /// <item>FabricationConfiguration is unavailable</item>
        /// <item>Material / gauge identifiers don't resolve</item>
        /// <item>Material.MAP has no matching row for the (material, gauge) pair</item>
        /// <item>Part weight is zero or unreadable</item>
        /// </list>
        /// Used as-is for duct fittings (per-piece) AND as the numerator
        /// for the per-foot calc on curve-based duct runs (caller divides
        /// by length).
        /// </summary>
        public static double ComputePerPiece(FabricationPart part,
                                             MaterialMapReader material)
        {
            try
            {
                FabricationConfiguration? cfg = null;
                try { cfg = FabricationConfiguration.GetFabricationConfiguration(part.Document); }
                catch { }
                if (cfg == null) return 0;

                int materialId = part.Material;
                int gaugeIdx   = part.MaterialGauge;

                string materialName;
                try { materialName = cfg.GetMaterialName(materialId) ?? ""; }
                catch { return 0; }
                if (string.IsNullOrEmpty(materialName)) return 0;

                // GetMaterialWireGauge is declared since Revit 2017 in the
                // API XML but isn't exposed on 2026's public RevitAPI.dll
                // surface, so we reach for it via reflection (same pattern
                // as FabricationPartType.ItemPath / GetAllLoadedItemFiles).
                int wireGauge = 0;
                try
                {
                    var m = cfg.GetType().GetMethod("GetMaterialWireGauge",
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance);
                    if (m != null)
                    {
                        var r = m.Invoke(cfg, new object[] { materialId, gaugeIdx });
                        if (r is int n) wireGauge = n;
                    }
                }
                catch { return 0; }
                if (wireGauge <= 0) return 0;

                var row = material.Lookup(materialName, wireGauge);
                if (row == null) return 0;

                // Weight: Revit's internal mass unit is kilograms → convert
                // to pounds before multiplying by Material.MAP's $/lb.
                double weightKg = 0;
                try { weightKg = part.Weight; } catch { return 0; }
                if (weightKg <= 0) return 0;
                double weightLb = weightKg * 2.20462262;

                return weightLb * row.CostPerLb;
            }
            catch { }
            return 0;
        }
    }

    /// <summary>
    /// <see cref="IPricingSource"/> for sheet-metal duct M-Rate.
    /// Combines TWO costs for ducts (matches Fab ESTmep's Cost
    /// Breakdown view exactly):
    /// <list type="bullet">
    /// <item><b>Base material:</b> Material.MAP weight × $/lb (per
    /// piece — total for the entire duct/fitting, not per-foot)</item>
    /// <item><b>Ancillary roll-up:</b> sealants, screws, corner pieces,
    /// etc. — same calc <see cref="SupplierMapPricingSource"/> applies
    /// to every part (whose canonical M-Rate path goes through
    /// supplier.map first). We delegate to the supplier source's
    /// <see cref="SupplierMapPricingSource.LookupBreakdown"/> to avoid
    /// duplicating the joint-wrapper / kit / loose-toggle logic.</item>
    /// </list>
    /// MUST be placed FIRST in the chain — supplier.map alone returns
    /// just the ancillary total for ducts (no base price), which would
    /// satisfy the ChainedPricingSource's first-non-null and starve
    /// the M-Rate of the weight × $/lb base. Putting this source first
    /// fills the slot with the full base+ancillary total for ducts;
    /// non-ducts get <see cref="PartRates.Empty"/> so the chain falls
    /// through to supplier.map / CSV.
    ///
    /// Always per-piece — Pricing Sync's pipe-length scaler MUST skip
    /// ducts (the weight × $/lb cost already includes the full piece).
    /// </summary>
    public sealed class DuctMaterialPricingSource : IPricingSource
    {
        private readonly MaterialMapReader _material;
        private readonly SupplierMapPricingSource? _supplierForAncillary;
        public string Description { get; }

        /// <param name="material">Material.MAP reader for weight × $/lb.</param>
        /// <param name="supplierForAncillary">Optional — when set, the
        /// part's ancillary roll-up is added to the base. Pass the same
        /// <see cref="SupplierMapPricingSource"/> instance the chain
        /// uses elsewhere so the joint-wrapper / kit / loose-toggle
        /// filters stay consistent. Pass null to return base-only
        /// (callers that handle ancillaries themselves).</param>
        /// <param name="databaseFolder">For the Description string only.</param>
        public DuctMaterialPricingSource(MaterialMapReader material,
                                          SupplierMapPricingSource? supplierForAncillary,
                                          string databaseFolder)
        {
            _material = material;
            _supplierForAncillary = supplierForAncillary;
            // Empty description — this source is conceptually part of
            // "Fab Database" (Material.MAP lives alongside supplier.map
            // in the same Database folder). Surfacing it separately in
            // the result-dialog summary would imply two distinct
            // sources when they're really one configuration. The
            // ChainedPricingSource description-join filters empties so
            // this won't leave a dangling "+" in the output.
            _ = databaseFolder;
            Description = "";
        }

        public PartRates Lookup(FabricationPart part)
        {
            if (!DuctMaterialResolver.IsDuct(part)) return PartRates.Empty;
            double basePerPiece = DuctMaterialResolver.ComputePerPiece(part, _material);
            if (basePerPiece <= 0) return PartRates.Empty;

            double ancillary = 0;
            if (_supplierForAncillary != null)
            {
                try
                {
                    var bd = _supplierForAncillary.LookupBreakdown(part);
                    ancillary = bd.AncillaryTotal;
                }
                catch { }
            }
            return new PartRates(basePerPiece + ancillary, null, null);
        }
    }
}
