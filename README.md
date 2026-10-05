# Session Skate Sim Map Exporter

Windows/WPF exporter for Session Skate Sim worlds.

## What it does

* Select a Session installation directory.
* Enter the AES key at runtime (nothing is hard-coded).
* Mount the game's PAK/IoStore archives with CUE4Parse.
* Enumerate **every virtual `.umap`** found in all mounted archives. There is no hardcoded map list.
* **Consolidates the world list into real maps.** Session ships dozens of auxiliary worlds per map (`…_Art`, `_Audio`, `_Lights`, `_Vege`, `_Unmerged`, …); the map selector shows one entry per map (e.g. *LESColemen Park*), not 700+ raw packages. Tick **Show individual worlds** to see the raw list again.
* Search/filter the map list.
* Export one, several, or all maps.
* **Exports the FULL map.** One export covers every world package of the selected map group - the persistent world plus all streamed sublevels (`…_Art_LevelArchitecture`, `…_Art_Props`, `…_Art_Unmerged`, …). Exporting only one of them yields only a fraction of the map (this is why skateparks/city geometry used to be missing). Shared static meshes are written once (`ExportSession` dedupes by object path).
* Uses CUE4Parse-Conversion's `ExportSession`, which has native handlers for UWorld, static meshes, textures, materials, landscapes and spline meshes.
* Writes **clean per-map output folders** (see below) instead of mirroring the whole game package tree.
* Ships **one `build_map.py`** per export that turns the raw USD data into a **single `.glb` (glTF 2.0) file with all textures embedded** — which you import straight into Blender. No `bpy`, no Blender scripting, no per-map broken helper scripts.
* Does not intentionally add/export Unreal light actors as a new Blender lighting rig.

## Requirements

* Visual Studio 2022/2026 with the .NET 10 SDK and Desktop development workload.
* Windows x64.
* A legally installed copy of Session Skate Sim.
* The game's AES key.
* For the Blender step: any Python 3.8+ (standard library only; `pip install pillow` is an optional extra).

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
4. Pick one or more maps from the list (expand *Show individual worlds* if you need a specific raw world package).
5. Choose an output folder.
6. Click **EXPORT SELECTED MAPS**.
7. Open a terminal in the output folder and run:

   ```powershell
   python build_map.py
   ```

   (build one map only: `python build_map.py "Maps/LESColemen Park"`)

8. In Blender: **File → Import → glTF 2.0** and pick `Maps/<map name>/<map name>.glb`.

## Export layout

```
Exports/
├── build_map.py                     the one and only helper script
├── README.txt                       the instructions above, offline
├── maps-export-summary.json
└── Maps/
    └── LESColemen Park/             one folder per map - proper files, proper places
        ├── export-manifest.json
        ├── LESColemen Park.glb      ← built by build_map.py; import this into Blender
        ├── build-report.json        ← what was built / skipped / missing textures
        ├── map-scene.json           ← raw placement data (every mesh + its UE transform)
        └── source/                  raw USD/USDA + textures (the master copy)
```

The old behaviour (a `SessionGame/Content/Art/Env/...` tree plus a per-map
`import_to_blender.py` that crashed with `ModuleNotFoundError: No module named
'bpy'`) is gone. `import_to_blender.py` needed Blender's embedded Python and
gave no standalone model; `build_map.py` runs on plain Python and produces a
clean asset instead.

## Full map = all world packages

A Session map is NOT one file - the playable map is spread across several
world packages (`…_Art_LevelArchitecture`, `…_Art_Props`, `…_Art_Unmerged`,
plus the persistent world). One map export therefore runs one WorldExporter
per world package of the group and writes them all into the same
`source/` folder; `build_map.py` then composes **every** world stage into a
single scene. Shared static meshes are written once (the export session
deduplicates by object path), and identical baked geometry (straight spline
coping / rail segments that were exported per actor) is collapsed to one mesh
by content hash.

## Optional: instanced Blender import (map-scene.json)

Besides the .glb, `build_map.py` writes `map-scene.json` - the raw data file
listing every unique asset and where each one is placed in the world (raw UE
translation/rotation/scale, centimetres). The `import_to_blender.py` script
builds a fully instanced Blender scene from it: each unique asset is imported
exactly once (then deduplicated by content) and instanced for every
placement, which keeps even coping-heavy maps light on RAM. Run it from
Blender's text editor with `MAP_ROOT` pointing at the map folder (or drop it
next to `map-scene.json`).

The old behaviour (a `SessionGame/Content/Art/Env/...` tree plus a per-map
`import_to_blender.py` that crashed with `ModuleNotFoundError: No module named
'bpy'`) is gone. `import_to_blender.py` needed Blender's embedded Python and
gave no standalone model; `build_map.py` runs on plain Python and produces a
clean asset instead.

## Notes on fidelity

* Geometry is exported in metres, +Y up, glTF conventions — CUE4Parse's exact coordinate mapping.
* Materials become glTF PBR materials (base colour, normal, ORM metallic/roughness, emissive, alpha masked/translucent).
* Point instancers (foliage, props) become real shared-mesh instances.
* Game lights, collision/guide shapes and invisible prims are skipped.
* Texture files referenced by materials are resolved even in old exports where all-mip export wrote `T_Name_MIP0.png` while materials pointed at `T_Name.png` (the exporter now exports the referenced mip only).

## Important

The CUE4Parse API is versioned. This project pins the current 1.2.2.202610 packages. If a future release changes an API, update the two package versions together.

The exporter deliberately does not ship or embed an AES key. The key entered into the UI exists only for the running process.
