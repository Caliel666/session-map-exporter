using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Forms;
using SessionMapExporter.Core;

namespace SessionMapExporter;

/// <summary>One row of the map selector (a consolidated map, or a raw world).</summary>
public sealed class MapItem
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public MapGroup? Group { get; init; }
    public MapEntry? World { get; init; }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MapItem> _items = new();
    private readonly ArchiveService _service = new();
    private List<MapGroup> _groups = new();
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        MapList.ItemsSource = _items;
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Select your Session Skate Sim installation folder" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            GameFolderBox.Text = dlg.SelectedPath;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Select export output folder" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputBox.Text = dlg.SelectedPath;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ExportButton.IsEnabled = false;
            _items.Clear();
            _groups.Clear();
            StatusText.Text = "Mounting archives…";

            var root = GameFolderBox.Text.Trim();
            var key = AesKeyBox.Password.Trim();

            var pakDir = ArchiveService.FindPakDirectory(root);
            if (pakDir is null)
                throw new DirectoryNotFoundException("Could not find SessionGame\\Content\\Paks under the selected folder.");

            await Task.Run(() => _service.Initialize(pakDir, key));

            var found = await Task.Run(() => _service.FindWorlds(new Progress<string>(s => Dispatcher.Invoke(() => StatusText.Text = s))));

            _groups = MapGrouping.Group(found);
            RebuildList();

            StatusText.Text = _groups.Count == 0
                ? "No .umap worlds were found."
                : "Ready — pick one or more maps and export. Each export lands in Maps/<map name>/ with a build_map.py that produces a .glb for Blender.";

            ExportButton.IsEnabled = _groups.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed.";
            System.Windows.MessageBox.Show(ex.ToString(), "Session Map Exporter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RebuildList()
    {
        _items.Clear();
        var worldCount = _groups.Sum(g => g.Worlds.Count);

        if (WorldsModeBox.IsChecked == true)
        {
            foreach (var group in _groups)
            {
                foreach (var world in group.Worlds)
                {
                    _items.Add(new MapItem
                    {
                        Title = Path.GetFileNameWithoutExtension(world.Path.Replace('\\', '/')),
                        Detail = world.Path,
                        Group = group,
                        World = world,
                    });
                }
            }
            MapCountText.Text = $"{_groups.Count} maps · {worldCount} worlds (individual view)";
        }
        else
        {
            foreach (var group in _groups)
            {
                _items.Add(new MapItem
                {
                    Title = group.DisplayName,
                    Detail = $"{group.Worlds.Count} world{(group.Worlds.Count == 1 ? "" : "s")} · exports {group.PrimaryName}",
                    Group = group,
                });
            }
            MapCountText.Text = $"{_groups.Count} maps ({worldCount} worlds consolidated)";
        }
    }

    private void Mode_Changed(object sender, RoutedEventArgs e) => RebuildList();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => MapList.SelectAll();

    private void Clear_Click(object sender, RoutedEventArgs e) => MapList.UnselectAll();

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MapList.ItemsSource is not ObservableCollection<MapItem> items) return;
        var view = CollectionViewSource.GetDefaultView(items);
        var q = FilterBox.Text.Trim();
        view.Filter = q.Length == 0
            ? null
            : item =>
            {
                var map = (MapItem)item;
                if (map.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
                if (map.Group is null) return map.Detail.Contains(q, StringComparison.OrdinalIgnoreCase);

                // in map view, a group matches when the map name or any of its
                // worlds matches - so users can search for a specific level name
                return map.Group.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || map.Group.Worlds.Any(w => w.Path.Contains(q, StringComparison.OrdinalIgnoreCase));
            };
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_service.Provider is null)
        {
            System.Windows.MessageBox.Show("Scan the game first.");
            return;
        }

        var selected = MapList.SelectedItems.Cast<MapItem>().ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("Select at least one map.");
            return;
        }

        var requests = BuildRequests(selected);
        if (requests.Count == 0)
        {
            System.Windows.MessageBox.Show("Nothing to export.");
            return;
        }

        var output = OutputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(output))
            output = Path.Combine(AppContext.BaseDirectory, "Exports");

        Directory.CreateDirectory(output);

        try
        {
            ExportButton.IsEnabled = false;
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(s => StatusText.Text = s);

            await _service.ExportWorldsAsync(
                requests, output,
                ExportTexturesBox.IsChecked == true,
                ExportMaterialsBox.IsChecked == true,
                NoLightingBox.IsChecked == true,
                progress,
                _cts.Token);

            var outPath = Path.GetFullPath(output);
            StatusText.Text = $"Finished exporting {requests.Count} map(s) to {outPath}. Run 'python build_map.py' there to get .glb files for Blender.";
            System.Windows.MessageBox.Show(
                $"Export complete.\n\nOutput: {outPath}\n\nNext step:\n  1. Open a terminal in the output folder.\n  2. Run:  python build_map.py\n  3. In Blender: File -> Import -> glTF 2.0 -> pick the .glb",
                "Session Map Exporter",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Export cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed.";
            System.Windows.MessageBox.Show(ex.ToString(), "Session Map Exporter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            ExportButton.IsEnabled = true;
        }
    }

    /// <summary>Turns selected rows into concrete export jobs.</summary>
    private List<ExportRequest> BuildRequests(List<MapItem> selected)
    {
        var requests = new List<ExportRequest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (WorldsModeBox.IsChecked == true)
        {
            foreach (var item in selected.Where(i => i.World is not null))
            {
                var world = item.World!;
                var group = item.Group;
                // individual worlds still live inside their map's folder
                var folder = group is null
                    ? MapGrouping.Sanitize(Path.GetFileNameWithoutExtension(world.Path.Replace('\\', '/')))
                    : Path.Combine(group.FolderName,
                        MapGrouping.Sanitize(Path.GetFileNameWithoutExtension(world.Path.Replace('\\', '/'))));

                if (seen.Add(world.Path))
                    requests.Add(new ExportRequest(world, folder, Path.GetFileName(folder)));
            }
        }
        else
        {
            foreach (var item in selected.Where(i => i.Group is not null))
            {
                var group = item.Group!;
                if (seen.Add(group.Key))
                    requests.Add(new ExportRequest(group.Primary, group.FolderName, group.DisplayName));
            }
        }

        return requests;
    }
}
