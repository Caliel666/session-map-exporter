using System.IO;
using System.Reflection;
using System.Text.Json;
using CUE4Parse;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;

namespace SessionMapExporter.Core;

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
        provider.PostMount();
        Provider = provider;
    }

    /// <summary>
    /// Finds actual UWorld packages. The list is intentionally a list of persistent/
    /// independently loadable worlds, not a list of every .umap filename.
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
        for (var i = 0; i < candidates.Length; i++)
        {
            var path = candidates[i];
            progress?.Report($"Resolving worlds {i + 1}/{candidates.Length}…");

            try
            {
                var package = Provider.LoadPackage(path);
                if (package.GetExports().Any(x => x.GetType().Name.Equals("UWorld", StringComparison.Ordinal)))
                    worlds.Add(new MapEntry(path.TrimStart('/')));
            }
            catch
            {
                // Unsupported/corrupt auxiliary package: not a selectable world.
            }
        }

        return worlds
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task ExportWorldsAsync(
        IReadOnlyList<MapEntry> maps,
        string outputRoot,
        bool exportTextures,
        bool exportMaterials,
        bool noGameLighting,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (Provider is null) throw new InvalidOperationException("Provider has not been initialized.");

        var options = new ExportOptions(
            meshFormat: EMeshFormat.USD,
            naniteMeshFormat: CUE4Parse_Conversion.Writers.UEFormat.Enums.ENaniteMeshFormat.NoNanite,
            meshQuality: CUE4Parse_Conversion.Writers.UEFormat.Enums.EMeshQuality.Highest,
            texturePlatform: CUE4Parse.UE4.Assets.Exports.Texture.ETexturePlatform.DesktopMobile,
            textureFormat: CUE4Parse_Conversion.Writers.UEFormat.Enums.ETextureFormat.Png,
            textureQuality: 100,
            exportHdrTexturesAsHdr: false,
            exportAllTextureMips: false,
            materialDepth: exportMaterials
                ? CUE4Parse_Conversion.Writers.UEFormat.Enums.EMaterialDepth.AllLayersNoRef
                : CUE4Parse_Conversion.Writers.UEFormat.Enums.EMaterialDepth.TopLayerOnly,
            exportMaterials: exportMaterials,
            exportMorphTargets: false,
            socketFormat: CUE4Parse_Conversion.Writers.UEFormat.Enums.ESocketFormat.None,
            compressionFormat: CUE4Parse_Conversion.Writers.UEFormat.Enums.EFileCompressionFormat.None);

        foreach (var map in maps)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Building map scene: {map.DisplayName}…");

            var relative = map.Path.Replace('/', Path.DirectorySeparatorChar);
            relative = Path.ChangeExtension(relative, null) ?? relative;
            var mapOut = Path.Combine(outputRoot, "Maps", SanitizeRelativePath(relative));
            Directory.CreateDirectory(mapOut);

            await ExportMapSceneAsync(map, mapOut, options, exportTextures, exportMaterials, noGameLighting, progress, ct);
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "Maps", "README.txt"),
            "Each map is exported as a lightweight scene manifest plus unique USD mesh assets.\n" +
            "Do NOT open a generated _flattened_map.usda: this exporter intentionally does not generate one.\n" +
            "Run import_to_blender.py from a map folder to instance the exported assets without flattening the entire world.\n",
            ct);
    }

    private async Task ExportMapSceneAsync(
        MapEntry map,
        string mapOut,
        ExportOptions options,
        bool exportTextures,
        bool exportMaterials,
        bool noGameLighting,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var package = Provider!.LoadPackage(map.Path);
        var rootWorld = package.GetExports().FirstOrDefault(x => x.GetType().Name.Equals("UWorld", StringComparison.Ordinal)) as UWorld;
        if (rootWorld is null)
            throw new InvalidDataException($"'{map.Path}' did not resolve to a UWorld.");

        var scene = new MapSceneData
        {
            MapPath = map.Path,
            MapName = map.DisplayName,
            GeneratedUtc = DateTime.UtcNow,
            GameLightingExcluded = noGameLighting
        };

        var uniqueAssets = new Dictionary<string, UObject>(StringComparer.OrdinalIgnoreCase);
        var rootWorlds = new Dictionary<string, UWorld>(StringComparer.OrdinalIgnoreCase);
        CollectWorld(rootWorld, scene, uniqueAssets, rootWorlds, ct);

        // Always export each unique mesh only once. Actor placements become instances in the
        // Blender scene, which is the critical difference from WorldExporter/flattened USD.
        var assetSession = new ExportSession
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
        };

        foreach (var asset in uniqueAssets.Values)
            assetSession.Add(asset);

        progress?.Report($"Exporting {uniqueAssets.Count} unique mesh assets for {map.DisplayName}…");
        var assetsOut = Path.Combine(mapOut, "Assets");
        Directory.CreateDirectory(assetsOut);

        var results = uniqueAssets.Count == 0
            ? Array.Empty<ExportResult>()
            : await assetSession.RunAsync(assetsOut, options, null, ct);

        var exported = results
            .Where(r => r.Success && r.DiskFilePaths is { Count: > 0 })
            .ToDictionary(r => r.ObjectPath, r => r.DiskFilePaths![0], StringComparer.OrdinalIgnoreCase);

        // Spline components are also roots in CUE4Parse. They are exported once each because
        // their geometry is component-specific; they are never duplicated into the world file.
        foreach (var placement in scene.Instances.Where(x => x.AssetKind == "SplineMesh"))
        {
            if (placement.SourceObjectPath is null) continue;
            if (!exported.ContainsKey(placement.SourceObjectPath))
            {
                // The object was already queued through uniqueAssets when possible.
            }
        }

        foreach (var asset in scene.Assets)
        {
            if (exported.TryGetValue(asset.ObjectPath, out var path))
                asset.File = Path.GetRelativePath(mapOut, path).Replace('\\', '/');
        }

        if (noGameLighting)
            scene.Instances.RemoveAll(x => x.AssetKind == "Light");

        await File.WriteAllTextAsync(
            Path.Combine(mapOut, "map-scene.json"),
            JsonSerializer.Serialize(scene, JsonOptions),
            ct);

        WriteBlenderHelper(mapOut);
        WriteMapInfo(mapOut, scene, rootWorlds.Count);
    }

    private void CollectWorld(
        UWorld world,
        MapSceneData scene,
        Dictionary<string, UObject> assets,
        Dictionary<string, UWorld> worlds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!worlds.TryAdd(world.GetPathName(), world)) return;

        var dto = new WorldDto(world, ct);
        try
        {
            foreach (var actor in dto.Actors)
                CollectActor(actor, FTransform.Identity, scene, assets, ct);

            foreach (var streaming in dto.StreamingLevels)
                CollectWorld(streaming.World, scene, assets, worlds, ct);
        }
        finally
        {
            dto.Dispose();
        }
    }

    private void CollectActor(
        ActorDto actor,
        FTransform parentWorld,
        MapSceneData scene,
        Dictionary<string, UObject> assets,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!actor.IsVisible) return;

        var rootWorld = actor.RootComponent is null
            ? parentWorld
            : actor.RootComponent.Transform * parentWorld;

        CollectComponent(actor.RootComponent, rootWorld, scene, assets, ct);
    }

    private void CollectComponent(
        SceneComponentDto? component,
        FTransform componentWorld,
        MapSceneData scene,
        Dictionary<string, UObject> assets,
        CancellationToken ct)
    {
        if (component is null) return;
        ct.ThrowIfCancellationRequested();

        if (component is CUE4Parse_Conversion.Dto.MeshComponentDto mesh && !mesh.MeshPtr.IsNull)
        {
            if (mesh is CUE4Parse_Conversion.Dto.SplineMeshComponentDto spline)
            {
                var splineObject = GetPrivateUObject(spline, "_component");
                if (splineObject is not null)
                {
                    var path = splineObject.GetPathName();
                    assets.TryAdd(path, splineObject);
                    scene.Assets.TryAdd(new SceneAsset(path, "SplineMesh"));
                    scene.Instances.Add(new SceneInstance
                    {
                        AssetKind = "SplineMesh",
                        SourceObjectPath = path,
                        Transform = ToTransform(componentWorld),
                        Name = component.Name
                    });
                }
            }
            else if (mesh.MeshPtr.TryLoad<UObject>(out var meshObject))
            {
                var path = meshObject.GetPathName();
                assets.TryAdd(path, meshObject);
                scene.Assets.TryAdd(new SceneAsset(path, "Mesh"));
                AddPlacement(mesh, meshObject, componentWorld, scene);
            }
        }
        else if (component is CUE4Parse_Conversion.Dto.LandscapeMeshComponentDto landscape)
        {
            var landscapeObject = GetPrivateUObject(landscape, "_component");
            if (landscapeObject is not null)
            {
                var path = landscapeObject.GetPathName();
                assets.TryAdd(path, landscapeObject);
                scene.Assets.TryAdd(new SceneAsset(path, "Landscape"));
                scene.Instances.Add(new SceneInstance
                {
                    AssetKind = "Landscape",
                    SourceObjectPath = path,
                    Transform = ToTransform(componentWorld),
                    Name = component.Name
                });
            }
        }
        else if (component.GetType().Name.Contains("LightComponent", StringComparison.Ordinal))
        {
            scene.Instances.Add(new SceneInstance
            {
                AssetKind = "Light",
                Name = component.Name,
                Transform = ToTransform(componentWorld)
            });
        }

        foreach (var child in component.Children)
        {
            var childWorld = child.Transform * componentWorld;
            CollectComponent(child, childWorld, scene, assets, ct);
        }

        foreach (var attached in component.AttachedActors)
            CollectActor(attached, componentWorld, scene, assets, ct);
    }

    private static void AddPlacement(
        CUE4Parse_Conversion.Dto.MeshComponentDto mesh,
        UObject meshObject,
        FTransform componentWorld,
        MapSceneData scene)
    {
        var path = meshObject.GetPathName();

        if (mesh is CUE4Parse_Conversion.Dto.InstancedStaticMeshComponentDto ism)
        {
            foreach (var local in ism.Transforms)
            {
                scene.Instances.Add(new SceneInstance
                {
                    AssetKind = "Mesh",
                    SourceObjectPath = path,
                    Transform = ToTransform(local * componentWorld),
                    Name = mesh.Name
                });
            }
        }
        else
        {
            scene.Instances.Add(new SceneInstance
            {
                AssetKind = mesh.GetType().Name.Contains("Skeletal", StringComparison.OrdinalIgnoreCase)
                    ? "SkeletalMesh"
                    : "Mesh",
                SourceObjectPath = path,
                Transform = ToTransform(componentWorld),
                Name = mesh.Name
            });
        }
    }

    private static UObject? GetPrivateUObject(object dto, string fieldName)
    {
        var field = dto.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(dto) as UObject;
    }

    private static TransformData ToTransform(FTransform t) => new()
    {
        Translation = [t.Translation.X, t.Translation.Y, t.Translation.Z],
        Rotation = [t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W],
        Scale = [t.Scale3D.X, t.Scale3D.Y, t.Scale3D.Z]
    };

    private static string SanitizeRelativePath(string value)
    {
        var parts = value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(parts.Select(Sanitize).ToArray());
    }

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "Map" : value;
    }

    private static void WriteMapInfo(string mapOut, MapSceneData scene, int worldCount)
    {
        File.WriteAllText(
            Path.Combine(mapOut, "export-manifest.json"),
            JsonSerializer.Serialize(new
            {
                scene.MapPath,
                scene.MapName,
                scene.GeneratedUtc,
                format = "USD asset library + Blender instance scene",
                persistentAndStreamingWorldCount = worldCount,
                uniqueAssetCount = scene.Assets.Count,
                instanceCount = scene.Instances.Count,
                gameLightingExcluded = scene.GameLightingExcluded,
                flattenedWorldGenerated = false,
                note = "This export deliberately avoids composing/flattening a giant USD world."
            }, JsonOptions));
    }

    private static void WriteBlenderHelper(string mapOut)
    {
        var script = """
import bpy
import json
from pathlib import Path
from mathutils import Matrix, Quaternion, Vector

ROOT = Path(__file__).resolve().parent
DATA = json.loads((ROOT / "map-scene.json").read_text(encoding="utf-8"))

# Never import a flattened USD stage. Each unique mesh asset is loaded once, then linked
# into the scene for every placement. This keeps RAM proportional to unique geometry,
# rather than to the number of world instances.
asset_objects = {}

def unreal_to_blender_transform(t):
    # CUE4Parse USD meshes mirror Unreal Y. Apply the same handedness conversion to
    # actor transforms before creating the Blender matrix.
    tx, ty, tz = t["translation"]
    qx, qy, qz, qw = t["rotation"]
    sx, sy, sz = t["scale"]

    # Reflection across Y: M_blender = C * M_unreal * C, C=diag(1,-1,1,1).
    c = Matrix(((1,0,0,0),(0,-1,0,0),(0,0,1,0),(0,0,0,1)))
    q = Quaternion((qw, qx, qy, qz))
    r = q.to_matrix().to_4x4()
    s = Matrix.Diagonal((sx, sy, sz, 1.0))
    m = Matrix.Translation((tx, ty, tz)) @ r @ s
    return c @ m @ c

def import_asset(asset):
    rel = asset.get("file")
    if not rel:
        return []
    path = ROOT / rel
    if not path.exists():
        print("SessionMapExporter: missing asset", path)
        return []
    before = set(bpy.data.objects)
    try:
        bpy.ops.wm.usd_import(filepath=str(path))
    except Exception as exc:
        print("SessionMapExporter: USD asset import failed:", path, exc)
        return []
    return [o for o in bpy.data.objects if o not in before]

for asset in DATA["assets"]:
    imported = import_asset(asset)
    asset_objects[asset["objectPath"]] = imported

for inst in DATA["instances"]:
    if inst.get("assetKind") == "Light":
        continue
    source = inst.get("sourceObjectPath")
    originals = asset_objects.get(source, [])
    if not originals:
        continue

    matrix = unreal_to_blender_transform(inst["transform"])
    for original in originals:
        obj = original.copy()
        if original.data is not None:
            obj.data = original.data
        obj.matrix_world = matrix @ original.matrix_world
        obj.name = inst.get("name") or original.name
        bpy.context.collection.objects.link(obj)

# Keep the original imported asset prototypes in a hidden collection so their mesh datablocks
# remain available without cluttering the visible map.
prototype_collection = bpy.data.collections.new("SessionMapExporter_AssetPrototypes")
bpy.context.scene.collection.children.link(prototype_collection)
for source, originals in asset_objects.items():
    for obj in originals:
        for coll in list(obj.users_collection):
            coll.objects.unlink(obj)
        prototype_collection.objects.link(obj)
prototype_collection.hide_viewport = True
prototype_collection.hide_render = True

print("SessionMapExporter: imported", len(DATA["assets"]), "unique assets and",
      len(DATA["instances"]), "map placements.")
""";
        File.WriteAllText(Path.Combine(mapOut, "import_to_blender.py"), script);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private sealed class MapSceneData
    {
        public string MapPath { get; set; } = "";
        public string MapName { get; set; } = "";
        public DateTime GeneratedUtc { get; set; }
        public bool GameLightingExcluded { get; set; }
        public List<SceneAsset> Assets { get; } = [];
        public List<SceneInstance> Instances { get; } = [];
    }

    private sealed class SceneAsset
    {
        public SceneAsset(string objectPath, string kind)
        {
            ObjectPath = objectPath;
            AssetKind = kind;
        }

        public string ObjectPath { get; }
        public string AssetKind { get; }
        public string? File { get; set; }
    }

    private sealed class SceneInstance
    {
        public string AssetKind { get; set; } = "";
        public string? SourceObjectPath { get; set; }
        public string? Name { get; set; }
        public TransformData Transform { get; set; } = new();
    }

    private sealed class TransformData
    {
        public float[] Translation { get; set; } = [0, 0, 0];
        public float[] Rotation { get; set; } = [0, 0, 0, 1];
        public float[] Scale { get; set; } = [1, 1, 1];
    }
}
