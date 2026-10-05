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

/// <summary>One export job: a world plus the folder it should end up in.</summary>
public sealed record ExportRequest(MapEntry World, string FolderName, string DisplayName);

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

        // one builder script + short readme per export run, next to Maps/
        WriteBuilderScript(outputRoot);
        WriteReadme(outputRoot);

        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();

            // Clean layout: Maps/<Map Name>/source  - no more deep package trees.
            var mapOut = Path.Combine(mapsRoot, UniqueFolderName(request.FolderName, usedFolders));
            var sourceOut = Path.Combine(mapOut, "source");
            Directory.CreateDirectory(sourceOut);

            progress?.Report($"Loading world {request.World.DisplayName}…");

            var package = Provider.LoadPackage(request.World.Path);
            var world = package.GetExports()
                .FirstOrDefault(x => x.GetType().Name.Equals("UWorld", StringComparison.Ordinal));

            if (world is null)
            {
                summary.Add(new { world = request.World.Path, success = false, error = "UWorld export could not be resolved." });
                continue;
            }

            var session = new ExportSession
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            };

            session.Add(world);
            var options = BuildExportOptions(exportTextures, exportMaterials);

            progress?.Report($"Exporting {request.DisplayName} as USD…");
            var results = await session.RunAsync(sourceOut, options, null, ct);

            if (noGameLighting)
                StripUsdLights(sourceOut);

            var sourceWorlds = results
                .Where(r => r.Success && r.DiskFilePaths is not null)
                .SelectMany(r => r.DiskFilePaths!)
                .Where(p => p.EndsWith(".usda", StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetRelativePath(mapOut, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var manifest = new
            {
                map = request.World.Path,
                displayName = request.DisplayName,
                format = "USD / USDA",
                builder = "run  python build_map.py  (see README.txt)  to produce the .glb",
                exportedAtUtc = DateTime.UtcNow,
                persistentWorldOnly = true,
                streamingLevelsConsolidatedByWorldExporter = true,
                gameLightingExcluded = noGameLighting,
                textureExportRequested = exportTextures,
                materialExportRequested = exportMaterials,
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
                world = request.World.Path,
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
        // NOTE: must stay false. USD material files reference "<Texture>.png" without a
        // mip suffix; with all-mips enabled the exporter writes T_Name_MIP0.png etc. and
        // every material silently lost its textures. (build_map.py tolerates old
        // _MIP* folders, but fresh exports no longer produce them.)
        SetBoolProperty(instance, "ExportAllTextureMips", false);
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

    /// <summary>
    /// Writes the single build_map.py helper (and a short README) into the export
    /// root. The script converts each exported map into one clean .glb with
    /// embedded textures; it only needs regular Python - no Blender install.
    /// </summary>
    private static void WriteBuilderScript(string outputRoot)
    {
        File.WriteAllText(Path.Combine(outputRoot, "build_map.py"), BuildMapScript.Content);
    }

    private static void WriteReadme(string outputRoot)
    {
        var readme = """
            HOW TO GET THIS MAP INTO BLENDER
            ================================

            1. Install Python 3.8+ from python.org (or use the one you have).

            2. Open a terminal in THIS folder and run:

                   python build_map.py

               That converts every map below Maps/ into a single .glb file with
               all textures embedded. To build just one map:

                   python build_map.py "Maps/<map name>"

            3. In Blender:  File -> Import -> glTF 2.0  and pick
               Maps/<map name>/<map name>.glb

            Optional (better materials, smaller textures):
                pip install pillow
                python build_map.py --max-texture-size 2048

            Folder layout
            -------------
              Maps/<Map Name>/source/          raw USD export (keep, it is the master copy)
              Maps/<Map Name>/<Map Name>.glb   the model you import into Blender
              Maps/<Map Name>/build-report.json what was built / skipped / missing
              build_map.py                     the converter (safe to copy elsewhere)
            """;
        File.WriteAllText(Path.Combine(outputRoot, "README.txt"), readme);
    }
}
