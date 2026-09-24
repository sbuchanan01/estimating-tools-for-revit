using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;
using EstimatingTools.Revit;

namespace EstimatingTools
{
    /// <summary>
    /// Resets the mapped M / E / F-Rate parameter values to zero on every
    /// FabricationPart in the project. Intended for the "share this model
    /// externally without cost data" workflow — strip pricing from every
    /// part, hand off the file, then re-run Pricing Sync to restore.
    ///
    /// Scope matches Pricing Sync exactly: walks `FabricationPart` instances
    /// and writes 0 (or "" for Text-typed params) to whichever parameters
    /// the user mapped under Pricing Setup → Revit parameter mapping. Any
    /// parameter name left unmapped in Pricing Setup is left alone — we
    /// only touch the slots Pricing Sync would write to.
    ///
    /// Destructive operation, gated by a confirmation TaskDialog showing
    /// the exact parameter names + part count before commit.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class PricingWipeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Pricing Wipe", "No active document.");
                return Result.Cancelled;
            }
            var doc = uiDoc.Document;

            // ── Collect the mapped target parameter names ──
            // Mapping comes from Pricing Setup (V6+ schema). Empty slots
            // are left unmapped on purpose — we mirror that, only wiping
            // parameters the user explicitly mapped. If nothing is mapped
            // there's nothing to wipe; tell the user and bail.
            var info = PricingSourceSchema.Read(doc);
            var paramNames = new List<string>();
            if (!string.IsNullOrWhiteSpace(info.MaterialRateParamName))
                paramNames.Add(info.MaterialRateParamName);
            if (!string.IsNullOrWhiteSpace(info.InstallRateParamName))
                paramNames.Add(info.InstallRateParamName);
            if (!string.IsNullOrWhiteSpace(info.FabricationRateParamName))
                paramNames.Add(info.FabricationRateParamName);

            if (paramNames.Count == 0)
            {
                TaskDialog.Show("Pricing Wipe",
                    "No target parameters are mapped in Pricing Setup. " +
                    "Open Pricing Setup → Revit parameter mapping to assign " +
                    "the M / E / F-Rate target parameters first.");
                return Result.Cancelled;
            }

            // ── Count parts ──
            var parts = new FilteredElementCollector(doc)
                .OfClass(typeof(FabricationPart))
                .Cast<FabricationPart>()
                .ToList();
            if (parts.Count == 0)
            {
                TaskDialog.Show("Pricing Wipe",
                    "No FabricationParts in this project. Nothing to wipe.");
                return Result.Cancelled;
            }

            // ── Confirmation gate ──
            // Destructive operation, so require an explicit Yes. Default
            // button is No so a stray Enter press doesn't blow away cost
            // data. Lists the exact parameter names + part count so the
            // user knows exactly what's about to happen.
            var confirm = new TaskDialog("Pricing Wipe")
            {
                MainInstruction = $"Reset cost values on {parts.Count:N0} " +
                                  "FabricationPart(s)?",
                MainContent =
                    "This will write 0 (or \"\" for Text-typed params) to " +
                    "the following mapped project parameters on every " +
                    "FabricationPart in the model:\n\n" +
                    string.Join("\n", paramNames.Select(n => "  • " + n)) +
                    "\n\nUse this before sharing the model externally if " +
                    "you don't want pricing data to travel with the file." +
                    "\n\nRe-run Pricing Sync at any time to restore the values.",
                CommonButtons = TaskDialogCommonButtons.Yes |
                                TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
            };
            if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;

            // ── Wipe ──
            int wiped       = 0;
            int readOnly    = 0;
            int notOnPart   = 0;
            int unsupported = 0;
            try
            {
                using var tx = new Transaction(doc, "Pricing Wipe");
                tx.Start();

                foreach (var part in parts)
                {
                    foreach (var name in paramNames)
                    {
                        var p = part.LookupParameter(name);
                        if (p == null) { notOnPart++; continue; }
                        if (p.IsReadOnly) { readOnly++; continue; }

                        bool ok = p.StorageType switch
                        {
                            StorageType.Double  => p.Set(0.0),
                            StorageType.Integer => p.Set(0),
                            StorageType.String  => p.Set(""),
                            _                    => false,
                        };
                        if (ok)               wiped++;
                        else if (!ok)         unsupported++;
                    }
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Pricing Wipe", $"Wipe failed:\n{ex.Message}");
                return Result.Failed;
            }

            // ── Result summary ──
            var sb = new StringBuilder();
            sb.AppendLine($"Wiped {wiped:N0} parameter value(s) across " +
                          $"{parts.Count:N0} FabricationPart(s).");
            sb.AppendLine();
            sb.AppendLine("Parameters reset:");
            foreach (var n in paramNames) sb.Append("  • ").AppendLine(n);
            if (readOnly > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"⚠ Skipped {readOnly:N0} read-only parameter(s).");
            }
            if (notOnPart > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"⚠ {notOnPart:N0} mapped parameter(s) weren't " +
                              "bound to one or more parts (no values to wipe).");
            }
            if (unsupported > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"⚠ Skipped {unsupported:N0} parameter(s) with " +
                              "unsupported storage type.");
            }
            sb.AppendLine();
            sb.AppendLine("Run Pricing Sync to restore the values.");

            TaskDialog.Show("Pricing Wipe", sb.ToString());
            return Result.Succeeded;
        }
    }
}
