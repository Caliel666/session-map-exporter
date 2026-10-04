using System.IO;
using System.Text.Json;
using CUE4Parse;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
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
            .Select(x => x.Path.Replace('\', '/'))
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

        var summary = new List<object>();
        var mapsRoot = Path.Combine(outputRoot, "Maps");
        Directory.CreateDirectory(mapsRoot);

        foreach (var map in maps)
        {
            ct.ThrowIfCancellationRequested();

            // Preserve the package hierarchy so identically named worlds cannot overwrite
            // one another. The WorldExporter itself consolidates streaming levels into
            // the selected persistent world via USD subLayers.
            var relative = map.Path.Replace('/', Path.DirectorySeparatorChar);
            relative = Path.ChangeExtension(relative, null) ?? relative;
            var mapOut = Path.Combine(mapsRoot, SanitizeRelativePath(relative));
            Directory.CreateDirectory(mapOut);

            progress?.Report($"Loading world {map.DisplayName}…");

            var package = Provider.LoadPackage(map.Path);
            var world = package.GetExports()
                .FirstOrDefault(x => x.GetType().Name.Equals("UWorld", StringComparison.Ordinal));

            if (world is null)
            {
                summary.Add(new { map.Path, success = false, error = "UWorld export could not be resolved." });
                continue;
            }

            var session = new ExportSession
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            };

            session.Add(world);
            var options = BuildExportOptions(exportTextures, exportMaterials);

            progress?.Report($"Exporting {map.DisplayName} as USD…");
            var results = await session.RunAsync(mapOut, options, null, ct);

            if (noGameLighting)
                StripUsdLights(mapOut);

            var manifest = new
            {
                map.Path,
                map.DisplayName,
                exportedAtUtc = DateTime.UtcNow,
                format = "USD / USDA",
                persistentWorldOnly = true,
                streamingLevelsConsolidatedByWorldExporter = true,
                gameLightingExcluded = noGameLighting,
                gameLightmapDataImported = false,
                textureExportRequested = exportTextures,
                materialExportRequested = exportMaterials,
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

            WriteBlenderHelper(mapOut);

            summary.Add(new
            {
                map.Path,
                map.DisplayName,
                success = results.Any(r => r.Success),
                resultCount = results.Count,
                outputDirectory = mapOut
            });
        }

        await File.WriteAllTextAsync(
            Path.Combine(mapsRoot, "maps-export-summary.json"),
            JsonSerializer.Serialize(new
            {
                exportedAtUtc = DateTime.UtcNow,
                mapCount = maps.Count,
                maps = summary
            }, new JsonSerializerOptions { WriteIndented = true }),
            ct);
    }

    private static ExportOptions BuildExportOptions(bool textures, bool materials)
    {
        // ExportOptions uses MeshFormat (not MeshExportFormat). WorldExporter currently
        // supports USD only; selecting UEFormat here produces the exact exception this
        // application previously hit.
        var type = typeof(ExportOptions);
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor is null)
            throw new InvalidOperationException("CUE4Parse-Conversion did not expose an ExportOptions constructor.");

        var args = ctor.GetParameters()
            .Select(p => DefaultForParameter(p.ParameterType, p.Name ?? ""))
            .ToArray();

        var instance = (ExportOptions)ctor.Invoke(args);

        SetEnumProperty(instance, "MeshFormat", "USD");
        SetEnumProperty(instance, "TextureExportFormat", "Png", "PNG");
        SetEnumProperty(instance, "MaterialExportFormat", materials ? "AllLayersNoRef" : "TopLayerOnly");
        SetBoolProperty(instance, "SaveEmbeddedMaterials", materials);
        SetBoolProperty(instance, "ExportMaterials", materials);
        SetBoolProperty(instance, "ExportAllTextureMips", true);
        SetBoolProperty(instance, "SaveHdrTexturesAsHdr", false);
        return instance;
    }

    private static object? DefaultForParameter(Type t, string name)
    {
        if (t.IsEnum)
        {
            var preferred = name.Contains("mesh", StringComparison.OrdinalIgnoreCase)
                ? new[] { "USD", "Gltf", "GLTF", "glTF", "Fbx", "FBX", "UEFormat" }
                : name.Contains("texture", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "Png", "PNG" }
                    : name.Contains("material", StringComparison.OrdinalIgnoreCase)
                        ? new[] { "TopLayerOnly", "AllLayersNoRef" }
                        : new[] { "None" };

            foreach (var candidate in preferred)
                if (Enum.GetNames(t).Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    return Enum.Parse(t, candidate, true);

            return Enum.GetValues(t).GetValue(0);
        }

        if (t == typeof(bool))
            return name.Contains("SaveEmbedded", StringComparison.OrdinalIgnoreCase);
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
            var match = Enum.GetNames(p.PropertyType)
                .FirstOrDefault(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

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

    private static void StripUsdLights(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.usda", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var stripped = RemoveUsdLightPrims(text);
            if (!ReferenceEquals(text, stripped) && !text.Equals(stripped, StringComparison.Ordinal))
                File.WriteAllText(file, stripped);
        }
    }

    // WorldExporter serializes light components as USD light prims. Remove those prim
    // blocks from the ASCII stage when the user explicitly requested fresh Blender
    // lighting. This also handles sublayer files produced for streaming worlds.
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

    private static void WriteBlenderHelper(string mapOut)
    {
        var script = """
import bpy
from pathlib import Path

ROOT = Path(__file__).resolve().parent

# The exporter emits a USD world plus referenced mesh/material/texture assets.
# Import the world stages first so streaming sublayers resolve as one scene.
worlds = sorted(ROOT.rglob("*.usda"))
for path in worlds:
    try:
        bpy.ops.wm.usd_import(filepath=str(path))
    except Exception as exc:
        print("SessionMapExporter: USD import failed:", path, exc)

# The USD world has already had Unreal light prims removed when the option is enabled.
# Remove any imported light objects defensively as well.
for obj in list(bpy.data.objects):
    if obj.type == 'LIGHT':
        bpy.data.objects.remove(obj, do_unlink=True)

print("SessionMapExporter: world import pass complete.")
""";
        File.WriteAllText(Path.Combine(mapOut, "import_to_blender.py"), script);
    }
}
