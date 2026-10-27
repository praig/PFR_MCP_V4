using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Pfr.Core;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Storage;
using Pfr.Gui.Views;

namespace Pfr.Gui;

public partial class MainWindow : Window, IInventoryHost
{
    private static readonly FilePickerFileType BackupFiles = new("NIOS grid backup")
    {
        Patterns = ["*.bak", "*.tar.gz", "*.tgz", "*.tar", "*.xml"],
    };

    private readonly SecureStore _store;

    /// <summary>Used by the designer and the app: client data next to the application.</summary>
    public MainWindow() : this(DataLocation.Resolve())
    {
    }

    /// <param name="dataFolder">Where encrypted client data lives.</param>
    /// <param name="kdfIterations">PBKDF2 iterations for a new store (tests use fewer).</param>
    public MainWindow(string dataFolder, int kdfIterations = SecureStore.DefaultIterations)
    {
        InitializeComponent();

        BannerText.Text = AppInfo.Banner;
        _store = new SecureStore(dataFolder);

        Inventory = new InventoryView();
        Inventory.OpenRequested += async (_, _) => await PickAndLoadAsync();
        OpenButton.Click += async (_, _) => await PickAndLoadAsync();

        Clients = new ClientsView(_store, this, kdfIterations);

        foreach (var (module, title, summary) in ModuleCatalog.All)
        {
            ModuleTabs.Items.Add(new TabItem
            {
                Header = title,
                Content = module switch
                {
                    AppModule.Inventory => Inventory,
                    AppModule.ClientWorkspace => Clients,
                    _ => Placeholder(summary),
                },
            });
        }

        // Start on Clients: work is organised per client.
        ShowModule(AppModule.ClientWorkspace);

        // Closing the app forgets the key; data stays encrypted on disk.
        Closed += (_, _) => _store.Dispose();
    }

    public InventoryView Inventory { get; }

    public ClientsView Clients { get; }

    public GridInventory? LoadedInventory { get; private set; }

    public LoadedSource? LoadedSource { get; private set; }

    /// <summary>Client and snapshot shown in Inventory, when opened from a client.</summary>
    public (Client Client, SnapshotInfo Snapshot)? LoadedSnapshot { get; private set; }

    public string StatusMessage => StatusText.Text ?? "";

    public string HeaderText => LoadedText.Text ?? "";

    public void SetStatus(string text) => StatusText.Text = text;

    public void ShowModule(AppModule module) =>
        ModuleTabs.SelectedIndex = ModuleCatalog.All.ToList().FindIndex(m => m.Module == module);

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
            var (inventory, source) = await Task.Run(() =>
            {
                var inv = BackupParser.ParseFile(path);
                var src = new LoadedSource(name, new FileInfo(path).Length, ClientWorkspace.Sha256Of(path));
                return (inv, src);
            });

            ShowLoaded(inventory, source, snapshot: null);
            LoadedText.Text = $"{inventory.GridName ?? "Grid"}  ·  NIOS {ShortVersion(inventory.NiosVersion)}  ·  {name}";
            Title = $"{inventory.GridName ?? name} — {AppInfo.ProductName}";
            StatusText.Text = $"Loaded {name} in {watch.Elapsed.TotalSeconds:0.0}s: {Counts(inventory)}";
            ShowModule(AppModule.Inventory);
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

    /// <summary>Shows a client's stored snapshot in Inventory.</summary>
    public void ShowSnapshot(Client client, SnapshotInfo snapshot, GridInventory inventory)
    {
        ShowLoaded(inventory,
            new LoadedSource(snapshot.SourceFileName, snapshot.SourceBytes, snapshot.SourceSha256),
            (client, snapshot));
        var when = snapshot.ImportedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        LoadedText.Text = $"{client.Name}  ·  {inventory.GridName ?? "Grid"}  ·  NIOS {ShortVersion(inventory.NiosVersion)}  ·  snapshot {when}";
        Title = $"{client.Name} — {inventory.GridName ?? "Grid"} — {AppInfo.ProductName}";
        StatusText.Text = $"Opened {client.Name} snapshot from {when}: {Counts(inventory)}";
        ShowModule(AppModule.Inventory);
    }

    private void ShowLoaded(GridInventory inventory, LoadedSource source, (Client, SnapshotInfo)? snapshot)
    {
        LoadedInventory = inventory;
        LoadedSource = source;
        LoadedSnapshot = snapshot;
        Inventory.ShowInventory(inventory);
    }

    private static string Counts(GridInventory inv) =>
        $"{inv.Members.Count} members, {inv.Zones.Count} zones, " +
        $"{inv.Networks.Count(n => !n.IsContainer)} networks, " +
        $"{inv.DhcpRanges.Count} DHCP ranges, {inv.Records.Count} records";

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
