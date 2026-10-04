using System.Collections.Generic;
using System.Linq;

namespace SessionMapExporter.Core;

/// <summary>
/// One logical map, consolidating every world package that belongs to the same
/// game environment. Session keeps dozens of auxiliary worlds per map
/// (…_Art, _Audio, _Lights, _Vege, _Unmerged, …); this groups them so the
/// exporter shows one entry per playable map instead of 700+ packages.
/// </summary>
public sealed record MapGroup
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string FolderName { get; init; }
    public required IReadOnlyList<MapEntry> Worlds { get; init; }

    private MapEntry? _primary;

    /// <summary>The world that is exported when the group is selected.</summary>
    public MapEntry Primary => _primary ??= SelectPrimary(Key, Worlds);

    public string PrimaryName => System.IO.Path.GetFileNameWithoutExtension(Primary.Path.Replace('\\', '/'));

    public override string ToString() => DisplayName;

    /// <summary>
    /// Chooses the most likely "real" map inside a group:
    /// exact folder-name match, then …_P (Unreal persistent level),
    /// then …_Unmerged, then a name that ends with the folder name,
    /// then the shortest name (fewest auxiliary suffixes), then alphabetical.
    /// </summary>
    public static MapEntry SelectPrimary(string key, IReadOnlyList<MapEntry> worlds)
    {
        var folderName = key.Split('/')[^1];
        var ranked = worlds
            .Select(w =>
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(w.Path.Replace('\\', '/'));
                return (entry: w, name, score: Score(name, folderName));
            })
            .OrderBy(t => t.score)
            .ThenBy(t => t.name.Length)
            .ThenBy(t => t.name, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ranked[0].entry;
    }

    private static int Score(string name, string folderName)
    {
        if (name.Equals(folderName, System.StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.EndsWith("_P", System.StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.EndsWith("_Unmerged", System.StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.EndsWith(folderName, System.StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Contains(folderName, System.StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }
}

public static class MapGrouping
{
    private static readonly HashSet<string> LevelDirNames = new(System.StringComparer.OrdinalIgnoreCase)
    { "Levels", "Level", "Maps", "Map" };

    private static readonly HashSet<string> ContainerDirNames = new(System.StringComparer.OrdinalIgnoreCase)
    { "Content", "Game", "SessionGame" };

    /// <summary>Groups worlds into logical maps.</summary>
    public static List<MapGroup> Group(IEnumerable<MapEntry> worlds)
    {
        var byKey = new Dictionary<string, List<MapEntry>>(System.StringComparer.OrdinalIgnoreCase);
        var displayNames = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var world in worlds)
        {
            var (key, displayName) = GroupFor(world.Path);
            if (!byKey.TryGetValue(key, out var list))
            {
                list = new List<MapEntry>();
                byKey[key] = list;
                displayNames[key] = displayName;
            }
            list.Add(world);
        }

        return byKey
            .Select(kv => new MapGroup
            {
                Key = kv.Key,
                DisplayName = displayNames[kv.Key],
                FolderName = FolderNameFor(displayNames[kv.Key]),
                Worlds = kv.Value.OrderBy(w => w.Path, System.StringComparer.OrdinalIgnoreCase).ToList(),
            })
            .OrderBy(g => g.DisplayName, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Derives (groupKey, displayName) from a world package path.
    /// Game maps live under …/Art/Env/&lt;Region&gt;/&lt;MapName&gt;/Levels/…;
    /// the &lt;MapName&gt; folder is the logical map. Anything else falls back to
    /// the nearest meaningful parent directory.
    /// </summary>
    public static (string Key, string DisplayName) GroupFor(string worldPath)
    {
        var normalized = worldPath.Replace('\\', '/').Trim('/');
        var withoutExt = System.IO.Path.ChangeExtension(normalized, null) ?? normalized;
        var parts = withoutExt.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return ("_root", "Misc");

        // locate Art/Env/
        var envIdx = FindEnvIndex(parts);
        if (envIdx >= 0 && parts.Length > envIdx + 1)
        {
            var region = parts[envIdx + 1];
            if (parts.Length == envIdx + 2)
                return ($"env/{region}", Prettify(region));

            var mapFolder = parts[envIdx + 2];
            return ($"env/{region}/{mapFolder}", Prettify(mapFolder));
        }

        // generic fallback: nearest meaningful directory
        var dirParts = parts[..^1];
        if (dirParts.Length == 0)
            return ("_root/" + parts[^1], Prettify(parts[^1]));

        var leaf = dirParts[^1];
        if (LevelDirNames.Contains(leaf) && dirParts.Length >= 2 &&
            !ContainerDirNames.Contains(dirParts[^2]))
        {
            leaf = dirParts[^2];
        }

        // generic container folders ("Maps", "Content", …) are not useful map
        // names - a world sitting directly inside one is its own map
        if (LevelDirNames.Contains(leaf) || ContainerDirNames.Contains(leaf))
            return ("world/" + parts[^1], Prettify(parts[^1]));

        return ("dir/" + leaf, Prettify(leaf));
    }

    private static int FindEnvIndex(string[] parts)
    {
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i].Equals("Art", System.StringComparison.OrdinalIgnoreCase) &&
                parts[i + 1].Equals("Env", System.StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return -1;
    }

    private static string Prettify(string folder) =>
        folder.Replace('_', ' ').Replace('-', ' ').Trim();

    /// <summary>Sanitized output folder for a map group.</summary>
    public static string FolderNameFor(string displayName) => Sanitize(displayName);

    public static string Sanitize(string value)
    {
        var chars = value.Select(c =>
            System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim();
        if (cleaned.Length == 0) return "Map";
        return cleaned.Length > 80 ? cleaned[..80].TrimEnd() : cleaned;
    }
}
