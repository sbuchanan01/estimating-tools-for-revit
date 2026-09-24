# Installation

Two paths, depending on whether you want to **just use the tool** or **build it from source**.

---

## A. Just use the tool (no compiling)

### 1. Download

Go to the [Releases page](https://github.com/sbuchanan01/estimating-tools-for-revit/releases) and download the ZIP attachment **matching your Revit version**:

- `EstimatingTools-Revit2025-v1.0.0.zip` — for Revit 2025
- `EstimatingTools-Revit2026-v1.0.0.zip` — for Revit 2026
- `EstimatingTools-Revit2027-v1.0.0.zip` — for Revit 2027

### 2. Extract

Unzip anywhere. Inside you'll find:

```
EstimatingTools.dll
EstimatingTools.addin
LICENSE
README.md
```

### 3. Drop into the Revit Add-ins folder

Press <kbd>Win</kbd>+<kbd>R</kbd> to open the Run dialog, paste **the path that matches the ZIP you downloaded**, hit <kbd>Enter</kbd>:

```
%APPDATA%\Autodesk\Revit\Addins\2025
%APPDATA%\Autodesk\Revit\Addins\2026
%APPDATA%\Autodesk\Revit\Addins\2027
```

That opens the per-user add-ins folder for your Revit version.

> If the folder doesn't exist, create it. The structure is `%APPDATA%\Autodesk\Revit\Addins\2026\` (you'll already have an `Addins` folder with other version sub-folders if you've installed add-ins before).

Copy **both** `EstimatingTools.dll` and `EstimatingTools.addin` into that folder.

### 4. Unblock the DLL (Windows quirk)

Windows marks DLLs downloaded from the internet as "blocked" by default — Revit will refuse to load them. Right-click `EstimatingTools.dll` → **Properties** → at the bottom of the General tab, tick **Unblock** → OK.

If you don't see an Unblock checkbox, the file is already cleared — skip.

### 5. Launch Revit

Start Revit. You'll see a new **Estimating Tools** ribbon tab with an **Estimating** panel: the pricing and estimate tools on the left, a separator, then the zone tools.

If the tab doesn't appear, see [Troubleshooting](#troubleshooting) below.

---

## B. Build from source

Prerequisites:

- **Windows 10/11**
- **Revit 2025, 2026 or 2027** (full install — the add-in references DLLs from the install folder)
- **.NET 8 SDK** for Revit 2025 / 2026, **.NET 10 SDK** for Revit 2027 — [download from Microsoft](https://dotnet.microsoft.com/download)
- Git — [download](https://git-scm.com/download/win) or use GitHub Desktop

### 1. Clone the repo

```powershell
git clone https://github.com/sbuchanan01/estimating-tools-for-revit.git
cd estimating-tools-for-revit
```

### 2. Build

Default build targets **Revit 2026**:

```powershell
cd src
dotnet build -c Debug
```

To target another version, pass `-p:RevitVersion=...`:

```powershell
dotnet build -c Debug -p:RevitVersion=2025
dotnet build -c Debug -p:RevitVersion=2027
```

Output goes into a per-version sub-folder (e.g. `bin/Debug-Revit2025/`) so the versions don't overwrite each other. The target framework follows the version automatically (.NET 8 for 2025 / 2026, .NET 10 for 2027).

> **Property syntax.** Use `-p:Name=Value` (single dash) for `dotnet build` MSBuild property overrides. The `/p:Name=Value` form (forward slash) is treated as a project-file argument by `dotnet build` and doesn't propagate.

If Revit installed somewhere other than `C:\Program Files\Autodesk\Revit <version>`, override the path:

```powershell
dotnet build -c Debug -p:RevitInstallPath="D:\Revit 2026"
```

### 3. Auto-deploy

Debug builds automatically copy `EstimatingTools.dll` and `EstimatingTools.addin` to `%APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\` (the folder matching whatever `-p:RevitVersion` you built with — default 2026). **Close Revit first** — if Revit is open when you build, the DLL is locked and the copy step is skipped (the build itself still succeeds; you just need to copy manually).

### 4. Launch Revit

Same as the install path — look for the **Estimating Tools** ribbon tab.

---

## Troubleshooting

### "I installed the files but the ribbon tab doesn't show up"

Check that **both** files are in `%APPDATA%\Autodesk\Revit\Addins\<version>\` (where `<version>` matches the Revit version you're starting):

- `EstimatingTools.addin` (the manifest — without it, Revit doesn't know what to load)
- `EstimatingTools.dll` (the actual add-in)

Open `EstimatingTools.addin` in Notepad and confirm it points at `EstimatingTools.dll` (relative path). If you renamed either file, fix the reference.

### "Revit shows a security warning about the DLL"

Right-click `EstimatingTools.dll` → Properties → **Unblock** → OK. Restart Revit.

### "I get a startup error dialog from the add-in"

The dialog title is "Estimating Tools — startup error". Copy the exception text and either:
- Search [existing issues](https://github.com/sbuchanan01/estimating-tools-for-revit/issues), or
- File a new issue with the exception text + your Revit version.

### "The estimate shows $0 / most parts have no price"

Pricing Setup isn't pointing at the Fabrication database the parts came from, or the parts have no **Product Code**. Open **Pricing Setup**, confirm the Database folder is the one containing `supplier.map`, and check that your parts carry Product Codes. **Pricing Sync** lists (and can select) the parts it couldn't price.

### "Build fails with 'Could not find RevitAPI.dll'"

Your Revit install path differs from the default. Pass `-p:RevitInstallPath`:

```powershell
dotnet build -c Debug -p:RevitInstallPath="D:\My Revit Folder"
```

### "Build for Revit 2027 fails with a System.Runtime version error"

Revit 2027 runs on .NET 10. Install the .NET 10 SDK and build with `-p:RevitVersion=2027`; the project switches to `net10.0-windows` automatically.

### "I have an older Revit version"

The add-in officially supports **Revit 2025**, **2026** and **2027**. Revit 2024 and earlier run on .NET Framework 4.8, which this project doesn't target — building for them would need project-level changes.

---

## Uninstalling

Delete `EstimatingTools.dll` and `EstimatingTools.addin` from `%APPDATA%\Autodesk\Revit\Addins\<version>\` (whichever you installed into) and restart Revit.

Settings saved in your models (Pricing Setup, zone bands) stay in those models and are simply ignored without the add-in.
