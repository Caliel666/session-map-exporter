namespace SessionMapExporter.Core;

public sealed record MapEntry(string Path)
{
    public string DisplayName =>
        Path.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? Path;
}
