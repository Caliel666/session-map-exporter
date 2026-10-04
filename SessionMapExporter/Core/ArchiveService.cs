using System.Reflection;
using System.Text.Json;
using CUE4Parse;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;

namespace SessionMapExporter.Core;

public sealed class ArchiveService
{
    public DefaultFileProvider? Provider { get; private set; }

    public static string? FindPakDirectory(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return null;
        var direct = Path.Combine(gameRoot, "SessionGame", "Content", "Paks");
        if (Directory.Exists(direct)) return direct;

        // Also accept SessionGame itself or Content as the selected folder.
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

        if (normalized.Length != 66)
            throw new ArgumentException("AES key must be 32 bytes / 64 hexadecimal characters.", nameof(aesKey));

        // Session is an Unreal game; use the UE4/UE5 container heuristics supplied by CUE4Parse.
        // The provider itself inspects PAK/UTOC/UCAS/UASSET/UMAP files during Initialize().
        var version = new VersionContainer(EGame.GAME_UE4_27);
        var provider = new DefaultFileProvider(pakDirectory, SearchOption.TopDirectoryOnly, true, version);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(normalized));
        provider.LoadLocalization(CUE4Parse.UE4.Objects.Core.Misc.ELanguage.English);
        Provider = provider;
    }

    public IReadOnlyList<MapEntry> FindWorlds()
    {
        if (Provider is null) throw new InvalidOperationException("Provider has not been initialized.");

        // Do not hardcode map names. Every virtual .umap in every mounted archive is considered.
        var worlds = Provider.Files.Values
            .Select(x => x.Path.Replace('\\', '/'))
            .Where(p => p.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new MapEntry(p))
            .ToList();

        return worlds;
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

        foreach (var map in maps)
        {
            ct.ThrowIfCancellationRequested();

            var safeName = Sanitize(Path.GetFileNameWithoutExtension(map.Path));
            var mapOut = Path.Combine(outputRoot, safeName);
            Directory.CreateDirectory(mapOut);

            progress?.Report($"Loading {map.DisplayName}…");

            var package = Provider.LoadPackage(map.Path);
            var world = package.GetExports().OfType<UWorld>().FirstOrDefault();

            if (world is null)
            {
                // Some packages expose the world through LoadObject rather than the export enumeration.
                var obj = Provider.LoadObject(map.Path);
                world = obj as UWorld;
            }

            if (world is null)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(mapOut, "EXPORT_FAILED.txt"),
                    $"Could not resolve UWorld from {map.Path}.{Environment.NewLine}",
                    ct);
                continue;
            }

            // ExportSession recursively queues the WorldExporter and referenced exporters.
            var session = new ExportSession
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            };

            session.Add(world);

            var options = BuildExportOptions(exportTextures, exportMaterials);

            progress?.Report($"Exporting {map.DisplayName}…");
            var results = await session.RunAsync(mapOut, options, null, ct);

            var manifest = new
            {
                map.Path,
                map.DisplayName,
                exportedAtUtc = DateTime.UtcNow,
                gameLightingExcluded = noGameLighting,
                textureExportRequested = exportTextures,
                materialExportRequested = exportMaterials,
                resultCount = results.Count,
                results = results.Select(r => new
                {
                    r.ObjectPath,
                    r.ExportPath,
                    r.Success,
                    r.Message
                }).ToArray()
            };

            await File.WriteAllTextAsync(
                Path.Combine(mapOut, "export-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                ct);

            WriteBlenderHelper(mapOut);
        }
    }

    private static ExportOptions BuildExportOptions(bool textures, bool materials)
    {
        // ExportOptions has changed constructor details over CUE4Parse releases.
        // Build it reflectively so the application survives minor API changes.
        var type = typeof(ExportOptions);
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor is null)
            throw new InvalidOperationException("CUE4Parse-Conversion did not expose an ExportOptions constructor.");

        var args = ctor.GetParameters().Select(p => DefaultForParameter(p.ParameterType, p.Name ?? "")).ToArray();
        var instance = (ExportOptions)ctor.Invoke(args);

        SetEnumProperty(instance, "MeshExportFormat", "Gltf", "GLTF", "glTF");
        SetEnumProperty(instance, "TextureExportFormat", "Png", "PNG");
        SetEnumProperty(instance, "MaterialExportFormat", "TopLayerOnly");
        SetBoolProperty(instance, "SaveEmbeddedMaterials", materials);
        SetBoolProperty(instance, "ExportAllTextureMips", true);
        SetBoolProperty(instance, "SaveHdrTexturesAsHdr", false);
        return instance;
    }

    private static object? DefaultForParameter(Type t, string name)
    {
        if (t.IsEnum)
        {
            var preferred = name.Contains("mesh", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Gltf", "GLTF", "glTF", "Fbx", "FBX", "UEFormat" }
                : new[] { "Png", "PNG", "TopLayerOnly" };
            foreach (var candidate in preferred)
                if (Enum.GetNames(t).Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    return Enum.Parse(t, candidate, true);
            return Enum.GetValues(t).GetValue(0);
        }

        if (t == typeof(bool)) return name.Contains("Embedded", StringComparison.OrdinalIgnoreCase);
        if (t == typeof(int)) return 100;
        if (t == typeof(float)) return 1f;
        if (t == typeof(double)) return 1d;
        if (t == typeof(string)) return "";
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }

    private static void SetEnumProperty(object obj, string property, params string[] names)
    {
        var p = obj.GetType().GetProperty(property);
        if (p?.CanWrite != true || !p.PropertyType.IsEnum) return;

        foreach (var name in names)
        {
            var match = Enum.GetNames(p.PropertyType).FirstOrDefault(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                p.SetValue(obj, Enum.Parse(p.PropertyType, match, true));
                return;
            }
        }
    }

    private static void SetBoolProperty(object obj, string property, bool value)
    {
        var p = obj.GetType().GetProperty(property);
        if (p?.CanWrite == true && p.PropertyType == typeof(bool))
            p.SetValue(obj, value);
    }

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "Map" : value;
    }

    private static void WriteBlenderHelper(string mapOut)
    {
        var script = """
import bpy
from pathlib import Path

ROOT = Path(__file__).resolve().parent

# Import all glTF/GLB/USD assets emitted by the exporter.
for path in sorted(ROOT.rglob("*")):
    if path.suffix.lower() in {".gltf", ".glb"}:
        try:
            bpy.ops.import_scene.gltf(filepath=str(path))
        except Exception as exc:
            print("SessionMapExporter: glTF import failed:", path, exc)
    elif path.suffix.lower() in {".usd", ".usda", ".usdc"}:
        try:
            bpy.ops.wm.usd_import(filepath=str(path))
        except Exception as exc:
            print("SessionMapExporter: USD import failed:", path, exc)

print("SessionMapExporter: import pass complete.")
""";
        File.WriteAllText(Path.Combine(mapOut, "import_to_blender.py"), script);
    }
}
