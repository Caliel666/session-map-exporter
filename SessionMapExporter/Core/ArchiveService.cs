using System.IO;
using System.Text.Json;
using CUE4Parse;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;

namespace SessionMapExporter.Core;

/// <summary>One export job: a world plus the folder it should end up in.</summary>
public sealed record ExportRequest(
    IReadOnlyList<MapEntry> Worlds,
    string FolderName,
    string DisplayName)
{
    public MapEntry PrimaryWorld => Worlds[0];
}

public sealed class ArchiveService
{
    public DefaultFileProvider? Provider { get; private set; }

    public static string? FindPakDirectory(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return null;
        var direct = Path.Combine(gameRoot, "SessionGame", "Content", "Paks");
        if (Directory.Exists(direct)) return direct;

        var candidates = new[]
        {
            Path.Combine(gameRoot, "Content", "Paks"),
            Path.Combine(gameRoot, "Paks")
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    public void Initialize(string pakDirectory, string aesKey)
    {
        if (string.IsNullOrWhiteSpace(aesKey))
            throw new ArgumentException("Enter the AES key first.", nameof(aesKey));

        var normalized = aesKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? aesKey
            : "0x" + aesKey;

        if (normalized.Length != 66 || !normalized[2..].All(Uri.IsHexDigit))
            throw new ArgumentException("AES key must be 32 bytes / 64 hexadecimal characters.", nameof(aesKey));

        var version = new VersionContainer(EGame.GAME_UE4_27);
        var provider = new DefaultFileProvider(pakDirectory, SearchOption.TopDirectoryOnly, true, version);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(normalized));
        Provider = provider;
    }

    /// <summary>
    /// Returns actual Unreal worlds, not every package ending in .umap.
    /// A package is only shown when it successfully resolves a UWorld export.
    /// Streaming/sublevel packages are deliberately not promoted to separate maps;
    /// the CUE4Parse WorldExporter follows them from the selected persistent world.
    /// </summary>
    public IReadOnlyList<MapEntry> FindWorlds(IProgress<string>? progress = null)
    {
        if (Provider is null) throw new InvalidOperationException("Provider has not been initialized.");

        var candidates = Provider.Files.Values
            .Select(x => x.Path.Replace('\\', '/'))
            .Where(p => p.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var worlds = new List<MapEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < candidates.Length; i++)
        {
            var path = candidates[i];
            progress?.Report($"Inspecting world packages {i + 1}/{candidates.Length}…");

            try
            {
                var package = Provider.LoadPackage(path);
                var world = package.GetExports()
                    .FirstOrDefault(x => x.GetType().Name.Equals("UWorld", StringComparison.Ordinal));

                if (world is null) continue;

                var normalizedPath = path.TrimStart('/');
                if (seen.Add(normalizedPath))
                    worlds.Add(new MapEntry(normalizedPath));
            }
            catch
            {
                // Some .umap packages are auxiliary/corrupt/unsupported for this game build.
                // They are not reported as maps merely because their filename ends in .umap.
            }
        }

        return worlds
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task ExportWorldsAsync(
        IReadOnlyList<ExportRequest> requests,
        string outputRoot,
        bool exportTextures,
        bool exportMaterials,
        bool noGameLighting,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (Provider is null) throw new InvalidOperationException("Provider has not been initialized.");

        var summary = new List<object>();
        var mapsRoot = Path.Combine(outputRoot, "Maps");
        Directory.CreateDirectory(mapsRoot);

        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();

            // Clean layout: Maps/<Map Name>/source  - no more deep package trees.
            var mapOut = Path.Combine(mapsRoot, UniqueFolderName(request.FolderName, usedFolders));
            var sourceOut = Path.Combine(mapOut, "source");
            Directory.CreateDirectory(sourceOut);

            progress?.Report($"Loading {request.DisplayName} ({request.Worlds.Count} world packages)…");

            var loadedWorlds = new List<(MapEntry Entry, UWorld World)>();
            foreach (var worldEntry in request.Worlds)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Loading {worldEntry.DisplayName}…");

                var package = Provider.LoadPackage(worldEntry.Path);
                var world = package.GetExports()
                    .OfType<UWorld>()
                    .FirstOrDefault();

                if (world is null)
                {
                    progress?.Report($"Skipping {worldEntry.DisplayName}: UWorld export could not be resolved.");
                    continue;
                }

                loadedWorlds.Add((worldEntry, world));
            }

            if (loadedWorlds.Count == 0)
            {
                summary.Add(new
                {
                    worlds = request.Worlds.Select(x => x.Path).ToArray(),
                    success = false,
                    error = "None of the selected map's world packages could be resolved."
                });
                continue;
            }

            var queuedWorldObjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ExportSession? session = null;
            session = new ExportSession((args, filterCt) =>
            {
                // CUE4Parse's WorldExporter only automatically queues streaming
                // levels marked persistent. Session has streamed map content that
                // can be referenced from non-persistent levels, so queue every
                // streaming world exposed by WorldDto as well.
                foreach (var level in args.StreamingLevels)
                {
                    filterCt.ThrowIfCancellationRequested();
                    queuedWorldObjectPaths.Add(level.World.GetPathName());
                    session.Add(level.World);
                }
            })
            {
                MaxDegreeOfParallelism = 1
            };

            // IMPORTANT: a Session map is composed of several sibling UWorld
            // packages (_Art, _Unmerged, _Vege, etc.). Export the whole group into
            // one ExportSession instead of choosing only one "primary" world.
            foreach (var (_, world) in loadedWorlds)
            {
                queuedWorldObjectPaths.Add(world.GetPathName());
                session.Add(world);
            }

            var options = BuildExportOptions(exportTextures, exportMaterials);

            progress?.Report($"Exporting {request.DisplayName} ({loadedWorlds.Count} world packages) as USD…");
            var results = await session.RunAsync(sourceOut, options, null, ct);

            if (noGameLighting)
                StripUsdLights(sourceOut);

            var sourceWorlds = results
                .Where(r => r.Success && queuedWorldObjectPaths.Contains(r.ObjectPath) && r.DiskFilePaths is not null)
                .SelectMany(r => r.DiskFilePaths!)
                .Where(p => p.EndsWith(".usda", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.GetRelativePath(mapOut, p).Replace('\\', '/'))
                .ToList();

            // Blender's USD importer does not resolve USD sublayers/references
            // reliably. Create one composition root; build_map.py will flatten it
            // with Blender's bundled Pixar USD library before importing the geometry.
            if (sourceWorlds.Count == 0)
            {
                summary.Add(new
                {
                    worlds = request.Worlds.Select(x => x.Path).ToArray(),
                    displayName = request.DisplayName,
                    success = false,
                    resultCount = results.Count,
                    error = "No exported UWorld USDA files were produced."
                });
                continue;
            }

            var compositionRoot = WriteCompositionRoot(mapOut, request.DisplayName, sourceWorlds);

            // Generate the helper that actually imports the complete composed map.
            WriteBlenderHelper(mapOut, compositionRoot);

            var textureFiles = Directory.EnumerateFiles(sourceOut, "*.*", SearchOption.AllDirectories)
                .Count(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                           p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                           p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                           p.EndsWith(".tga", StringComparison.OrdinalIgnoreCase));

            var materialFiles = Directory.EnumerateFiles(sourceOut, "*.usda", SearchOption.AllDirectories)
                .Count(p => p.Contains($"{Path.DirectorySeparatorChar}Materials{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase));

            var manifest = new
            {
                worlds = request.Worlds.Select(x => x.Path).ToArray(),
                displayName = request.DisplayName,
                format = "USD / USDA",
                compositionRoot = Path.GetRelativePath(mapOut, compositionRoot).Replace('\\', '/'),
                builder = "run python build_map.py to flatten the USD composition in Blender and write a textured .glb",
                exportedAtUtc = DateTime.UtcNow,
                worldPackagesRequested = request.Worlds.Count,
                worldPackagesLoaded = loadedWorlds.Count,
                streamingLevelsQueued = true,
                gameLightingExcluded = noGameLighting,
                textureExportRequested = exportTextures,
                materialExportRequested = exportMaterials,
                exportedTextureFileCount = textureFiles,
                exportedMaterialFileCount = materialFiles,
                sourceWorlds,
                resultCount = results.Count,
                successfulResultCount = results.Count(r => r.Success),
                results = results.Select(r => new
                {
                    r.ObjectPath,
                    r.Success,
                    diskFilePaths = r.DiskFilePaths?.ToArray(),
                    error = r.Error?.ToString()
                }).ToArray()
            };

            await File.WriteAllTextAsync(
                Path.Combine(mapOut, "export-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                ct);

            summary.Add(new
            {
                worlds = request.Worlds.Select(x => x.Path).ToArray(),
                displayName = request.DisplayName,
                success = results.Any(r => r.Success),
                resultCount = results.Count,
                outputDirectory = mapOut
            });
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "maps-export-summary.json"),
            JsonSerializer.Serialize(new
            {
                exportedAtUtc = DateTime.UtcNow,
                mapCount = requests.Count,
                maps = summary
            }, new JsonSerializerOptions { WriteIndented = true }),
            ct);
    }

    private static string UniqueFolderName(string folderName, HashSet<string> used)
    {
        // folder names may contain subfolders (individual-world exports)
        var segments = folderName.Split('\\', '/');
        var name = string.Join(Path.DirectorySeparatorChar,
            segments.Select(MapGrouping.Sanitize));
        var candidate = name;
        var i = 2;
        while (!used.Add(candidate))
            candidate = $"{name} {i++}";
        return candidate;
    }

    private static string WriteCompositionRoot(string mapOut, string displayName, IReadOnlyList<string> sourceWorlds)
    {
        var rootPath = Path.Combine(mapOut, $"{MapGrouping.Sanitize(displayName)}.usda");
        using var writer = new StreamWriter(rootPath, false, new System.Text.UTF8Encoding(false));

        writer.WriteLine("#usda 1.0");
        writer.WriteLine("(");
        writer.WriteLine("    subLayers = [");
        for (var i = 0; i < sourceWorlds.Count; i++)
        {
            var rel = sourceWorlds[i].Replace('\\', '/').Replace("\"", "\\\"");
            writer.WriteLine($"        @{rel}@{(i + 1 == sourceWorlds.Count ? "" : ",")}");
        }
        writer.WriteLine("    ]");
        writer.WriteLine(")");
        writer.WriteLine();
        writer.WriteLine("def Xform \"SessionMap\"");
        writer.WriteLine("{");
        writer.WriteLine("}");
        return rootPath;
    }

    private static ExportOptions BuildExportOptions(bool textures, bool materials)
    {
        // ExportOptions uses primary-constructor parameters and exposes readonly fields.
        // Reflection against properties therefore cannot change MeshFormat after construction.
        // Construct it with the current CUE4Parse values instead.
        var type = typeof(ExportOptions);
        var ctor = type.GetConstructors().OrderByDescending(x => x.GetParameters().Length).First();

        var args = ctor.GetParameters().Select(p =>
        {
            var name = p.Name ?? string.Empty;
            var t = p.ParameterType;

            if (t.IsEnum)
            {
                var preferred = name switch
                {
                    var n when n.Contains("meshFormat", StringComparison.OrdinalIgnoreCase) =>
                        new[] { "USD" },
                    var n when n.Contains("nanite", StringComparison.OrdinalIgnoreCase) =>
                        new[] { "NoNanite" },
                    var n when n.Contains("meshQuality", StringComparison.OrdinalIgnoreCase) =>
                        new[] { "Highest" },
                    var n when n.Contains("texturePlatform", StringComparison.OrdinalIgnoreCase) =>
                        new[] { "DesktopMobile" },
                    var n when n.Contains("textureFormat", StringComparison.OrdinalIgnoreCase) =>
                        new[] { "Png" },
                    var n when n.Contains("materialDepth", StringComparison.OrdinalIgnoreCase) =>
                        materials ? new[] { "AllLayersNoRef" } : new[] { "TopLayerOnly" },
                    var n when n.Contains("socketFormat", StringComparison.OrdinalIgnoreCase) =>
                        new[] { "Bone" },
                    var n when n.Contains("compressionFormat", StringComparison.OrdinalIgnoreCase) =>
                        new[] { "None" },
                    _ => Array.Empty<string>()
                };

                foreach (var candidate in preferred)
                {
                    var match = Enum.GetNames(t).FirstOrDefault(x =>
                        x.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                        return Enum.Parse(t, match, true);
                }

                return Enum.GetValues(t).GetValue(0)!;
            }

            if (t == typeof(bool))
            {
                return name switch
                {
                    var n when n.Contains("exportHdrTexturesAsHdr", StringComparison.OrdinalIgnoreCase) => false,
                    var n when n.Contains("exportAllTextureMips", StringComparison.OrdinalIgnoreCase) => false,
                    var n when n.Contains("exportMaterials", StringComparison.OrdinalIgnoreCase) => materials,
                    var n when n.Contains("exportMorphTargets", StringComparison.OrdinalIgnoreCase) => false,
                    _ => false
                };
            }

            if (t == typeof(int))
                return name.Contains("textureQuality", StringComparison.OrdinalIgnoreCase) ? 95 : 0;

            if (t == typeof(float)) return 1f;
            if (t == typeof(double)) return 1d;

            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }).ToArray();

        return (ExportOptions)ctor.Invoke(args);
    }

    private static void StripUsdLights(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.usda", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var stripped = RemoveUsdLightPrims(text);
            if (!text.Equals(stripped, StringComparison.Ordinal))
                File.WriteAllText(file, stripped);
        }
    }

    private static string RemoveUsdLightPrims(string text)
    {
        var lightTypes = new[]
        {
            "DistantLight", "SphereLight", "RectLight", "DiskLight",
            "DomeLight", "CylinderLight", "PortalLight", "Light"
        };

        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var output = new List<string>(lines.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            var isLight = lightTypes.Any(type =>
                trimmed.StartsWith($"def {type} ", StringComparison.Ordinal) ||
                trimmed.StartsWith($"over {type} ", StringComparison.Ordinal));

            if (!isLight)
            {
                output.Add(lines[i]);
                continue;
            }

            var depth = CountBraces(lines[i]);
            while (i + 1 < lines.Length && depth > 0)
            {
                i++;
                depth += CountBraces(lines[i]);
            }
        }

        return string.Join(Environment.NewLine, output);
    }

    private static int CountBraces(string line)
    {
        var count = 0;
        var inString = false;

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                inString = !inString;
            else if (!inString)
            {
                if (line[i] == '{') count++;
                else if (line[i] == '}') count--;
            }
        }

        return count;
    }

    private static void WriteBlenderHelper(string mapOut, string compositionRoot)
    {
        var rootLiteral = Path.GetFullPath(compositionRoot)
            .Replace("\\", "/")
            .Replace("\"", "\\\"");

        var buildScript = $$"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
WORLD = Path(r"{{rootLiteral}}")
DEFAULT_GLB = ROOT / "{{Path.GetFileNameWithoutExtension(compositionRoot)}}.glb"

def find_blender(explicit=None):
    if explicit:
        return explicit
    for name in ("blender", "blender.exe"):
        found = shutil.which(name)
        if found:
            return found
    candidates = []
    for base in (
        Path(os.environ.get("PROGRAMFILES", "")) / "Blender Foundation",
        Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "Blender Foundation",
    ):
        if base.exists():
            candidates.extend(base.glob("Blender */blender.exe"))
    if candidates:
        return str(sorted(candidates)[-1])
    return None

def main():
    ap = argparse.ArgumentParser(description="Flatten a SessionMapExporter USD map and build a textured GLB.")
    ap.add_argument("--blender", help="Path to blender.exe")
    ap.add_argument("--output", type=Path, help="Override GLB output path")
    args = ap.parse_args()

    blender = find_blender(args.blender)
    if not blender:
        print("ERROR: Blender was not found. Use --blender C:\\Path\\to\\blender.exe")
        return 2

    if not WORLD.exists():
        print(f"ERROR: USD composition root not found: {WORLD}")
        return 2

    glb = (args.output or DEFAULT_GLB).resolve()
    flat = ROOT / "_flattened_map.usda"
    blender_script = ROOT / "_build_map_blender.py"

    script = r'''
import bpy
import sys
from pathlib import Path

bpy.utils.expose_bundled_modules()
from pxr import Usd

args = sys.argv[sys.argv.index("--") + 1:]
world = Path(args[0]).resolve()
flat = Path(args[1]).resolve()
glb = Path(args[2]).resolve()

print("SessionMapExporter: opening composed USD:", world)
stage = Usd.Stage.Open(str(world))
if stage is None:
    raise RuntimeError("USD stage could not be opened")

print("SessionMapExporter: flattening USD composition...")
flat_layer = stage.Flatten()
if not flat_layer.Export(str(flat)):
    raise RuntimeError("USD flatten/export failed")

print("SessionMapExporter: importing flattened stage into Blender...")
bpy.ops.wm.usd_import(
    filepath=str(flat),
    import_lights=False,
    import_materials=True,
    import_meshes=True,
    import_curves=True,
    import_points=True,
    import_visible_only=True,
    read_mesh_uvs=True,
    read_mesh_colors=True,
    read_mesh_attributes=True,
    import_usd_preview=True,
    import_textures_mode='IMPORT_PACK',
)

for obj in list(bpy.data.objects):
    if obj.type == 'LIGHT':
        bpy.data.objects.remove(obj, do_unlink=True)

bpy.ops.object.select_all(action='SELECT')
bpy.context.view_layer.objects.active = next(
    (o for o in bpy.context.selected_objects if o.type == 'MESH'),
    None
)

print("SessionMapExporter: exporting GLB:", glb)
bpy.ops.export_scene.gltf(
    filepath=str(glb),
    export_format='GLB',
    export_image_format='AUTO',
    export_materials='EXPORT',
    export_lights=False,
    export_cameras=False,
    export_texcoords=True,
    export_normals=True,
    export_yup=True,
    use_selection=False,
)
print("SessionMapExporter: GLB complete")
'''

    blender_script.write_text(script, encoding="utf-8")
    try:
        cmd = [blender, "--background", "--factory-startup", "--python", str(blender_script),
               "--", str(WORLD), str(flat), str(glb)]
        print("Running:", " ".join(f'"{x}"' if " " in x else x for x in cmd))
        return subprocess.run(cmd).returncode
    finally:
        try:
            blender_script.unlink()
        except FileNotFoundError:
            pass

if __name__ == "__main__":
    raise SystemExit(main())
""";

        File.WriteAllText(Path.Combine(mapOut, "build_map.py"), buildScript);

        var importScript = $$"""
import bpy
from pathlib import Path

ROOT = Path(__file__).resolve().parent
WORLD = Path(r"{{rootLiteral}}")

if not WORLD.exists():
    raise FileNotFoundError(f"USD composition root not found: {WORLD}")

# Blender's USD importer does not resolve the CUE4Parse layer/reference
# composition on its own. build_map.py performs the USD flattening step first.
print("SessionMapExporter: use build_map.py to build the complete map.")
print("Composition root:", WORLD)
""";

        File.WriteAllText(Path.Combine(mapOut, "import_to_blender.py"), importScript);
    }


}