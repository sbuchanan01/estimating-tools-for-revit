# Developer guide

How to set up your development environment, build, debug, and ship changes.

---

## Prerequisites

- **Windows 10/11** — Revit only runs on Windows.
- **Revit 2025, 2026 or 2027** — full install. The add-in references DLLs from your Revit install folder (default `C:\Program Files\Autodesk\Revit <version>\`). Build defaults to Revit 2026; pass `-p:RevitVersion=2025` or `-p:RevitVersion=2027` to target another version.
- **.NET 8 SDK** for Revit 2025 / 2026 and **.NET 10 SDK** for Revit 2027 — [download](https://dotnet.microsoft.com/download). The csproj picks `net8.0-windows` or `net10.0-windows` from `RevitVersion`.
- **A C# IDE** (any will do):
  - Visual Studio 2022 / 2026 Community — open `src/EstimatingTools.csproj`.
  - JetBrains Rider — open the same file.
  - VS Code + C# Dev Kit — same.
  - Claude Code — open the repo root; the included `CLAUDE.md` orients it.

---

## Clone and build

```powershell
git clone https://github.com/sbuchanan01/estimating-tools-for-revit.git
cd estimating-tools-for-revit/src
dotnet build -c Debug
```

Successful output ends with:

```
Build succeeded.
    0 Error(s)
Time Elapsed 00:00:0X.XX
```

(Some `MSB3277` warnings about RevitAPIUI re-references are normal and harmless.)

Debug builds auto-deploy to `%APPDATA%\Autodesk\Revit\Addins\<version>\` via the `DeployToRevitAddins` MSBuild target — the `<version>` matches whatever `-p:RevitVersion=...` you passed (default 2026). **If Revit is open, the DLL is locked** — the copy step is skipped (the build itself still succeeds).

If your Revit is installed somewhere non-default:

```powershell
dotnet build -c Debug -p:RevitInstallPath="D:\Revit 2026"
```

To build every supported version in one go (typical for releases):

```powershell
dotnet build -c Release -p:RevitVersion=2025
dotnet build -c Release -p:RevitVersion=2026
dotnet build -c Release -p:RevitVersion=2027
```

Outputs land in `bin/Release-Revit2025/`, `bin/Release-Revit2026/` and `bin/Release-Revit2027/`.

> **Property syntax.** Use `-p:Name=Value` (single dash). The `/p:Name=Value` form (forward slash) is treated as a project-file argument by `dotnet build` and doesn't propagate to MSBuild.

---

## Debug-and-iterate cycle

The typical inner loop:

1. **Close Revit.** This releases the DLL lock so the post-build copy can land.
2. Make code changes in your IDE.
3. `dotnet build -c Debug` (or hit Build in your IDE).
4. **Open Revit**, open a model that contains Fabrication parts.
5. Run the command you changed.
6. Loop.

If you want to **attach a debugger**, the standard Revit add-in debug workflow is:

1. Open the csproj in Visual Studio.
2. Open **Properties** on the project → Debug → "Launch Profile" → set the executable to your Revit binary (`C:\Program Files\Autodesk\Revit <version>\Revit.exe`).
3. Hit F5 — Visual Studio starts Revit with the debugger attached.
4. Set breakpoints in source; they hit when the relevant code path runs in Revit.

For Rider, it's the same idea via Run/Debug configurations → .NET Executable.

---

## Code layout

See [architecture.md](architecture.md) for a full file-by-file tour. The short version:

- **`src/EstimatingToolsApp.cs`** — Revit's entry point. Builds the ribbon (pricing + estimate tools, separator, zone tools) and registers the ExternalEvent the modeless Cost Breakdown window uses.
- **`src/*Command.cs`** — one `IExternalCommand` per button.
- **`src/Revit/`** — Fabrication data readers (`supplier.map`, `cost.map`, `etimes` / `ftimes`, ITM, `Material.MAP`), pricing sources, Excel writers and the template filler, and the zone engine under `Revit/Zones/`.
- **`src/UI/`** — WPF dialogs and the shared estimate card.

---

## Making changes safely

### Every command is transaction-scoped

The estimate and breakdown commands only read the model. Pricing Sync / Wipe, Sync Zones, the zone setup and Manage Bands write inside their own `Transaction`. Don't touch the document from a WPF event handler of a **modeless** window — Revit's API is single-threaded. The Cost Breakdown window routes its "Pick Parts in Revit" through `EstimatingToolsApp.PickHandler` / `PickEvent`:

```csharp
EstimatingToolsApp.PickHandler!.SetAction(uiApp =>
{
    // runs on Revit's API thread
});
EstimatingToolsApp.PickEvent!.Raise();
```

Modal dialogs (Pricing Setup, Manage Bands, the estimate results) run inside the command's API context and can use a `Transaction` directly.

### Fabrication binary files are reverse-engineered

`supplier.map`, `cost.map`, `etimes.map`, `ftimes.map`, `Material.MAP` and `.ITM` are undocumented formats. The readers in `src/Revit/` were validated against Fabrication's own Cost Breakdown view. Re-check against ESTmep before changing parsing or pricing rules.

### Extensible Storage schemas

Pricing Setup is stored with a schema GUID owned by this add-in (see `PricingSourceSchema.cs`). Never reuse another tool's GUID, and never change a shipped schema in place — add fields under a new GUID and read the old one as a fallback.

### Zone classification rules are deliberate

Each part gets exactly one zone (centerline majority, never "MULTIPLE"), and mostly-outside parts stay UNASSIGNED so gaps show up in Diagnose Zones. Keep that contract if you change `ZoneAssignment.cs` — zone totals must never double-count.

---

## Ship a new release

For binary distribution:

### 1. Bump the version

Update `<Version>` in `src/EstimatingTools.csproj`.

### 2. Release build (every Revit version)

```powershell
cd src
dotnet build -c Release -p:RevitVersion=2025
dotnet build -c Release -p:RevitVersion=2026
dotnet build -c Release -p:RevitVersion=2027
```

### 3. Stage + package

For each Revit version, make a ZIP containing:
- `EstimatingTools.dll` (from `src/bin/Release-Revit<version>/`)
- `EstimatingTools.addin` (from `src/`)
- `LICENSE` (from repo root)
- `README.md` (from repo root)

Name them `EstimatingTools-Revit<version>-v{version}.zip`.

### 4. Tag and push

```powershell
git tag v{version}
git push origin v{version}
```

### 5. Create the GitHub Release

```powershell
gh release create v{version} `
  releases/EstimatingTools-Revit2025-v{version}.zip `
  releases/EstimatingTools-Revit2026-v{version}.zip `
  releases/EstimatingTools-Revit2027-v{version}.zip `
  --title "v{version}" `
  --notes "Release notes..."
```

---

## Code style

- **No comments unless the WHY is non-obvious.** Identifier names should carry the WHAT.
- **`/// <summary>` XML docs** on public types and members that aren't self-explanatory.

---

## Reporting issues

[File an issue](https://github.com/sbuchanan01/estimating-tools-for-revit/issues) with:
- Revit version + build number (Help → About Revit)
- What you did, what you expected, what happened
- Stack trace if there was a TaskDialog
- A minimal sample model if the bug is data-dependent
- The generated CSV / XLSX if the issue is about estimate output
