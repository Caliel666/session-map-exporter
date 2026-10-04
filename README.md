# Session Skate Sim Map Exporter

Windows/WPF exporter for Session Skate Sim worlds.

## What it does

* Select a Session installation directory.
* Enter the AES key at runtime (nothing is hard-coded).
* Mount the game's PAK/IoStore archives with CUE4Parse.
* Enumerate **every virtual `.umap`** found in all mounted archives. There is no hardcoded map list.
* Search/filter the complete world list.
* Export one, several, or all worlds.
* Uses CUE4Parse-Conversion's `ExportSession`, which has native handlers for UWorld, static meshes, textures, materials, landscapes and spline meshes.
* Requests glTF-oriented mesh output and PNG textures where the current CUE4Parse version exposes those formats.
* Writes `export-manifest.json` and a `import_to_blender.py` helper per map.
* Does not intentionally add/export Unreal light actors as a new Blender lighting rig.

## Requirements

* Visual Studio 2022/2026 with the .NET 10 SDK and Desktop development workload.
* Windows x64.
* A legally installed copy of Session Skate Sim.
* The game's AES key.

## Build

Open `SessionMapExporter.sln` in Visual Studio and Build > Build Solution.

Or:

```powershell
dotnet restore
dotnet build -c Release
```

The first restore downloads CUE4Parse and CUE4Parse-Conversion from NuGet.

## Use

1. Select the Session installation folder.
2. Paste the AES key into the AES key field.
3. Click **Scan game**.
4. The list is generated from the mounted virtual filesystem and should contain all `.umap` worlds available to the installed build, not a hand-maintained list.
5. Select maps or click Select all.
6. Choose an output folder.
7. Click **EXPORT SELECTED MAPS**.
8. Open the generated map folder. The exporter writes the converted assets plus `export-manifest.json` and `import_to_blender.py`.

## Important

The CUE4Parse API is versioned. This project pins the current 1.2.2.202610 packages. If a future release changes an API, update the two package versions together.

The exporter deliberately does not ship or embed an AES key. The key entered into the UI exists only for the running process.
