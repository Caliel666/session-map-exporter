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

            ExportSession? session = null;
            session = new ExportSession((args, filterCt) =>
            {
                // WorldExporter writes streaming-level references, but its built-in
                // queueing only follows levels marked persistent. Session uses a large
                // streamed world, so explicitly queue every referenced streaming world.
                foreach (var level in args.StreamingLevels)
                {
                    filterCt.ThrowIfCancellationRequested();
                    session.Add(level.World);
                }
            })
            {
                MaxDegreeOfParallelism = 1
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
                        new[] { "Lowest" },
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

    private static void WriteBlenderHelper(string mapOut)
    {
        // Import only the top-level world stage. USD subLayers/references bring in
        // streaming levels and assets; importing every asset USD separately duplicates
        // the scene in Blender and can exhaust RAM.
        var worldFile = Directory.EnumerateFiles(mapOut, "*.usda", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        var worldLiteral = (worldFile ?? string.Empty)
            .Replace("\\", "/")
            .Replace("\"", "\\\"");

        var script =
            "import bpy\n" +
            "from pathlib import Path\n\n" +
            "ROOT = Path(__file__).resolve().parent\n" +
            "WORLD = Path(\"" + worldLiteral + "\")\n\n" +
            "if not WORLD.exists():\n" +
            "    raise FileNotFoundError(f\"World USD not found: {WORLD}\")\n\n" +
            "print(\"SessionMapExporter: importing persistent world:\", WORLD)\n" +
            "bpy.ops.wm.usd_import(filepath=str(WORLD))\n\n" +
            "# Game lighting is intentionally excluded from the exported stage.\n" +
            "for obj in list(bpy.data.objects):\n" +
            "    if obj.type == 'LIGHT':\n" +
            "        bpy.data.objects.remove(obj, do_unlink=True)\n\n" +
            "print(\"SessionMapExporter: world import complete.\")\n";

        File.WriteAllText(Path.Combine(mapOut, "import_to_blender.py"), script);
    }

