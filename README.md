# Session Skate Sim Map Exporter

Windows/WPF exporter for Session: Skate Sim.

## What it does

- Select a Session installation directory.
- Enter the AES key at runtime; it is not hard-coded or written into the project.
- Mount Session's PAK/IoStore archives with CUE4Parse.
- Inspect mounted `.umap` packages and only list packages that resolve to an actual `UWorld`.
- Export a selected world as a **consolidated map scene**, including its streaming worlds, instead of exporting every auxiliary `.umap` as a separate map.
- Export each referenced mesh/spline/landscape asset **once** and store actor placements as lightweight instances.
- Export USD/USDA assets plus a compact `map-scene.json`.
- Generate a Blender Python importer that loads each unique asset once and creates linked object instances.
- Never generate a giant `_flattened_map.usda` scene.
- Optionally omit Unreal light objects from the generated Blender scene.

## Why the exporter does not create a giant world USD

CUE4Parse's built-in `WorldExporter` walks the world and queues meshes, spline meshes, landscapes and materials for export. Its USD world format is useful as an interchange representation, but composing that whole scene into one flattened USD stage can become enormous for a game world.

This project therefore uses CUE4Parse's world DTOs only to **read the world hierarchy**. It builds its own lightweight scene manifest:

```text
map-scene.json
    ├── unique asset A -> Assets/...usda
    ├── unique asset B -> Assets/...usda
    └── placements -> transforms referencing A/B
```

Blender imports each unique USD asset once and duplicates/links the resulting mesh datablocks for all placements. This avoids loading the same geometry thousands of times into memory.

## Export layout

```text
Exports/
└── Maps/
    └── <Map>/
        ├── export-manifest.json
        ├── map-scene.json
        ├── import_to_blender.py
        └── Assets/
            ├── SessionGame/
            │   └── Content/
            │       └── ... unique referenced assets ...
            └── Engine/
                └── Content/
                    └── ... only if actually referenced ...
```

There is deliberately **no** `_flattened_map.usda`.

## Blender import

Run the generated `import_to_blender.py` with Blender's bundled Python, or open Blender and execute the script from the Text Editor.

The script:

1. Reads `map-scene.json`.
2. Imports every unique USD asset exactly once.
3. Creates linked Blender object instances for the recorded placements.
4. Keeps the imported prototypes in a hidden collection.
5. Does not import game lights when the no-lighting option is enabled.

Do not attempt to import a flattened USD stage; none is produced.

## Build

Open `SessionMapExporter.sln` in Visual Studio with the .NET 10 SDK and Desktop development workload.

Or:

```powershell
dotnet restore
dotnet build -c Release
```

The project pins CUE4Parse and CUE4Parse-Conversion 1.2.2.202610.

## Use

1. Select the Session installation folder.
2. Enter the AES key.
3. Click **Scan game**.
4. Select one or more actual worlds.
5. Choose an output directory.
6. Click **EXPORT SELECTED MAPS**.
7. Run the generated `Maps/<map>/import_to_blender.py` from Blender's Python environment.

## Fidelity / limitations

- World streaming levels are recursively consolidated into the selected world scene.
- Static meshes, skeletal meshes, geometry collections, spline mesh components and landscape components are handled through CUE4Parse-Conversion.
- Instanced static meshes become repeated linked Blender objects using one shared mesh datablock.
- Spline mesh components remain individual exported assets because their deformation can differ even when they use the same source static mesh.
- Unreal light actors are represented only when lighting export is enabled; the default no-lighting option removes them from the Blender scene.
- This exporter does not intentionally import Unreal baked lightmap lighting.
- Some Unreal-specific material/shader behavior cannot be represented perfectly by Blender's USD importer.

## Important

The exporter deliberately does not ship or embed an AES key. The key entered into the UI exists only for the running process.
