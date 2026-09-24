using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Computes labour cost per FabricationPart by walking its ancillary
    /// usages, looking up each ancillary's name as a labour-table key in
    /// the parsed etimes/ftimes breakpoint tables, and summing
    ///   (minutes-at-size / 60) × $/hr × quantity-at-that-size
    /// across every ancillary that has a matching table.
    ///
    /// Size resolution depends on UsageType:
    ///   • UsageType == Connector  → size comes from the part's connectors
    ///     (Connector.Radius × 2 in inches). FabricationAncillaryUsage's
    ///     own AncillaryWidthOrDiameter is reported as 0 for connector-
    ///     scoped ancillaries because Revit aggregates them across the
    ///     part's connectors — the actual size lives on each connector.
    ///   • Anything else → falls back to AncillaryWidthOrDiameter /
    ///     AncillaryDepth / Length on the usage record itself.
    ///
    /// Quantity is used as the connector count for per-connector
    /// ancillaries; we pair it with the FIRST N connector sizes from the
    /// part's ConnectorManager. For uniform-bore fittings (elbows,
    /// straight tees) all connectors share a size so this gives the
    /// right answer. For multi-size fittings (reducers) it may need
    /// refinement — flagged in the project notes.
    ///
    /// Fills the E-Rate slot when constructed for installation
    /// (isErection=true), F-Rate slot for fabrication. MRate stays null.
    /// </summary>
    public sealed class AncillaryLabourPricingSource : IPricingSource
    {
        private readonly EtimesBreakpointParser _parser;
        private readonly double _ratePerHour;
        private readonly bool _isErection;
        private readonly ItmFileIndex? _itmIndex;
        public string Description { get; }

        public AncillaryLabourPricingSource(EtimesBreakpointParser parser,
            double ratePerHour, string labourTypeName, bool isErection,
            ItmFileIndex? itmIndex = null)
        {
            _parser = parser;
            _ratePerHour = ratePerHour;
            _isErection = isErection;
            _itmIndex = itmIndex;
            string itmSuffix = itmIndex != null
                ? $"; +ITM install lookup ({itmIndex.EntryCount:N0} parts)"
                : "";
            Description = isErection
                ? $"Installation labor (etimes × {labourTypeName} @ ${ratePerHour:0.##}/hr; {parser.TableCount} tables{itmSuffix})"
                : $"Fabrication labor (ftimes × {labourTypeName} @ ${ratePerHour:0.##}/hr; {parser.TableCount} tables)";
        }

        public PartRates Lookup(FabricationPart part)
        {
            if (_ratePerHour <= 0) return PartRates.Empty;

            // Per-table minutes for THIS part — same computation as the
            // Labor Estimate report and Cost Breakdown dialog use
            // (single-source-of-truth). Regular bucket is charged at the
            // section rate; ConnectorMicroRate is charged at Fab UI's
            // internal $1/hr connector-fab rate. Both summed into one
            // labour cost for the E-Rate / F-Rate slot.
            var byTable = AncillaryLabourBreakdown.ComputeMinutesByTable(
                part, _parser, isFabrication: !_isErection, _itmIndex);

            double regularMinutes = 0;
            foreach (var v in byTable.Regular.Values) regularMinutes += v;
            double microMinutes = 0;
            foreach (var v in byTable.ConnectorMicroRate.Values) microMinutes += v;
            if (regularMinutes <= 0 && microMinutes <= 0) return PartRates.Empty;

            double labourCost =
                (regularMinutes / 60.0) * _ratePerHour +
                (microMinutes   / 60.0) * AncillaryLabourBreakdown.ConnectorFabMicroRate;

            return _isErection
                ? new PartRates(null, labourCost, null)   // E-Rate slot
                : new PartRates(null, null, labourCost);  // F-Rate slot
        }
    }
}
