# Estimating Tools for Revit

A Revit add-in (builds for **Revit 2025**, **Revit 2026** and **Revit 2027**)
that prices **Autodesk MEP Fabrication parts** straight from your Fabrication
database and turns them into estimates. It reads material and labor rates from
the Fabrication database files (or your own CSV price lists), writes them onto
the parts, breaks down any part's cost the way Fabrication ESTmep does, and
generates material, labor and zone-by-zone estimates — optionally through your
own branded Excel template.

The zone tools let you sketch estimating zones as Revit Areas, split a zone
into stacked vertical bands (for example below and above a ceiling), tag every
Fabrication part with the one zone it belongs to, and price each zone
separately.

---

## ⚠ Disclaimer

This code is provided by Autodesk for evaluation purposes only, as an example
of what is possible with the Autodesk platform and APIs. **THIS CODE IS NOT
INTENDED FOR USE IN PRODUCTION.** Autodesk makes no representations,
warranties, or commitments about the code. This code is not fully tested
and may include errors or faults that may cause total data loss or system
failure. No further updates to this tool are promised or implied — the
version published here may be the last, and may never be revised after the
posting date.

The MIT license applies to the source — see [LICENSE](LICENSE) — but the
evaluation-only nature above takes precedence over any "use however you
like" reading of the MIT terms.

---

## What it does

- **Pricing Setup** — point the add-in at a Fabrication database folder
  and/or CSV price lists, set installation and fabrication labor rates, choose
  which Revit parameters receive the rates, and optionally pick an Excel
  estimate template. Saved with the project.
- **Pricing Sync** — writes Material (M), Installation (E) and Fabrication (F)
  rates onto every Fabrication part, and flags parts with no price.
- **Pricing Wipe** — resets those rate parameters to $0, for sharing a model
  without cost data. Pricing Sync restores them.
- **Cost Breakdown** — per-part cost forensics matching Fabrication's Cost
  Breakdown view: list price, ancillary kits, and labor per table.
- **Generate Estimate** — Material, Labor, Material + Labor, and Zone
  estimates, shown on screen and exported to CSV / XLSX or your own template.
- **Estimate Zones** — set up an Estimating Zones Area Scheme, sketch zones,
  split them into vertical bands, tag every part with its zone, check the
  zones in 3D, and find untagged parts.

---

## Install (no compiling required)

1. **Download the ZIP that matches your Revit version** from
   <https://github.com/sbuchanan01/estimating-tools-for-revit/releases>:
   - `EstimatingTools-Revit2025-v1.0.0.zip` for Revit 2025
   - `EstimatingTools-Revit2026-v1.0.0.zip` for Revit 2026
   - `EstimatingTools-Revit2027-v1.0.0.zip` for Revit 2027
2. Extract `EstimatingTools.dll` and `EstimatingTools.addin`.
3. Drop **both files** into your version-matched Revit add-ins folder:
   - Revit 2025 → `%APPDATA%\Autodesk\Revit\Addins\2025\`
   - Revit 2026 → `%APPDATA%\Autodesk\Revit\Addins\2026\`
   - Revit 2027 → `%APPDATA%\Autodesk\Revit\Addins\2027\`

   (paste the path into File Explorer's address bar — it expands to your user
   folder.)
4. Restart Revit. You'll see a new **Estimating Tools** ribbon tab with one
   **Estimating** panel.

If Revit blocks the DLL on first launch with a security warning, right-click
`EstimatingTools.dll` → **Properties** → tick **Unblock** at the bottom → OK.
That's a one-time Windows quirk for DLLs downloaded from the internet.

Full step-by-step: [docs/installation.md](docs/installation.md).

---

## Quick start

1. Open a Revit model that contains Fabrication parts.
2. On the **Estimating Tools** tab, click **Pricing Setup**. Tick **Use Fab
   Database**, browse to your Fabrication `Database` folder, set the labor
   rates, map the three rate parameters, and click **Save**.
3. Click **Generate Estimate → Material + Labor Estimate** to see the totals,
   then **Open XLSX** for the full breakdown.
4. For zone pricing: **Estimate Zones → Setup**, sketch zones with **Zone
   Boundary** and **Place Zone**, run **Estimate Zones → Sync Zones**, then
   **Generate Estimate → Zone Estimate**.

Full user guide: [docs/user-guide.md](docs/user-guide.md).

---

## Modify the code

The repo is a standard .NET / C# project (.NET 8 for Revit 2025 and 2026,
.NET 10 for Revit 2027). Any compatible toolchain works:

- **Visual Studio 2022 / 2026 Community** (free) — open `src/EstimatingTools.csproj`.
- **JetBrains Rider** — open the same csproj.
- **VS Code + C# Dev Kit** — same.
- **Claude Code** — open the repo root; the included `CLAUDE.md` orients
  it to the project layout.
- **Anything else that speaks `dotnet build`** — `cd src && dotnet build -c Debug`.

Full build / debug / deploy guide: [docs/developer-guide.md](docs/developer-guide.md).

A code-structure tour for people modifying it:
[docs/architecture.md](docs/architecture.md).

---

## License

[MIT](LICENSE) — modify, redistribute, fork freely, just keep the copyright
notice and disclaimer. See the LICENSE file for the full text including the
Autodesk evaluation disclaimer.

---

## Acknowledgements

Built against the **Revit 2025 / 2026 / 2027** and **Autodesk Fabrication MEP**
APIs.
