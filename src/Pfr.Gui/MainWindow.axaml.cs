using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Pfr.Core;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Gui.Views;

namespace Pfr.Gui;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType BackupFiles = new("NIOS grid backup")
    {
        Patterns = ["*.bak", "*.tar.gz", "*.tgz", "*.tar", "*.xml"],
    };

    public MainWindow()
    {
        InitializeComponent();

        BannerText.Text = AppInfo.Banner;
        Inventory = new InventoryView();
        Inventory.OpenRequested += async (_, _) => await PickAndLoadAsync();
        OpenButton.Click += async (_, _) => await PickAndLoadAsync();

        foreach (var (module, title, summary) in ModuleCatalog.All)
        {
            ModuleTabs.Items.Add(new TabItem
            {
                Header = title,
                Content = module == AppModule.Inventory ? Inventory : Placeholder(summary),
            });
        }

        // Open on the Inventory tab, the only module built so far.
        ModuleTabs.SelectedIndex = ModuleCatalog.All.ToList().FindIndex(m => m.Module == AppModule.Inventory);
    }

    public InventoryView Inventory { get; }

    public GridInventory? LoadedInventory { get; private set; }

    public string StatusMessage => StatusText.Text ?? "";

    private static Control Placeholder(string summary) => new TextBlock
    {
        Text = summary + "\n\nComing in a later version.",
        Margin = new Thickness(24),
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Top,
        Opacity = 0.75,
    };

    private async Task PickAndLoadAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open NIOS grid backup",
            AllowMultiple = false,
            FileTypeFilter = [BackupFiles, FilePickerFileTypes.All],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await LoadBackupAsync(path);
        }
    }

    /// <summary>Parses a backup off the UI thread and shows its inventory.</summary>
    public async Task LoadBackupAsync(string path)
    {
        var name = Path.GetFileName(path);
        StatusText.Text = $"Reading {name}…";
        OpenButton.IsEnabled = false;
        try
        {
            var watch = Stopwatch.StartNew();
            var inventory = await Task.Run(() => BackupParser.ParseFile(path));
            LoadedInventory = inventory;
            Inventory.ShowInventory(inventory);

            LoadedText.Text = $"{inventory.GridName ?? "Grid"}  ·  NIOS {ShortVersion(inventory.NiosVersion)}  ·  {name}";
            Title = $"{inventory.GridName ?? name} — {AppInfo.ProductName}";
            StatusText.Text = $"Loaded {name} in {watch.Elapsed.TotalSeconds:0.0}s: " +
                              $"{inventory.Members.Count} members, {inventory.Zones.Count} zones, " +
                              $"{inventory.Networks.Count(n => !n.IsContainer)} networks, " +
                              $"{inventory.DhcpRanges.Count} DHCP ranges, {inventory.Records.Count} records";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Xml.XmlException)
        {
            ShowLoadError(name, ex.Message);
        }
        catch (Exception ex)
        {
            // Never let a load problem close the app; keep whatever was showing before.
            ShowLoadError(name, $"unexpected error ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            OpenButton.IsEnabled = true;
        }
    }

    private void ShowLoadError(string fileName, string message)
    {
        StatusText.Text = $"Could not read {fileName}: {message}";
        if (LoadedInventory is null)
        {
            Inventory.ShowEmpty($"Could not read {fileName}. {message}");
        }
    }

    /// <summary>Shows an error that escaped a handler in the status bar.</summary>
    public void ReportUnexpectedError(Exception ex) =>
        StatusText.Text = $"Something went wrong ({ex.GetType().Name}): {ex.Message}";

    /// <summary>"9.0.8-54134-924d94e0dcd0" → "9.0.8".</summary>
    private static string ShortVersion(string? version) =>
        version is null ? "?" : version.Split('-')[0];
}
