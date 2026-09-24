# Estimating Tools for Revit — Claude Code orientation

A standalone Revit add-in (builds for Revit 2025, 2026 and 2027 from one
source tree via `-p:RevitVersion=...`) that prices Autodesk MEP Fabrication
parts from the Fabrication database (or CSV price lists), writes M / E / F
rates onto the parts, breaks down per-part cost, generates material / labor /
combined / zone estimates (optionally through a user Excel template), and
manages estimating zones — Areas split into stacked vertical bands.

## Project layout

```
src/
├── EstimatingTools.csproj             ← net8.0-windows (2025/2026) or net10.0-windows (2027), Revit refs, AfterBuild deploy
├── EstimatingTools.addin              ← Revit add-in manifest (VendorId ESTTL)
├── EstimatingToolsApp.cs              ← IExternalApplication: ribbon tab + panel + PickHandler/PickEvent
├── Pricing*Command.cs                 ← Pricing Setup / Sync / Wipe
├── CostBreakdownCommand.cs            ← per-part breakdown (modeless window)
├── *EstimateCommand.cs, EstimateByZoneCommand.cs  ← Generate Estimate pulldown
├── SyncZonesCommand.cs, ZoneVolumeCommands.cs, ZoneBoundaryCommand.cs, PlaceZoneCommand.cs
├── Models/PricingSourceInfo.cs        ← Pricing Setup payload
├── Revit/                             ← Fab file readers, pricing sources, XLSX writers + template filler, ES schema
│   └── Zones/                         ← zone scheme/params/bands, classifier, rollup, 3D volumes
└── UI/                                ← WPF dialogs + shared estimate card (EstimateCards / EstimateCardView)

docs/                                  ← README-linked user + developer documentation
releases/                              ← per-version release zips (git-ignored, published via GH Releases)
```

See `docs/architecture.md` for the file-by-file tour.

## Build and deploy

```
cd src
dotnet build -c Debug                       # default = Revit 2026
dotnet build -c Debug -p:RevitVersion=2025  # Revit 2025
dotnet build -c Debug -p:RevitVersion=2027  # Revit 2027 (.NET 10 SDK required)
```

Debug builds auto-deploy `EstimatingTools.dll` + `EstimatingTools.addin` to
`%APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\`. Output goes into
`bin/Debug-Revit<RevitVersion>/`. A `REVIT<version>` compile-time symbol is
defined for source that needs to branch on the API surface.

If Revit is open, the DLL is locked — close Revit and rebuild.

`dotnet build` needs the `-p:` (single dash) syntax for property overrides;
`/p:` is treated as a project-file argument.

## Rules worth knowing before changing things

- **Fabrication binary formats are reverse-engineered** (`supplier.map`,
  `cost.map`, `etimes.map` / `ftimes.map`, `Material.MAP`, `.ITM`). The
  readers and pricing rules were validated against Fabrication ESTmep's Cost
  Breakdown. Re-check against ESTmep before changing them.
- **Pricing chain order**: Fab Database first, then CSV files in list order;
  first non-null value per rate wins. Duct material comes from `Material.MAP`
  before `supplier.map`.
- **Loose ancillaries** (UsageType=Loose, e.g. duct sealant) are excluded
  unless Pricing Setup's toggle is on — matches ESTmep for pipe.
- **Zones: one zone per part, never MULTIPLE.** The zone holding the majority
  of the part's centerline wins; fittings vote by connector points + centroid;
  hangers use their host pipe's centerline; mostly-outside parts stay
  UNASSIGNED so Diagnose Zones finds them. Zone totals must never double-count.
- **Band storage**: band 1 in `Zone Bottom / Top Elevation`, the rest in the
  `Zone Extra Bands` text param (`Name:bottom-top;…`). Revit can't blank those
  Length params (no HideWhenNoValue), so an Area with bands keeps at least one.
- **Excel templates**: a placeholder must be the whole cell value; a table
  anchor row deletes everything below it; the sheet named `Instructions` is
  never filled.

## Critical Revit API gotchas

- **Modeless windows must not touch the document directly.** Route through
  `EstimatingToolsApp.PickHandler` / `PickEvent` (see CostBreakdownDialog).
- **`WPF Topmost=True` hides `MessageBox.Show`** — pass the window as owner
  (`MessageBox.Show(this, ...)`).
- **`ConnectorManager.Connectors` enumeration is unstable** — snapshot to a
  list before iterating more than once.
- **`FabricationPart.Location` varies by part** — straights have a
  `LocationCurve`; many fittings, valves and hangers have neither a curve nor
  a point, so use connector origins.
- **Zone Boundary / Place Zone use `PostCommand`** with Revit's internal
  command IDs (`ID_OBJECTS_AREASCHEME_BOUNDARY`); they can change between
  Revit releases.

## ExtensibleStorage schema

- Pricing Setup: GUID `3e7c536a-a5d8-457b-bde8-48df8c8f4dd2`, schema name
  `EstimatingToolsPricingSetup`, VendorId `ESTTL` (must match the .addin).

Don't reuse this GUID elsewhere, and never change a shipped schema in place —
add fields under a new GUID and fall back to reading the old one.
