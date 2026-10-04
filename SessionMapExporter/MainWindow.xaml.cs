using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Forms;
using SessionMapExporter.Core;

namespace SessionMapExporter;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MapEntry> _maps = new();
    private readonly ArchiveService _service = new();
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        MapList.ItemsSource = _maps;
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
            _maps.Clear();
            StatusText.Text = "Mounting archives…";

            var root = GameFolderBox.Text.Trim();
            var key = AesKeyBox.Password.Trim();

            var pakDir = ArchiveService.FindPakDirectory(root);
            if (pakDir is null)
                throw new DirectoryNotFoundException("Could not find SessionGame\\Content\\Paks under the selected folder.");

            await Task.Run(() => _service.Initialize(pakDir, key));

            var found = await Task.Run(() => _service.FindWorlds(new Progress<string>(s => Dispatcher.Invoke(() => StatusText.Text = s))));
            foreach (var map in found)
                _maps.Add(map);

            MapCountText.Text = $"{_maps.Count} actual worlds found";
            StatusText.Text = _maps.Count == 0
                ? "No .umap worlds were found."
                : "Ready — select one, several, or all worlds.";

            ExportButton.IsEnabled = _maps.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed.";
            System.Windows.MessageBox.Show(ex.ToString(), "Session Map Exporter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        MapList.SelectAll();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        MapList.UnselectAll();
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MapList.ItemsSource is not ObservableCollection<MapEntry> items) return;
        var view = CollectionViewSource.GetDefaultView(items);
        var q = FilterBox.Text.Trim();
        view.Filter = q.Length == 0
            ? null
            : item => ((MapEntry)item).DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || ((MapEntry)item).Path.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_service.Provider is null)
        {
            System.Windows.MessageBox.Show("Scan the game first.");
            return;
        }

        var selected = MapList.SelectedItems.Cast<MapEntry>().ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("Select at least one map.");
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
                selected, output,
                ExportTexturesBox.IsChecked == true,
                ExportMaterialsBox.IsChecked == true,
                NoLightingBox.IsChecked == true,
                progress,
                _cts.Token);

            StatusText.Text = $"Finished exporting {selected.Count} map(s).";
            System.Windows.MessageBox.Show(
                $"Export complete.\\n\\nOutput: {Path.GetFullPath(output)}",
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
}
