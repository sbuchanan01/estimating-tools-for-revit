using System;
using System.Reflection;
using Autodesk.Revit.UI;
using EstimatingTools.Revit;

namespace EstimatingTools
{
    /// <summary>
    /// Revit IExternalApplication entry point. Creates an "Estimating Tools"
    /// ribbon tab with one "Estimating" panel: the pricing + estimate tools
    /// on the left, a separator, then the zone tools. Registers the
    /// ExternalEvent the modeless Cost Breakdown dialog uses to pick parts
    /// back on the Revit API thread.
    /// </summary>
    public class EstimatingToolsApp : IExternalApplication
    {
        /// <summary>Used by CostBreakdownDialog's "Pick Parts in Revit".</summary>
        public static RevitEventHandler? PickHandler { get; private set; }
        public static ExternalEvent?     PickEvent   { get; private set; }

        private const string RibbonTabName   = "Estimating Tools";
        private const string RibbonPanelName = "Estimating";

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                InstallTooltipWrapStyle();

                PickHandler = new RevitEventHandler();
                PickEvent   = ExternalEvent.Create(PickHandler);

                try { app.CreateRibbonTab(RibbonTabName); }
                catch (Exception) { /* tab already exists */ }

                var panel = app.CreateRibbonPanel(RibbonTabName, RibbonPanelName);
                string asm = Assembly.GetExecutingAssembly().Location;

                // ── Pricing + estimates ────────────────────────────────────
                panel.AddItem(new PushButtonData("PricingSetup", "Pricing\nSetup", asm,
                    "EstimatingTools.PricingSourceCommand")
                {
                    ToolTip = "Configure pricing sources, labor rates, and Revit parameter mapping.",
                    LongDescription = "Pick where pricing comes from (Fab Database or CSV), set " +
                                      "per-side labor rates, map M/E/F rates to Revit project " +
                                      "parameters, and choose an Excel estimate template. Saved " +
                                      "with the project.",
                    LargeImage = RibbonIconFactory.Gear(32),
                    Image      = RibbonIconFactory.Gear(16),
                });
                panel.AddItem(new PushButtonData("PricingSync", "Pricing\nSync", asm,
                    "EstimatingTools.PricingSyncCommand")
                {
                    ToolTip = "Refresh fabrication-part costing from the configured source.",
                    LongDescription = "Walks every Fabrication part in the project, looks up " +
                                      "Material / Installation / Fabrication rates from the " +
                                      "configured source, and writes them to the parameters " +
                                      "mapped in Pricing Setup.",
                    LargeImage = RibbonIconFactory.SyncArrows(32),
                    Image      = RibbonIconFactory.SyncArrows(16),
                });
                panel.AddItem(new PushButtonData("PricingWipe", "Pricing\nWipe", asm,
                    "EstimatingTools.PricingWipeCommand")
                {
                    ToolTip = "Reset mapped cost parameter values to $0 on every Fabrication part.",
                    LongDescription = "Writes 0 to the M / E / F-Rate parameters mapped in Pricing " +
                                      "Setup. Use it to strip cost data before sharing the model " +
                                      "externally; run Pricing Sync any time to restore the values. " +
                                      "A confirmation dialog gates the wipe.",
                    LargeImage = RibbonIconFactory.Eraser(32),
                    Image      = RibbonIconFactory.Eraser(16),
                });
                panel.AddItem(new PushButtonData("CostBreakdown", "Cost\nBreakdown", asm,
                    "EstimatingTools.CostBreakdownCommand")
                {
                    ToolTip = "Per-part cost forensics matching Fabrication's Cost Breakdown view.",
                    LongDescription = "Pick one or more Fabrication parts (or use the current " +
                                      "selection) to see material price and ancillary kits, " +
                                      "installation labor per table, and fabrication labor per " +
                                      "table, with a total for everything picked.",
                    LargeImage = RibbonIconFactory.DollarSign(32),
                    Image      = RibbonIconFactory.DollarSign(16),
                });

                var estimate = panel.AddItem(new PulldownButtonData("GenerateEstimate", "Generate\nEstimate")
                {
                    ToolTip = "Generate a cost estimate report (material, labor, both, or by zone).",
                    LongDescription = "Material rolls every Fabrication part up by Product Code; " +
                                      "Labor rolls installation and fabrication time up by labor " +
                                      "table; Material + Labor combines both; Zone Estimate buckets " +
                                      "material and labor per Estimate Zone.",
                    LargeImage = RibbonIconFactory.ReportDocument(32),
                    Image      = RibbonIconFactory.ReportDocument(16),
                }) as PulldownButton;
                estimate?.AddPushButton(new PushButtonData("MaterialEstimate", "Material Estimate", asm,
                    "EstimatingTools.MaterialEstimateCommand")
                {
                    ToolTip = "Material-only BOM rolled up by Product Code.",
                });
                estimate?.AddPushButton(new PushButtonData("LaborEstimate", "Labor Estimate", asm,
                    "EstimatingTools.LaborEstimateCommand")
                {
                    ToolTip = "Labor-only estimate rolled up by labor table " +
                              "(installation × etimes + fabrication × ftimes).",
                });
                estimate?.AddPushButton(new PushButtonData("CombinedEstimate", "Material + Labor Estimate", asm,
                    "EstimatingTools.CombinedEstimateCommand")
                {
                    ToolTip = "Combined estimate — material BOM + labor breakdown + grand total.",
                });
                estimate?.AddPushButton(new PushButtonData("ZoneEstimate", "Zone Estimate", asm,
                    "EstimatingTools.EstimateByZoneCommand")
                {
                    ToolTip = "Material + labor estimate bucketed per Estimate Zone, with an " +
                              "Exclude Unassigned toggle and XLSX export.",
                });

                panel.AddSeparator();

                // ── Zones ──────────────────────────────────────────────────
                var zones = panel.AddItem(new PulldownButtonData("EstimateZones", "Estimate\nZones")
                {
                    ToolTip = "Set up and maintain estimating zones — scheme setup, vertical " +
                              "bands, tag sync, diagnostics and 3D volumes. The zone report " +
                              "lives under Generate Estimate.",
                    LongDescription = "Sketch estimating zones as Areas under the 'Estimating " +
                                      "Zones' Area Scheme, run Sync Zones to tag each Fabrication " +
                                      "part with its zone, then use Zone Estimate under Generate " +
                                      "Estimate.",
                    LargeImage = RibbonIconFactory.MagnifyingGlass(32),
                    Image      = RibbonIconFactory.MagnifyingGlass(16),
                }) as PulldownButton;
                zones?.AddPushButton(new PushButtonData("EstimateZonesSetup", "Setup", asm,
                    "EstimatingTools.UI.EstimateZonesSetupCommand")
                {
                    ToolTip = "One-time-per-project setup for the Estimating Zones scheme, " +
                              "parameter binding, and Area Plans.",
                });
                zones?.AddPushButton(new PushButtonData("SyncZones", "Sync Zones", asm,
                    "EstimatingTools.SyncZonesCommand")
                {
                    ToolTip = "Classify every Fabrication part against the zones and write the " +
                              "zone name into the Estimate Zone parameter.",
                });
                zones?.AddPushButton(new PushButtonData("DiagnoseZones", "Diagnose Zones", asm,
                    "EstimatingTools.UI.DiagnoseZonesCommand")
                {
                    ToolTip = "List parts whose zone came out UNASSIGNED, with Show-in-view per row.",
                });
                zones?.AddPushButton(new PushButtonData("ManageZoneBands", "Manage Bands", asm,
                    "EstimatingTools.UI.ManageZoneBandsCommand")
                {
                    ToolTip = "Split one zone Area into stacked vertical bands (e.g. A-1 = 0' to " +
                              "10', A-2 = 10' to 14'). Select an Area first, or pick one when prompted.",
                    LongDescription = "Band names default to the Area name plus a number and can " +
                                      "be overridden. Each band reports as its own zone in Sync " +
                                      "Zones, Diagnose and the zone estimate.",
                });
                zones?.AddPushButton(new PushButtonData("ShowZoneVolumes", "Show Volumes", asm,
                    "EstimatingTools.ShowZoneVolumesCommand")
                {
                    ToolTip = "Draw each zone band as a see-through box so you can check the " +
                              "zones in a 3D view.",
                    LongDescription = "Zones without bands span their Level to the next Level up " +
                                      "(or 10 ft when there's no higher Level). Running it again " +
                                      "clears the previous boxes first, so it's safe to rerun " +
                                      "after changing a boundary.",
                });
                zones?.AddPushButton(new PushButtonData("HideZoneVolumes", "Hide Volumes", asm,
                    "EstimatingTools.HideZoneVolumesCommand")
                {
                    ToolTip = "Remove the boxes Show Volumes created. Anything else in the model " +
                              "is left alone.",
                });

                panel.AddItem(new PushButtonData("ZoneBoundary", "Zone\nBoundary", asm,
                    "EstimatingTools.ZoneBoundaryCommand")
                {
                    ToolTip = "Sketch a zone boundary in the active Estimating Zones Area Plan.",
                    LongDescription = "Opens Revit's Area Boundary tool from this tab, so you " +
                                      "don't need the Architecture tab. Works only in an Area Plan " +
                                      "under the Estimating Zones scheme.",
                    LargeImage = RibbonIconFactory.Wrench(32),
                    Image      = RibbonIconFactory.Wrench(16),
                });
                panel.AddItem(new PushButtonData("PlaceZone", "Place\nZone", asm,
                    "EstimatingTools.PlaceZoneCommand")
                {
                    ToolTip = "Place a zone Area inside a closed boundary in the active " +
                              "Estimating Zones Area Plan.",
                    LongDescription = "Opens Revit's Area tool. Click inside each closed boundary " +
                                      "to place an Area, then rename it in the Properties palette " +
                                      "to your zone name.",
                    LargeImage = RibbonIconFactory.Tag(32),
                    Image      = RibbonIconFactory.Tag(16),
                });

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Estimating Tools — startup error", ex.ToString());
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

        /// <summary>Plain-string tooltips render on one line by default and
        /// run across the screen; give them a wrapping TextBlock instead.</summary>
        private static void InstallTooltipWrapStyle()
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    // Revit hosts WPF without an Application object; create
                    // one to hold global resources. OnExplicitShutdown keeps
                    // it from exiting when a dialog closes.
                    _ = new System.Windows.Application
                    {
                        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                    };
                }
                var wpfApp = System.Windows.Application.Current;
                if (wpfApp == null) return;

                var style = new System.Windows.Style(typeof(System.Windows.Controls.ToolTip));
                style.Setters.Add(new System.Windows.Setter(
                    System.Windows.FrameworkElement.MaxWidthProperty, 400.0));
                var wrap = new System.Windows.DataTemplate();
                var tb = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
                tb.SetValue(System.Windows.Controls.TextBlock.TextWrappingProperty,
                            System.Windows.TextWrapping.Wrap);
                tb.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                              new System.Windows.Data.Binding());
                wrap.VisualTree = tb;
                style.Setters.Add(new System.Windows.Setter(
                    System.Windows.Controls.ContentControl.ContentTemplateProperty, wrap));
                wpfApp.Resources[typeof(System.Windows.Controls.ToolTip)] = style;
            }
            catch
            {
                // Non-fatal — long tooltips beat a crashed ribbon.
            }
        }
    }
}
