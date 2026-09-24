using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using EstimatingTools.Models;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Extensible Storage for the Pricing Setup configuration. Persisted
    /// on the document's Project Information element so the setting travels
    /// with the model and survives close/reopen.
    ///
    /// The schema GUID is this add-in's own — never reuse another tool's —
    /// so its settings stay independent of any other add-in in the same
    /// model. Once released, add fields only under a NEW GUID and read the
    /// old one as a fallback; changing a shipped schema in place breaks
    /// every model that already stores it.
    /// </summary>
    public static class PricingSourceSchema
    {
        private static readonly Guid SchemaGuid =
            new Guid("3e7c536a-a5d8-457b-bde8-48df8c8f4dd2");

        private const string SchemaName    = "EstimatingToolsPricingSetup";
        // Must match <VendorId> in EstimatingTools.addin or vendor writes silently fail.
        private const string VendorId      = "ESTTL";
        private const string Documentation = "Pricing Setup configuration (Fab DB + CSV + labour rates + material toggles + param mapping + estimate template).";

        private const string F_DatabaseFolder            = "DatabaseFolder";
        private const string F_CsvFiles                  = "CsvFiles";        // newline-delimited
        private const string CsvSeparator                = "\n";
        private const string F_ErectionLabourTypeName    = "ErectionLabourTypeName";
        private const string F_FabricationLabourTypeName = "FabricationLabourTypeName";
        private const string F_IncludeLooseAncillaries   = "IncludeLooseAncillaries";
        private const string F_ErectionRatePerHour       = "ErectionRatePerHour";
        private const string F_FabricationRatePerHour    = "FabricationRatePerHour";
        private const string F_MaterialRateParamName     = "MaterialRateParamName";
        private const string F_InstallRateParamName      = "InstallRateParamName";
        private const string F_FabricationRateParamName  = "FabricationRateParamName";
        private const string F_EstimateTemplatePath      = "EstimateTemplatePath";
        private const string F_WriteLaborAsHours         = "WriteLaborAsHours";

        private static Schema? _cached;

        private static Schema GetOrCreateSchema()
        {
            if (_cached != null && _cached.IsValidObject) return _cached;

            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) { _cached = existing; return existing; }

            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName(SchemaName);
            b.SetVendorId(VendorId);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            b.SetDocumentation(Documentation);
            b.AddSimpleField(F_DatabaseFolder,             typeof(string));
            b.AddSimpleField(F_CsvFiles,                   typeof(string));
            b.AddSimpleField(F_ErectionLabourTypeName,     typeof(string));
            b.AddSimpleField(F_FabricationLabourTypeName,  typeof(string));
            b.AddSimpleField(F_IncludeLooseAncillaries,    typeof(bool));
            // Rates stored as strings: ExtensibleStorage rejects double
            // fields without a measurable unit spec, and neither Currency
            // nor Number qualifies. Written with InvariantCulture "R" so
            // they round-trip exactly.
            b.AddSimpleField(F_ErectionRatePerHour,        typeof(string));
            b.AddSimpleField(F_FabricationRatePerHour,     typeof(string));
            b.AddSimpleField(F_MaterialRateParamName,      typeof(string));
            b.AddSimpleField(F_InstallRateParamName,       typeof(string));
            b.AddSimpleField(F_FabricationRateParamName,   typeof(string));
            b.AddSimpleField(F_EstimateTemplatePath,       typeof(string));
            b.AddSimpleField(F_WriteLaborAsHours,          typeof(bool));

            _cached = b.Finish();
            return _cached;
        }

        /// <summary>Reads the saved config; returns defaults if nothing is stored yet.</summary>
        public static PricingSourceInfo Read(Document doc)
        {
            var pi = doc?.ProjectInformation;
            if (pi == null) return new PricingSourceInfo();

            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new PricingSourceInfo();
            var entity = pi.GetEntity(schema);
            if (entity == null || !entity.IsValid()) return new PricingSourceInfo();

            string S(string field)
            {
                try { return entity.Get<string>(schema.GetField(field)) ?? ""; }
                catch { return ""; }
            }
            bool B(string field)
            {
                try { return entity.Get<bool>(schema.GetField(field)); }
                catch { return false; }
            }
            double D(string field) =>
                double.TryParse(S(field), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    ? v : 0;

            return new PricingSourceInfo
            {
                DatabaseFolder            = S(F_DatabaseFolder),
                CsvFiles                  = ParseCsv(S(F_CsvFiles)),
                ErectionLabourTypeName    = S(F_ErectionLabourTypeName),
                FabricationLabourTypeName = S(F_FabricationLabourTypeName),
                IncludeLooseAncillaries   = B(F_IncludeLooseAncillaries),
                ErectionRatePerHour       = D(F_ErectionRatePerHour),
                FabricationRatePerHour    = D(F_FabricationRatePerHour),
                MaterialRateParamName     = S(F_MaterialRateParamName),
                InstallRateParamName      = S(F_InstallRateParamName),
                FabricationRateParamName  = S(F_FabricationRateParamName),
                EstimateTemplatePath      = S(F_EstimateTemplatePath),
                WriteLaborAsHours         = B(F_WriteLaborAsHours),
            };
        }

        private static List<string> ParseCsv(string blob)
        {
            return string.IsNullOrEmpty(blob)
                ? new List<string>()
                : blob.Split(new[] { CsvSeparator }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .Where(s => !string.IsNullOrEmpty(s))
                      .ToList();
        }

        /// <summary>Writes the config — caller must wrap in a Transaction.</summary>
        public static void Write(Document doc, PricingSourceInfo info)
        {
            if (doc == null || info == null) return;
            var pi = doc.ProjectInformation;
            if (pi == null) return;

            var schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(F_DatabaseFolder),            info.DatabaseFolder ?? "");
            entity.Set(schema.GetField(F_CsvFiles),
                string.Join(CsvSeparator, info.CsvFiles ?? new List<string>()));
            entity.Set(schema.GetField(F_ErectionLabourTypeName),    info.ErectionLabourTypeName    ?? "");
            entity.Set(schema.GetField(F_FabricationLabourTypeName), info.FabricationLabourTypeName ?? "");
            entity.Set(schema.GetField(F_IncludeLooseAncillaries),   info.IncludeLooseAncillaries);
            entity.Set(schema.GetField(F_ErectionRatePerHour),
                info.ErectionRatePerHour.ToString("R", CultureInfo.InvariantCulture));
            entity.Set(schema.GetField(F_FabricationRatePerHour),
                info.FabricationRatePerHour.ToString("R", CultureInfo.InvariantCulture));
            entity.Set(schema.GetField(F_MaterialRateParamName),     info.MaterialRateParamName     ?? "");
            entity.Set(schema.GetField(F_InstallRateParamName),      info.InstallRateParamName      ?? "");
            entity.Set(schema.GetField(F_FabricationRateParamName),  info.FabricationRateParamName  ?? "");
            entity.Set(schema.GetField(F_EstimateTemplatePath),      info.EstimateTemplatePath      ?? "");
            entity.Set(schema.GetField(F_WriteLaborAsHours),         info.WriteLaborAsHours);
            pi.SetEntity(entity);
        }
    }
}
