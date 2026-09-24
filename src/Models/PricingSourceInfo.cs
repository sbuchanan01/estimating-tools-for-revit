using System.Collections.Generic;

namespace EstimatingTools.Models
{
    /// <summary>
    /// Persisted Pricing Setup configuration. Both Fab Database and one
    /// or more CSV files may be active simultaneously — Pricing Sync chains
    /// them in order (Fab DB first, then CSVs in list order) and uses the
    /// first non-null value found per rate field. Stored on the document's
    /// Project Information element via Extensible Storage.
    /// </summary>
    public sealed class PricingSourceInfo
    {
        /// <summary>
        /// Path to a fabrication Database folder (the parent of supplier.map).
        /// Empty when Fab Database isn't being used.
        /// </summary>
        public string DatabaseFolder { get; set; } = "";

        /// <summary>Zero-or-more CSV file paths, applied in order.</summary>
        public List<string> CsvFiles { get; set; } = new();

        // ── Labour rates (user-entered $/hr) ───────────────────────────────
        // Rates are user-entered, NOT derived from cost.map at
        // runtime. Pricing Setup pre-fills from cost.map on first select
        // (so the UI shows the database default) but the user can override
        // freely. cost.map is no longer consulted during sync.
        //
        // Type NAME stays around for display only (so the Cost Breakdown
        // header reads "Skilled @ $30/hr" rather than just "@ $30/hr"). It
        // can be anything — including names the user typed that don't
        // exist in cost.map.

        /// <summary>Installation labour type name (display label).</summary>
        public string ErectionLabourTypeName { get; set; } = "";

        /// <summary>Installation labour rate ($/hr, user-entered).</summary>
        public double ErectionRatePerHour { get; set; } = 0;

        /// <summary>Fabrication labour type name (display label).</summary>
        public string FabricationLabourTypeName { get; set; } = "";

        /// <summary>Fabrication labour rate ($/hr, user-entered).</summary>
        public double FabricationRatePerHour { get; set; } = 0;

        // ── Revit parameter mapping ────────────────────────────────────────
        // Pricing Sync writes the three computed rates to user-mapped
        // project parameters. No more hardcoded "Cost - Product" / "Cost -
        // Labor" / "Cost - Fabrication" names — users with different Revit
        // templates can map to their own parameter naming convention.
        //
        // Empty means "not mapped" — Pricing Sync hard-fails until every
        // active rate (one that produces a non-null value) has a target
        // parameter assigned.

        /// <summary>Revit project parameter name receiving M-Rate ($).</summary>
        public string MaterialRateParamName { get; set; } = "";

        /// <summary>Revit project parameter name receiving E-Rate ($).</summary>
        public string InstallRateParamName { get; set; } = "";

        /// <summary>Revit project parameter name receiving F-Rate ($).</summary>
        public string FabricationRateParamName { get; set; } = "";

        /// <summary>
        /// When true, Pricing Sync writes E-Rate and F-Rate to their
        /// mapped Revit parameters as <b>labor hours</b> instead of
        /// dollar cost. Computed as <c>dollars ÷ hourly rate</c> using
        /// the ErectionRatePerHour / FabricationRatePerHour values
        /// configured above. M-Rate always stays in dollars — materials
        /// don't have hours.
        ///
        /// Useful for shops that want to schedule labor as hours in
        /// Revit and price it downstream, or for verifying that the
        /// times-map lookups agree with the shop's own estimates.
        /// If a labor rate is 0 (unusable divisor) the corresponding
        /// rate is skipped with a warning in the sync summary.
        /// </summary>
        public bool WriteLaborAsHours { get; set; } = false;

        // ── Excel template ─────────────────────────────────────────────────

        /// <summary>
        /// Optional path to an .xlsx template used by the three Generate
        /// Estimate commands (Material / Labor / Material + Labor). When
        /// set AND the file exists, estimate output goes through
        /// <see cref="EstimatingTools.Revit.XlsxTemplateFiller"/> which
        /// replaces <c>{{Placeholder}}</c> cells and expands
        /// <c>{{TableName.FieldName}}</c> row anchors with data while
        /// preserving the template's formatting / branding / layout.
        /// When empty or missing, estimate commands fall back to their
        /// existing plain output (CSV for Material/Labor, MinimalXlsx-
        /// Writer 3-sheet workbook for Combined).
        /// </summary>
        public string EstimateTemplatePath { get; set; } = "";

        // ── Material breakdown options ─────────────────────────────────────

        /// <summary>
        /// When true, ancillaries with <c>UsageType=Loose</c> (e.g.
        /// body-scoped sealants like Class A duct sealants) are included
        /// in the material cost breakdown. Default false — matches Fab
        /// ESTmep's Cost Breakdown for pipe parts. Set true on duct-heavy
        /// projects where Fab DOES bill the Class A sealant.
        /// </summary>
        public bool IncludeLooseAncillaries { get; set; } = false;

        public bool UseFabDatabase => !string.IsNullOrWhiteSpace(DatabaseFolder);
        public bool UseCsv         => CsvFiles.Count > 0;
        public bool IsConfigured   => UseFabDatabase || UseCsv;

        /// <summary>True when every rate-receiving slot the user enabled
        /// also has a Revit parameter mapped. Pricing Sync hard-fails when
        /// this returns false; the dialog should warn before save too.</summary>
        public bool HasParameterMapping =>
            !string.IsNullOrWhiteSpace(MaterialRateParamName) ||
            !string.IsNullOrWhiteSpace(InstallRateParamName)  ||
            !string.IsNullOrWhiteSpace(FabricationRateParamName);
    }
}
