namespace SessionMapExporter.Core;

public sealed record MapEntry(string Path, int StreamingLevels = 0)
{
    public string DisplayName
    {
        get
        {
            var normalized = Path.Replace('\\', '/').Trim('/');
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => Path,
                1 => System.IO.Path.GetFileNameWithoutExtension(parts[0]),
                _ => $"{System.IO.Path.GetFileNameWithoutExtension(parts[^1])}  [{string.Join("/", parts[..^1])}]"
            };
        }
    }

    public string RelativeDisplayPath =>
        System.IO.Path.ChangeExtension(Path.Replace('\\', '/'), null) ?? Path;
}
