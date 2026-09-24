using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using EstimatingTools.Revit;

namespace EstimatingTools
{
    [Transaction(TransactionMode.Manual)]
    public class PricingSyncCommand : IExternalCommand
    {
        // V6 schema: parameter target names live in PricingSourceInfo and
        // are user-mapped via Pricing Setup. No hardcoded fallbacks — if a
        // mapping is empty when sync runs, the corresponding rate is just
        // not written. If a mapping IS set but the parameter doesn't exist
        // (or has the wrong type), sync hard-fails before writing anything.

        // Family loaded by the user that we drop at the centre of any ITM
        // missing a real Product Cost (so the value comes from the ancillary
        // kit). Marker instances are tagged via the Comments parameter so
        // subsequent syncs can clean them up before placing fresh ones.
        private const string MARKER_FAMILY_NAME = "Missing Cost";
        private const string MARKER_TAG         = "EstimatingTools:MissingCost";

        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Pricing Sync", "No active document.");
                return Result.Cancelled;
            }
            var doc = uiDoc.Document;

            // Read configured source(s) and build a chain. Fab Database first
            // (if enabled), then each CSV in list order. First non-null value
            // per rate field wins.
            var info = PricingSourceSchema.Read(doc);
            if (!info.IsConfigured)
            {
                TaskDialog.Show("Pricing Sync",
                    "No pricing source configured. Run Pricing Setup first.");
                return Result.Cancelled;
            }

            // Validate Revit parameter mapping BEFORE building anything. We
            // need at least ONE mapped target (otherwise sync would be a
            // no-op), and any mapped name must resolve to an existing Text /
            // Number / Currency parameter on a sample FabricationPart. Hard
            // fail with a dialog enumerating the problems.
            var paramErrors = ValidateParameterMapping(doc, info);
            if (paramErrors.Count > 0)
            {
                var sbErr = new StringBuilder();
                sbErr.AppendLine("Pricing Sync can't run until the parameter mapping is fixed:");
                sbErr.AppendLine();
                foreach (var err in paramErrors) sbErr.Append("  • ").AppendLine(err);
                sbErr.AppendLine();
                sbErr.AppendLine("Open Pricing Setup → Revit parameter mapping section to assign " +
                                 "Text / Number / Currency parameters as M / E / F-Rate targets.");
                TaskDialog.Show("Pricing Sync", sbErr.ToString());
                return Result.Cancelled;
            }

            // Keep a direct handle on the supplier-map source so we can ask
            // for a per-part breakdown (needed for missing-cost detection)
            // without re-reading the .map file.
            SupplierMapPricingSource? supplierSrc = null;
            IPricingSource source;
            try
            {
                var chain = new List<IPricingSource>();
                if (info.UseFabDatabase)
                {
                    // Pre-load etimes if present so SupplierMapPricingSource
                    // can use its table names as the joint-wrapper filter
                    // for ancillary material roll-up (excludes labour
                    // containers like "Flange 150" / "GRC_Flange-150# (ASME B16.5)"
                    // that Fab UI shows as headers, not material lines).
                    EstimatingTools.Revit.EtimesBreakpointParser? etimes = null;
                    string etimesPath = System.IO.Path.Combine(info.DatabaseFolder, "etimes.map");
                    if (System.IO.File.Exists(etimesPath))
                    {
                        try { etimes = new EstimatingTools.Revit.EtimesBreakpointParser(etimesPath); }
                        catch { }
                    }
                    supplierSrc = new SupplierMapPricingSource(info.DatabaseFolder, etimes);

                    // Sheet-metal duct material via Material.MAP. MUST be
                    // added to the chain BEFORE supplier.map so it wins
                    // the M-Rate slot for ducts — supplier.map alone
                    // returns just the ancillary roll-up (sealants /
                    // screws / corners) for ducts, which would starve
                    // M-Rate of the weight × $/lb base. DuctMaterialPricing-
                    // Source composes weight × $/lb (base) + supplier's
                    // ancillary total into one M-Rate, matching Cost
                    // Breakdown's "MaterialExtended = base + ancillary"
                    // formula. Returns Empty for non-duct parts so the
                    // chain falls through to supplier on pipes / fittings.
                    string materialMapPath = System.IO.Path.Combine(info.DatabaseFolder, "Material.map");
                    if (System.IO.File.Exists(materialMapPath))
                    {
                        try
                        {
                            var materialReader = new MaterialMapReader(materialMapPath);
                            chain.Add(new DuctMaterialPricingSource(
                                materialReader, supplierSrc, info.DatabaseFolder));
                        }
                        catch { /* swallow — duct material is optional */ }
                    }

                    chain.Add(supplierSrc);

                    // Labour sources: now driven by EtimesBreakpointParser
                    // (binary format cracked) + ancillary-material name
                    // lookup. Rates are V6 user-entered values (cost.map is
                    // pre-fill only in the Pricing Setup dialog; never
                    // consulted at sync time). The chain entries fill the
                    // E-Rate / F-Rate slots; the chained source's first-
                    // non-null logic keeps them from interfering with the
                    // M-Rate from supplier.map.
                    AddLabourSourceIfReady(chain, doc, info.DatabaseFolder, "etimes.map",
                        info.ErectionLabourTypeName, info.ErectionRatePerHour, isErection: true);
                    AddLabourSourceIfReady(chain, doc, info.DatabaseFolder, "ftimes.map",
                        info.FabricationLabourTypeName, info.FabricationRatePerHour, isErection: false);
                }
                foreach (var csv in info.CsvFiles)
                    chain.Add(new CsvPricingSource(csv));
                source = new ChainedPricingSource(chain);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Pricing Sync",
                    $"Failed to load pricing source:\n{ex.Message}\n\n" +
                    "Run Pricing Setup first to fix the configuration.");
                return Result.Failed;
            }

            // Walk all FabricationPart instances in the project.
            var parts = new FilteredElementCollector(doc)
                .OfClass(typeof(FabricationPart))
                .Cast<FabricationPart>()
                .ToList();

            int updated   = 0;
            int noRates   = 0;
            int noParams  = 0;
            int pipesScaled = 0;
            // RFA Pipe Accessory tally — populated inside the same
            // transaction and surfaced in the result summary below.
            int rfaSeen    = 0;
            int rfaUpdated = 0;
            int rfaNoCode  = 0;
            int rfaNoPrice = 0;
            int rfaNoParam = 0;
            // Parts whose Cost - Product was written, but the value came
            // entirely from ancillary roll-up because supplier.map and the
            // ITM-level Cost both have no entry. Tracked so we can:
            //   1) report a count + list to the user,
            //   2) drop a "Missing Cost" marker family at each one's centre,
            //   3) let the user click Show to select them in Revit.
            var missingCost = new List<MissingCostHit>();

            // The mapping is already validated above (ValidateParameterMapping
            // hard-fails before we reach this point), so these names either
            // resolve correctly on every part OR are intentionally empty
            // (= "skip this rate"). No fallback / hardcoded defaults.
            string paramM = info.MaterialRateParamName;
            string paramE = info.InstallRateParamName;
            string paramF = info.FabricationRateParamName;

            try
            {
                using var tx = new Transaction(doc, "Pricing Sync");
                tx.Start();

                foreach (var part in parts)
                {
                    var rates = source.Lookup(part);
                    if (!rates.HasAny) { noRates++; continue; }

                    // Domain-aware geometry classification:
                    //   Pipe   = LocationCurve + DomainPiping
                    //   Duct   = LocationCurve + DomainHvac  (NEVER length-scaled —
                    //            Material.MAP returns the full piece cost via
                    //            weight × $/lb already)
                    //   Fitting/valve/hanger = LocationPoint
                    //   Duct fitting = LocationPoint + DomainHvac
                    bool isCurveBased = part.Location is LocationCurve lc && lc.Curve != null;
                    bool isDuct       = DuctMaterialResolver.IsDuct(part);
                    bool isPipe       = isCurveBased && !isDuct;
                    double curveLengthFt = isCurveBased
                        ? ((LocationCurve)part.Location).Curve.Length : 0;

                    if (isPipe)
                    {
                        // Pipe M-Rate from supplier.map is $/ft → scale by
                        // length. E/F-Rate from AncillaryLabourPricing-
                        // Source already include length internally (their
                        // ComputePipeMinutesByTable multiplies minutes-per-
                        // foot × pipe length), so DON'T scale them here —
                        // doing so would multiply by length twice (the bug
                        // that wrote $67.50 for a 5' copper pipe that
                        // should be $13.50).
                        rates = new PartRates(
                            rates.MRate.HasValue ? rates.MRate * curveLengthFt : null,
                            rates.ERate,
                            rates.FRate);
                        pipesScaled++;
                    }
                    // Ducts (curve-based AND fittings): M-Rate already total
                    // per piece via DuctMaterialPricingSource (weight × $/lb).
                    // No length scaling. Labour sides scale internally for
                    // curve-based ducts (per-foot duct labour rules).

                    // Round to 2 dp at write time — matches the Cost Breakdown
                    // dialog's display precision and avoids ".789" tails in
                    // Revit schedules. Banker's-rounding would clip $0.005 →
                    // $0.00 unpredictably; AwayFromZero is the conventional
                    // money rounding mode.
                    //
                    // When WriteLaborAsHours is on, E/F values get divided by
                    // their hourly rate so the parameter carries hours
                    // instead of dollars. M-Rate always stays in dollars —
                    // materials don't have hours. A 0 rate would divide-by-
                    // zero so we skip that side and let the summary report
                    // the count.
                    bool wroteAny = false;
                    if (rates.MRate.HasValue && !string.IsNullOrEmpty(paramM) &&
                        SetIfWritable(part, paramM, Round2(rates.MRate.Value))) wroteAny = true;

                    double? eValue = ComputeLaborWriteValue(
                        rates.ERate, info.WriteLaborAsHours, info.ErectionRatePerHour);
                    if (eValue.HasValue && !string.IsNullOrEmpty(paramE) &&
                        SetIfWritable(part, paramE, Round2(eValue.Value))) wroteAny = true;

                    double? fValue = ComputeLaborWriteValue(
                        rates.FRate, info.WriteLaborAsHours, info.FabricationRatePerHour);
                    if (fValue.HasValue && !string.IsNullOrEmpty(paramF) &&
                        SetIfWritable(part, paramF, Round2(fValue.Value))) wroteAny = true;
                    if (wroteAny) updated++;
                    else          noParams++;

                    // Missing-Product-Cost detection: flags parts whose
                    // M-Rate came ENTIRELY from ancillary roll-up
                    // (supplier.map has no list price + ITM has no Cost).
                    // Skipped for:
                    //   • pipes — a pipe with a $/ft entry in supplier.map
                    //     is the common case; pipes with no entry are
                    //     surfaced by the existing "no rates" counter
                    //   • ducts AND duct fittings — Material.MAP weight ×
                    //     $/lb satisfies the cost; supplier.map's silence
                    //     is by design, not a missing-price condition
                    if (!isPipe && !isDuct && wroteAny && supplierSrc != null)
                    {
                        var bd = supplierSrc.LookupBreakdown(part);
                        if (bd.MissingProductCost)
                        {
                            missingCost.Add(new MissingCostHit
                            {
                                Id          = part.Id,
                                ProductCode = ReadProductCode(part),
                                Description = ReadDescription(part),
                                AncillaryTotal = bd.AncillaryTotal,
                                Centre = TryGetPartCentre(part),
                            });
                        }
                    }
                }

                // ── Native RFA Pipe Accessory pass ─────────────────────
                // Walk every FamilyInstance in OST_PipeAccessory that
                // isn't a FabricationPart (OfClass implicit exclusion).
                // For each one with a populated "Product Code" parameter:
                //   - Compute material price = supplier.map base +
                //     ITM-declared ancillary kit sum.
                //   - Write to the mapped M-Rate parameter via the
                //     existing SetIfWritable helper.
                // Mapped E-Rate / F-Rate are NOT computed for RFAs (no
                // labour table lookup for native content yet; out of
                // scope for v1).
                if (!string.IsNullOrEmpty(paramM) && supplierSrc != null)
                {
                    // Pull the raw SupplierMapReader directly from the
                    // .map file path. supplierSrc wraps a reader but
                    // doesn't expose a code-only Lookup, so we open
                    // our own. Same file, no contention since map
                    // files are read-only at runtime.
                    SupplierMapReader? rfaSupplier = null;
                    if (info.UseFabDatabase)
                    {
                        string p = System.IO.Path.Combine(info.DatabaseFolder, "supplier.map");
                        if (System.IO.File.Exists(p))
                        {
                            try { rfaSupplier = new SupplierMapReader(p); }
                            catch { }
                        }
                    }
                    EstimatingTools.Revit.ItmFileIndex? rfaItmIndex = null;
                    string? itemsRoot = info.UseFabDatabase
                        ? EstimatingTools.Revit.ItmFileIndex.TryLocateItemsRoot(info.DatabaseFolder)
                        : null;
                    if (!string.IsNullOrEmpty(itemsRoot))
                    {
                        try
                        {
                            var loaded = EstimatingTools.Revit.ItmFileIndex
                                .GetLoadedItmPaths(doc);
                            rfaItmIndex = new EstimatingTools.Revit.ItmFileIndex(
                                itemsRoot!,
                                EstimatingTools.Revit.ItmFileIndex.DefaultSkipFolders,
                                loaded.Count > 0 ? loaded : null);
                        }
                        catch { /* optional */ }
                    }

                    var rfaAccessories = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_PipeAccessory)
                        .WhereElementIsNotElementType()
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .ToList();
                    rfaSeen = rfaAccessories.Count;
                    foreach (var fi in rfaAccessories)
                    {
                        string code = ReadProductCodeFromRfa(fi);
                        if (string.IsNullOrEmpty(code)) { rfaNoCode++; continue; }

                        // Base via supplier.map.
                        double? basePiece = null;
                        if (rfaSupplier != null)
                        {
                            var p = rfaSupplier.Lookup(code);
                            if (p.HasValue && p.Value > 0) basePiece = p;
                        }
                        if (!basePiece.HasValue) { rfaNoPrice++; continue; }

                        // ITM ancillary kit sum.
                        double ancSum = 0;
                        if (rfaItmIndex != null && rfaSupplier != null)
                        {
                            var entry = rfaItmIndex.GetByProductCode(code);
                            if (entry != null)
                            {
                                foreach (var ancCode in entry.AncillaryProductCodes)
                                {
                                    var ap = rfaSupplier.Lookup(ancCode);
                                    if (ap.HasValue && ap.Value > 0)
                                        ancSum += ap.Value;
                                }
                            }
                        }

                        double total = basePiece.Value + ancSum;
                        if (SetIfWritable(fi, paramM, Round2(total)))
                            rfaUpdated++;
                        else
                            rfaNoParam++;
                    }
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Pricing Sync", $"Sync failed:\n{ex.Message}");
                return Result.Failed;
            }

            // Refresh "Missing Cost" markers in a separate transaction so a
            // family-load issue can't roll back the sync. The cleanup pass
            // ALWAYS runs (even when there are zero new missing-cost hits)
            // — that's how a previously-flagged ITM gets its marker removed
            // once a price has been added.
            int markersPlaced  = 0;
            int markersRemoved = 0;
            string? markerNote = null;
            (markersPlaced, markersRemoved, markerNote) =
                RefreshMissingCostMarkers(doc, uiDoc.ActiveView, missingCost);

            // Result summary.
            var sb = new StringBuilder();
            sb.Append("Source: ").AppendLine(source.Description);
            sb.Append("Fabrication parts scanned: ").Append(parts.Count).AppendLine();
            sb.AppendLine();
            sb.Append("  Updated: ").Append(updated).AppendLine();
            sb.Append("  Pipes priced by length: ").Append(pipesScaled).AppendLine();
            sb.Append("  No rates in source: ").Append(noRates).AppendLine();
            sb.Append("  Found rates but no writable target parameter: ").Append(noParams).AppendLine();
            if (rfaSeen > 0)
            {
                sb.AppendLine();
                sb.Append("Native RFA pipe accessories scanned: ")
                  .Append(rfaSeen).AppendLine();
                sb.Append("  Updated (M-Rate written): ").Append(rfaUpdated).AppendLine();
                sb.Append("  Missing Product Code: ").Append(rfaNoCode).AppendLine();
                sb.Append("  Product Code not in supplier.map: ").Append(rfaNoPrice).AppendLine();
                sb.Append("  Found price but no writable M-Rate param on RFA: ")
                  .Append(rfaNoParam).AppendLine();
            }
            if (missingCost.Count > 0)
            {
                sb.AppendLine();
                sb.Append("⚠ ").Append(missingCost.Count)
                  .AppendLine(" ITM(s) had no Product Cost on file.");
                sb.AppendLine("  Cost - Product was set to the sum of ancillary kit items instead");
                sb.AppendLine("  (bolts, nuts, gaskets, etc.). Add a real list price in supplier.map");
                sb.AppendLine("  or on the ITM type's Cost parameter to silence this.");
                if (markersPlaced > 0)
                    sb.Append("  Placed ").Append(markersPlaced)
                      .AppendLine(" Missing Cost marker(s) in the model.");
            }
            if (markersRemoved > 0)
            {
                sb.AppendLine();
                sb.Append("✓ Removed ").Append(markersRemoved)
                  .AppendLine(" stale Missing Cost marker(s) (price now found).");
            }
            if (!string.IsNullOrEmpty(markerNote))
                sb.AppendLine().Append("  ").AppendLine(markerNote);
            // Mapped-parameter sanity is hard-failed at Execute entry, so
            // by the time we reach this summary block every name has been
            // validated. The historical "missing project parameter" warning
            // is no longer needed here.

            ShowResultDialog(uiDoc, sb.ToString(), missingCost);
            return Result.Succeeded;
        }

        /// <summary>
        /// Returns the list of human-readable problems with the parameter
        /// mapping. Empty list = OK. Hard-failure cases (any non-empty
        /// return aborts the sync):
        ///   • All three mapping fields blank → nothing to write.
        ///   • A mapped name doesn't exist on a sample FabricationPart.
        ///   • A mapped name exists but isn't a writable Double / Integer /
        ///     String parameter (StorageType.None / read-only).
        /// </summary>
        private static List<string> ValidateParameterMapping(Document doc,
            Models.PricingSourceInfo info)
        {
            var problems = new List<string>();
            bool anyMapped =
                !string.IsNullOrWhiteSpace(info.MaterialRateParamName) ||
                !string.IsNullOrWhiteSpace(info.InstallRateParamName)  ||
                !string.IsNullOrWhiteSpace(info.FabricationRateParamName);
            if (!anyMapped)
            {
                problems.Add("No target parameters mapped for M / E / F rates.");
                return problems;
            }

            var sample = new FilteredElementCollector(doc)
                .OfClass(typeof(FabricationPart))
                .FirstElement();
            // No parts in the project — defer validation (sync would no-op
            // anyway). Don't fail at this point.
            if (sample == null) return problems;

            CheckOne(sample, info.MaterialRateParamName,    "M-Rate", problems);
            CheckOne(sample, info.InstallRateParamName,     "E-Rate", problems);
            CheckOne(sample, info.FabricationRateParamName, "F-Rate", problems);
            return problems;
        }

        private static void CheckOne(Element sample, string paramName,
            string slot, List<string> problems)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return;  // unmapped is fine
            var p = sample.LookupParameter(paramName);
            if (p == null)
            {
                problems.Add($"{slot} mapped to '{paramName}', but that " +
                             "parameter doesn't exist on FabricationParts.");
                return;
            }
            if (p.IsReadOnly)
            {
                problems.Add($"{slot} mapped to '{paramName}', but the " +
                             "parameter is read-only.");
                return;
            }
            // Acceptable storage: Double (Number/Currency), Integer (Number),
            // String (Text). Anything else (None) is a mapping error.
            if (p.StorageType != StorageType.Double &&
                p.StorageType != StorageType.Integer &&
                p.StorageType != StorageType.String)
            {
                problems.Add($"{slot} mapped to '{paramName}', but the " +
                             $"parameter's storage type ({p.StorageType}) isn't " +
                             "compatible. Use a Text / Number / Currency parameter.");
            }
        }

        private static double Round2(double v) =>
            Math.Round(v, 2, MidpointRounding.AwayFromZero);

        /// <summary>
        /// Returns the value to write to an E-Rate / F-Rate parameter:
        /// dollars by default, or hours when
        /// <paramref name="asHours"/> is true. Hours = dollars ÷
        /// hourly rate; a 0 rate is unusable as a divisor so we return
        /// null and skip the write (the site loop's <c>HasValue</c>
        /// check handles that).
        /// </summary>
        private static double? ComputeLaborWriteValue(double? dollars,
            bool asHours, double ratePerHour)
        {
            if (!dollars.HasValue) return null;
            if (!asHours) return dollars.Value;
            if (ratePerHour <= 0) return null;   // divide-by-zero guard
            return dollars.Value / ratePerHour;
        }

        /// <summary>One missing-Product-Cost hit, captured during the sync pass.</summary>
        private sealed class MissingCostHit
        {
            public ElementId Id          = ElementId.InvalidElementId;
            public string    ProductCode = "";
            public string    Description = "";
            public double    AncillaryTotal;
            public XYZ?      Centre;
        }

        /// <summary>
        /// TaskDialog showing the sync result. Adds a "Show missing-cost ITMs"
        /// command link when any were detected — the user can click it to
        /// select them in Revit and zoom the active view to fit.
        /// </summary>
        private static void ShowResultDialog(UIDocument uiDoc, string body,
            List<MissingCostHit> missingCost)
        {
            var dlg = new TaskDialog("Pricing Sync")
            {
                MainInstruction = "Pricing Sync result",
                MainContent     = body,
                CommonButtons   = TaskDialogCommonButtons.Close,
                DefaultButton   = TaskDialogResult.Close,
            };

            if (missingCost.Count > 0)
            {
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    $"Show {missingCost.Count} missing-cost ITM(s)",
                    "Selects them in the project and zooms the active view to fit.");
            }

            var result = dlg.Show();
            if (result == TaskDialogResult.CommandLink1 && missingCost.Count > 0)
            {
                try
                {
                    var ids = missingCost.Select(m => m.Id)
                        .Where(id => id != ElementId.InvalidElementId)
                        .ToList();
                    uiDoc.Selection.SetElementIds(ids);
                    uiDoc.ShowElements(ids);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Pricing Sync",
                        $"Could not show elements:\n{ex.Message}");
                }
            }
        }

        // ── Missing Cost marker placement ──────────────────────────────────

        /// <summary>
        /// Reconciles "Missing Cost" markers against the current set of
        /// missing-cost ITMs. Per-ITM diff:
        ///   • ITMs that are still missing a price AND already have a marker
        ///     keep theirs (no churn).
        ///   • ITMs that are missing a price but have no marker → place one.
        ///   • Markers whose linked ITM no longer needs one (price was found,
        ///     or the ITM was deleted) → remove.
        /// Linkage is via the marker's Mark parameter, which we set to the
        /// linked ITM's ElementId on placement.
        /// </summary>
        private static (int placed, int removed, string? note)
            RefreshMissingCostMarkers(Document doc, View activeView,
                                      List<MissingCostHit> hits)
        {
            // Always run the cleanup pass even when 'hits' is empty — that's
            // how a previously-flagged ITM loses its marker after the user
            // adds a price.
            var existing = FindExistingMarkers(doc);

            FamilySymbol? symbol = null;
            if (hits.Count > 0)
            {
                symbol = FindMarkerSymbol(doc);
                if (symbol == null)
                    return (0, 0, $"'{MARKER_FAMILY_NAME}.rfa' isn't loaded — " +
                                  "markers skipped. Load the family and re-run.");
            }

            // Index existing markers by linked ITM id (stored in Mark param).
            var byLinkedId = new Dictionary<long, ElementId>();
            var orphans    = new List<ElementId>();   // markers we can't link
            foreach (var (markerId, linkedId) in existing)
            {
                if (linkedId.HasValue) byLinkedId[linkedId.Value] = markerId;
                else                   orphans.Add(markerId);
            }

            // Decide: place / keep / remove.
            var stillNeeded = new HashSet<long>(hits.Select(h => h.Id.Value));
            var toRemove = new List<ElementId>(orphans);
            foreach (var kv in byLinkedId)
            {
                if (!stillNeeded.Contains(kv.Key)) toRemove.Add(kv.Value);
            }
            var toPlace = hits.Where(h => !byLinkedId.ContainsKey(h.Id.Value))
                              .ToList();

            if (toRemove.Count == 0 && toPlace.Count == 0)
                return (0, 0, null);

            int placed  = 0;
            int removed = 0;
            int failed  = 0;

            try
            {
                using var tx = new Transaction(doc, "Refresh Missing Cost markers");
                tx.Start();

                if (toRemove.Count > 0)
                {
                    try
                    {
                        var deleted = doc.Delete(toRemove);
                        removed = deleted?.Count ?? toRemove.Count;
                    }
                    catch
                    {
                        // Tolerate strays — fall back to a per-element loop.
                        foreach (var id in toRemove)
                        {
                            try { doc.Delete(id); removed++; } catch { }
                        }
                    }
                }

                if (toPlace.Count > 0 && symbol != null)
                {
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                    bool isAnnotation = IsAnnotationCategory(symbol.Category);

                    foreach (var hit in toPlace)
                    {
                        if (hit.Centre == null) { failed++; continue; }
                        var inst = TryNewFamilyInstance(doc, hit.Centre, symbol,
                                                       activeView, isAnnotation);
                        if (inst != null) { TagInstance(inst, hit); placed++; }
                        else              { failed++; }
                    }
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                return (placed, removed, $"Marker refresh failed: {ex.Message}");
            }

            string? note = failed > 0
                ? $"{failed} marker(s) couldn't be placed (no centre or unsupported view)."
                : null;
            return (placed, removed, note);
        }

        /// <summary>
        /// Try the view-based overload first when the family is annotation,
        /// else the level-free unhosted overload. Falls back to the other
        /// overload if the first throws.
        /// </summary>
        private static FamilyInstance? TryNewFamilyInstance(Document doc, XYZ pt,
            FamilySymbol symbol, View view, bool isAnnotation)
        {
            try
            {
                return isAnnotation
                    ? doc.Create.NewFamilyInstance(pt, symbol, view)
                    : doc.Create.NewFamilyInstance(pt, symbol,
                          StructuralType.NonStructural);
            }
            catch { }
            try
            {
                return isAnnotation
                    ? doc.Create.NewFamilyInstance(pt, symbol,
                          StructuralType.NonStructural)
                    : doc.Create.NewFamilyInstance(pt, symbol, view);
            }
            catch { return null; }
        }

        private static FamilySymbol? FindMarkerSymbol(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => string.Equals(s.FamilyName,
                    MARKER_FAMILY_NAME, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns every marker we placed previously, paired with the linked
        /// ITM ElementId stored in the Mark parameter. Match key is the
        /// Comments parameter (== MARKER_TAG) — this excludes any manually
        /// placed Missing Cost instances the user may own.
        /// </summary>
        private static List<(ElementId markerId, long? linkedId)>
            FindExistingMarkers(Document doc)
        {
            var result = new List<(ElementId, long?)>();
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();
            foreach (var fi in instances)
            {
                if (fi.Symbol == null) continue;
                if (!string.Equals(fi.Symbol.FamilyName, MARKER_FAMILY_NAME,
                        StringComparison.OrdinalIgnoreCase)) continue;
                var cm = fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (cm == null || !string.Equals(cm.AsString(), MARKER_TAG,
                        StringComparison.Ordinal)) continue;

                long? linked = null;
                var mk = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (mk != null && long.TryParse(mk.AsString(), out long v))
                    linked = v;
                result.Add((fi.Id, linked));
            }
            return result;
        }

        /// <summary>
        /// Tags a freshly-placed marker:
        ///   • Comments → MARKER_TAG (so future syncs identify it as ours)
        ///   • Mark     → the linked ITM's ElementId (so future syncs can
        ///                tell which ITM each marker belongs to without
        ///                geometry guesswork)
        /// </summary>
        private static void TagInstance(FamilyInstance inst, MissingCostHit hit)
        {
            try
            {
                var p = inst.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (p != null && !p.IsReadOnly) p.Set(MARKER_TAG);
            }
            catch { }
            try
            {
                var mark = inst.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (mark != null && !mark.IsReadOnly &&
                    hit.Id != ElementId.InvalidElementId)
                    mark.Set(hit.Id.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static bool IsAnnotationCategory(Category? cat)
        {
            if (cat == null) return false;
            try
            {
                long id = cat.Id.Value;
                return id == (long)BuiltInCategory.OST_GenericAnnotation;
            }
            catch { return false; }
        }

        // ── Geometry helpers ───────────────────────────────────────────────

        /// <summary>
        /// Adds an AncillaryLabourPricingSource to the chain when a labour
        /// type AND a user-entered $/hr rate are both set AND the corresponding
        /// times .map file (etimes.map or ftimes.map) exists. Silently no-op
        /// when prerequisites aren't met — labour is optional.
        ///
        /// V6 change: the rate comes from Pricing Setup, not cost.map.
        /// cost.map is now consulted only by the dialog to pre-fill labour-
        /// type names + default rates. The user can override the rate freely.
        ///
        /// Also builds the .ITM index when the Items folder can be located
        /// from <paramref name="folder"/>; the index lets the pricing
        /// source pick up the per-part install-table contribution (the
        /// "Installation Table Cost" row from Fab's ESTmep Cost Breakdown).
        /// Best-effort: on failure the source falls back to fixings-only.
        /// </summary>
        private static void AddLabourSourceIfReady(
            System.Collections.Generic.List<IPricingSource> chain,
            Autodesk.Revit.DB.Document doc,
            string folder, string mapFileName, string labourTypeName,
            double ratePerHour, bool isErection)
        {
            if (string.IsNullOrWhiteSpace(labourTypeName)) return;
            if (ratePerHour <= 0) return;
            string mapPath = System.IO.Path.Combine(folder, mapFileName);
            if (!System.IO.File.Exists(mapPath)) return;

            try
            {
                var parser = new EtimesBreakpointParser(mapPath);
                if (parser.TableCount == 0) return;

                EstimatingTools.Revit.ItmFileIndex? itmIndex = null;
                string? itemsRoot = EstimatingTools.Revit.ItmFileIndex
                    .TryLocateItemsRoot(folder);
                if (!string.IsNullOrEmpty(itemsRoot))
                {
                    try
                    {
                        var loaded = EstimatingTools.Revit.ItmFileIndex
                            .GetLoadedItmPaths(doc);
                        itmIndex = new EstimatingTools.Revit.ItmFileIndex(
                            itemsRoot!,
                            EstimatingTools.Revit.ItmFileIndex.DefaultSkipFolders,
                            loaded.Count > 0 ? loaded : null);
                    }
                    catch { /* swallow — ITM lookup is optional */ }
                }

                var src = new AncillaryLabourPricingSource(
                    parser, ratePerHour, labourTypeName, isErection, itmIndex);
                chain.Add(src);
            }
            catch
            {
                // Bad .map file — skip rather than fail the whole sync.
            }
        }

        /// <summary>
        /// Centre point of a fabrication part. Pipes use the curve midpoint;
        /// fittings/valves/hangers use LocationPoint.Point. Falls back to the
        /// bounding-box centre when neither is available (rare).
        /// </summary>
        private static XYZ? TryGetPartCentre(FabricationPart part)
        {
            try
            {
                if (part.Location is LocationCurve lc && lc.Curve != null)
                    return lc.Curve.Evaluate(0.5, true);
                if (part.Location is LocationPoint lp)
                    return lp.Point;
            }
            catch { }
            try
            {
                var bb = part.get_BoundingBox(null);
                if (bb != null) return (bb.Min + bb.Max) * 0.5;
            }
            catch { }
            return null;
        }

        private static string ReadProductCode(FabricationPart part)
        {
            try
            {
                var bp = part.get_Parameter(BuiltInParameter.FABRICATION_PRODUCT_CODE);
                if (bp != null && bp.HasValue)
                {
                    var s = bp.AsString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Reads "Product Code" from a native Revit family instance.
        /// Type parameter wins (where estimators most naturally put a
        /// part-level code) and falls back to the instance parameter.
        /// Tries a short alias list. Mirrors the helper of the same
        /// name in MaterialEstimateCommand.
        /// </summary>
        private static string ReadProductCodeFromRfa(FamilyInstance fi)
        {
            string[] names = { "Product Code", "ProductCode", "ADSK Code" };
            try
            {
                var t = fi.Document.GetElement(fi.GetTypeId()) as ElementType;
                if (t != null)
                {
                    foreach (var n in names)
                    {
                        var p = t.LookupParameter(n);
                        if (p == null) continue;
                        string v = p.AsString() ?? p.AsValueString() ?? "";
                        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                    }
                }
            }
            catch { }
            foreach (var n in names)
            {
                try
                {
                    var p = fi.LookupParameter(n);
                    if (p == null) continue;
                    string v = p.AsString() ?? p.AsValueString() ?? "";
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
                catch { }
            }
            return "";
        }

        private static string ReadDescription(FabricationPart part)
        {
            try
            {
                var typeId = part.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var t = part.Document.GetElement(typeId) as ElementType;
                    if (t != null && !string.IsNullOrEmpty(t.Name)) return t.Name;
                }
            }
            catch { }
            return part.Name ?? "";
        }

        private static bool SetIfWritable(Element elem, string paramName, double value)
        {
            try
            {
                var p = elem.LookupParameter(paramName);
                if (p == null || p.IsReadOnly) return false;
                return p.StorageType switch
                {
                    StorageType.Double  => p.Set(value),
                    StorageType.Integer => p.Set((int)Math.Round(value)),
                    StorageType.String  => p.Set(value.ToString("0.####",
                        System.Globalization.CultureInfo.InvariantCulture)),
                    _ => false,
                };
            }
            catch { return false; }
        }

    }
}
