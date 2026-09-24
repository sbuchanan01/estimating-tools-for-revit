# Architecture

A code tour for people modifying the add-in. For build and debug steps see [developer-guide.md](developer-guide.md).

---

## High-level shape

```
Ribbon button ──► *Command.cs (IExternalCommand)
                     │
                     ├─ PricingSourceSchema.Read(doc) ──► PricingSourceInfo  (Pricing Setup, saved on Project Information)
                     │
                     ├─ Pricing sources (Revit/)
                     │    supplier.map ─ SupplierMapReader / SupplierMapPricingSource
                     │    Material.MAP ─ MaterialMapReader + DuctMaterialResolver
                     │    etimes/ftimes ─ EtimesBreakpointParser + AncillaryLabourBreakdown
                     │    .ITM files   ─ ItmFileIndex / ItmFileParser
                     │    cost.map     ─ CostMapReader (labor types for Pricing Setup)
                     │    CSV          ─ CsvPricingSource
                     │    ChainedPricingSource: Fab DB first, then CSVs in order
                     │
                     ├─ Writes: Pricing Sync / Wipe → mapped M/E/F parameters
                     │          Sync Zones        → "Estimate Zone" parameter
                     │
                     └─ Output: result dialog (EstimateCards) + CSV / XLSX
                               XlsxTemplateFiller (user template) · MinimalXlsxWriter · XlsxStyledWriter
```

The zone tools add a second path: Areas in the **Estimating Zones** scheme → `ZoneAssignment` (which zone is each part in?) → `Estimate Zone` parameter → `ZoneRollupBuilder` (price per zone).

---

## File-by-file

### `src/EstimatingToolsApp.cs`

`IExternalApplication`. Creates the **Estimating Tools** tab and one **Estimating** panel: Pricing Setup, Pricing Sync, Pricing Wipe, Cost Breakdown, the Generate Estimate pulldown, a separator, the Estimate Zones pulldown, Zone Boundary and Place Zone. Registers `PickHandler` / `PickEvent` for the modeless Cost Breakdown window, and installs a WPF style so long tooltips wrap.

### Pricing commands

- **`PricingSourceCommand.cs`** — opens `UI/PricingSourceDialog`.
- **`PricingSyncCommand.cs`** — builds the pricing chain, validates the mapped parameters (hard-fails before writing if any is missing or the wrong type), writes M / E / F on every `FabricationPart`, prices native pipe-accessory families with a Product Code (M-Rate only), then refreshes optional "Missing Cost" markers in a separate transaction and shows a summary with a "select the unpriced parts" link.
- **`PricingWipeCommand.cs`** — confirms, then writes 0 / "" to the mapped parameters on every `FabricationPart`.
- **`CostBreakdownCommand.cs`** — builds a `CostBreakdownContext` (readers + rates) and opens the modeless `UI/CostBreakdownDialog` seeded from the selection. `ComputeBreakdown` / `ComputeBreakdownForRfa` produce a `PartBreakdown` that both the window and the **Save Report** TXT render, so the two always agree.

### Estimate commands

- **`MaterialEstimateCommand.cs`** — `BuildLines` walks every `FabricationPart` (plus native pipe accessories), prices each by duct material → supplier.map → CSV, adds each ancillary as its own line, and rolls up by (Product Code, kind). Writes a CSV, or fills the template. Other commands reuse `BuildLines`.
- **`LaborEstimateCommand.cs`** — `BuildLines` totals installation / fabrication minutes per labor table and prices them at the Pricing Setup rates. CSV or template.
- **`CombinedEstimateCommand.cs`** — both, with a grand total. Three-sheet XLSX (`MinimalXlsxWriter`) or template.
- **`EstimateByZoneCommand.cs`** — `ZoneRollupBuilder.Build`, then `UI/ZoneEstimateResultDialog`.

### Zone commands

- **`SyncZonesCommand.cs`** — `ZoneAssignment.Load` + `Classify` for every Fabrication pipe, duct and hanger; writes the zone name into **Estimate Zone**.
- **`ZoneVolumeCommands.cs`** — Show / Hide Volumes (wrap `ZoneVolumeVisualizer`).
- **`ZoneBoundaryCommand.cs`**, **`PlaceZoneCommand.cs`** — check the active view is an Estimating Zones Area Plan, then `PostCommand` Revit's own Area Boundary / Area tool.
- The Setup, Diagnose Zones and Manage Bands commands live next to their dialogs in `UI/`.

### `src/Models/PricingSourceInfo.cs`

The Pricing Setup payload: Database folder, CSV list, labor type names and $/hr, Loose-ancillary toggle, mapped parameter names, labor-as-hours toggle, template path.

### `src/Revit/` — Fabrication data and pricing

- **`MapFileHelper.cs`** — decodes the zlib "MAP Compressed File 2005" envelope Fabrication uses.
- **`SupplierMapReader.cs`** — Product Code → list price from `supplier.map`.
- **`PricingReader.cs`** — the `IPricingSource` abstraction, `SupplierMapPricingSource` (part-aware: base price, ITM cost, ancillary roll-up), `CsvPricingSource` and `ChainedPricingSource`.
- **`MaterialMapReader.cs`** + **`DuctMaterialPricingSource.cs`** — sheet-metal duct pricing from `Material.MAP` (weight × $/lb by material and gauge).
- **`EtimesBreakpointParser.cs`** — `etimes.map` / `ftimes.map` breakpoint tables (minutes by size).
- **`AncillaryLabourBreakdown.cs`** — labor minutes per part per table, the single source of truth for Pricing Sync, the estimates and Cost Breakdown.
- **`LabourPricingSources.cs`** — wraps the labor minutes as an `IPricingSource` for the E / F rates.
- **`CostMapReader.cs`** — labor types and rates from `cost.map`, for Pricing Setup's dropdowns.
- **`ItmFileIndex.cs`** / **`ItmFileParser.cs`** — index and parse `.ITM` files (labor-table brackets, ancillary kits, costs).
- **`PricingSourceSchema.cs`** — Extensible Storage for Pricing Setup (schema GUID owned by this add-in).

### `src/Revit/` — output

- **`XlsxTemplateFiller.cs`** — fills a user `.xlsx`: `{{Name}}` scalars, `{{Table.Field}}` row anchors, never touches a sheet named **Instructions**.
- **`EstimateTemplateBuilder.cs`** — builds the fill request (placeholder names are defined here) and blanks known values a given estimate type doesn't produce.
- **`SampleTemplateGenerator.cs`** — writes the styled four-sheet sample template.
- **`MinimalXlsxWriter.cs`** — plain multi-sheet XLSX (Material + Labor estimate).
- **`XlsxStyledWriter.cs`** — single-sheet styled XLSX (Zone Estimate).

All three writers are dependency-free (`ZipArchive` + XML); nothing ships alongside the DLL.

### `src/Revit/Zones/`

- **`EstimateZoneScheme.cs`** — scheme lookup, Area Plans per level, parameter binding (via a private shared-parameter file under `%APPDATA%\EstimatingTools\`), and band storage: band 1 in **Zone Bottom / Top Elevation**, the rest in the **Zone Extra Bands** text parameter (`Name:bottom-top;…`). Unnamed bands resolve to `{Area}-1`, `{Area}-2`.
- **`ZoneAssignment.cs`** — the classifier. One `ZoneVolume` per band (or per level-based Area). Each part is sampled along its centerline — curve samples every 6" for straights, connector points plus centroid for fittings, the host centerline point for hangers — and the zone with the most samples wins. Overlaps go to the most specific zone; ties go to the zone at the part's middle. Never returns MULTIPLE.
- **`ZoneRollupBuilder.cs`** — prices parts per zone (material + labor), with UNASSIGNED as its own bucket.
- **`ZoneVolumeVisualizer.cs`** — DirectShape prisms per band, tagged through Comments so Hide removes only its own.

### `src/Revit/` — plumbing

- **`RevitEventHandler.cs`** — generic `IExternalEventHandler` for modeless windows.
- **`RibbonIconFactory.cs`** — the eight ribbon icons, rendered at startup from Segoe MDL2 glyphs.

### `src/UI/`

- **`PricingSourceDialog`**, **`EstimateTemplateHelpDialog`** — Pricing Setup.
- **`CostBreakdownDialog`** — modeless tree; `CostNode` rows styled by `CostRowKind`.
- **`EstimateResultDialog`** — Material / Labor / Material + Labor results (card mode); **`ZoneEstimateResultDialog`** — zone results with Exclude Unassigned.
- **`EstimateCards`** + **`EstimateCardView`** — the shared card model, palette and view used by every results window.
- **`EstimateZonesSetupDialog`**, **`DiagnoseZonesDialog`**, **`ManageZoneBandsDialog`** — zone dialogs (each file also holds its command class).

---

## Data flow — Pricing Sync

```
PricingSourceSchema.Read
  → build chain: SupplierMapPricingSource (+ etimes / ftimes labor sources) → CsvPricingSource…
  → validate mapped parameters on a sample part (fail before any write)
  → Transaction: for each FabricationPart
        rates = chain.Lookup(part)          (first non-null per M / E / F)
        write rounded values (E / F as hours if enabled)
      then native pipe accessories (M-Rate)
  → separate Transaction: refresh "Missing Cost" markers
  → summary TaskDialog (+ select unpriced parts)
```

## Data flow — Zone Estimate

```
Sync Zones:   ZoneAssignment.Load(scheme) → Classify(part) → Estimate Zone parameter
Estimate:     ZoneRollupBuilder.Build → per-zone material lines + labor
              → ZoneEstimateResultDialog (cards, Exclude Unassigned) → XlsxStyledWriter
```

---

## Adding a new tool to this add-in

1. Add an `IExternalCommand` class under `src/`.
2. Add a `PushButtonData` for it in `EstimatingToolsApp.OnStartup` (and an icon in `RibbonIconFactory` if it's a top-level button).
3. If it's a modeless window that edits the model, route writes through a `RevitEventHandler` + `ExternalEvent` pair like `PickHandler`.
4. If it needs per-project settings, add them to `PricingSourceInfo` and a **new** schema GUID (keep reading the old one), or give it its own schema.
