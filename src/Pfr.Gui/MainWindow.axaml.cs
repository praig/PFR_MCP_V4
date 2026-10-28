using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Pfr.Core;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
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
    /// <param name="templatesFolder">Where report templates (plain JSON files) live; next to the application by default.</param>
    public MainWindow(string dataFolder, int kdfIterations = SecureStore.DefaultIterations, string? templatesFolder = null)
    {
        InitializeComponent();

        BannerText.Text = AppInfo.Banner;
        _store = new SecureStore(dataFolder);

        Inventory = new InventoryView();
        Inventory.OpenRequested += async (_, _) => await PickAndLoadAsync();
        OpenButton.Click += async (_, _) => await PickAndLoadAsync();

        Clients = new ClientsView(_store, this, kdfIterations);
        Reports = new ReportsView(new ReportTemplateStore(TemplateLocation.Resolve(templatesFolder)));
        Clients.StateChanged += async (_, _) => await OnStoreStateChangedAsync();
        Clients.DataChanged += (_, _) => RefreshPickers();

        GridPicker.SelectionChanged += async (_, _) =>
        {
            if (_updatingPickers || GridPicker.SelectedItem is not GridChoice choice) return;
            await Clients.OpenGridAsync(choice.ClientId, choice.GridId);
        };
        SaveToClientButton.Click += (_, _) => SaveLoadedToClient();
        RefreshPickers();

        foreach (var (module, title, summary) in ModuleCatalog.All)
        {
            ModuleTabs.Items.Add(new TabItem
            {
                Header = title,
                Content = module switch
                {
                    AppModule.Inventory => Inventory,
                    AppModule.ClientWorkspace => Clients,
                    AppModule.Reports => Reports,
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

    public ReportsView Reports { get; }

    public GridInventory? LoadedInventory { get; private set; }

    public LoadedSource? LoadedSource { get; private set; }

    /// <summary>Client, grid and snapshot shown in Inventory, when opened from a client.</summary>
    public (Client Client, ClientGrid Grid, SnapshotInfo Snapshot)? LoadedSnapshot { get; private set; }

    private bool _updatingPickers;

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
                // A grid without a name can't be identified, so it isn't loaded at all.
                BackupParser.EnsureGridName(inv, name);
                var src = new LoadedSource(name, new FileInfo(path).Length, ClientWorkspace.Sha256Of(path));
                return (inv, src);
            });

            ShowLoaded(inventory, source, snapshot: null);
            RefreshPickers();
            LoadedText.Text = $"{inventory.GridName}  ·  NIOS {ShortVersion(inventory.NiosVersion)}  ·  {name} (not saved)";
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

    /// <summary>Shows a client's stored snapshot in Inventory and remembers it for next time.</summary>
    public void ShowSnapshot(Client client, ClientGrid grid, SnapshotInfo snapshot, GridInventory inventory,
        bool switchToInventory)
    {
        ShowLoaded(inventory,
            new LoadedSource(snapshot.SourceFileName, snapshot.SourceBytes, snapshot.SourceSha256),
            (client, grid, snapshot));
        var when = snapshot.ImportedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        LoadedText.Text = $"NIOS {ShortVersion(inventory.NiosVersion)}  ·  snapshot {when}" +
                          (snapshot.Label.Length > 0 ? $"  ·  {snapshot.Label}" : "");
        Title = $"{client.Name} › {grid.Name} — {AppInfo.ProductName}";
        StatusText.Text = $"Opened {client.Name} › {grid.Name} (snapshot {when}): {Counts(inventory)}";

        try
        {
            Clients.Workspace?.RememberLastViewed(client.Id, grid.Id, snapshot.Id);
        }
        catch (IOException)
        {
            // Remembering is a convenience; never block viewing on it.
        }

        RefreshPickers();
        if (switchToInventory) ShowModule(AppModule.Inventory);
    }

    private void ShowLoaded(GridInventory inventory, LoadedSource source, (Client, ClientGrid, SnapshotInfo)? snapshot)
    {
        LoadedInventory = inventory;
        LoadedSource = source;
        LoadedSnapshot = snapshot;
        Inventory.ShowInventory(inventory);
        Reports.ShowInventory(inventory, snapshot is { } x
            ? $"{x.Item1.Name} › {x.Item2.Name}  (snapshot {x.Item3.ImportedUtc.ToLocalTime():yyyy-MM-dd HH:mm})"
            : $"{inventory.GridName}  ({source.FileName}, not saved to a client)");
        UpdateSaveBar();
    }

    // ---- Client › grid switcher and save bar --------------------------------

    /// <summary>After unlocking, reopen the grid that was open last time (without leaving Clients).</summary>
    private async Task OnStoreStateChangedAsync()
    {
        // Locking hides client data everywhere, including a client grid shown in Inventory.
        if (Clients.Workspace is null && LoadedSnapshot is not null)
        {
            LoadedInventory = null;
            LoadedSource = null;
            LoadedSnapshot = null;
            Inventory.ShowEmpty("Client data is locked. Unlock Clients to open a grid again.");
            Reports.ShowInventory(null, "");
            LoadedText.Text = "";
            Title = AppInfo.ProductName;
        }

        RefreshPickers();
        if (Clients.Workspace?.LastViewed() is { } last && LoadedInventory is null)
        {
            // Opens that grid's latest snapshot, the same as switching to it.
            await Clients.OpenGridAsync(last.Client.Id, last.Grid.Id, switchToInventory: false);
            StatusText.Text = $"Reopened {last.Client.Name} › {last.Grid.Name}. " + StatusText.Text;
        }
    }

    /// <summary>Fills the header switcher and the save bar's client list from the store.</summary>
    public void RefreshPickers()
    {
        _updatingPickers = true;
        try
        {
            var clients = Clients.Workspace?.ListClients() ?? [];
            var choices = clients
                .SelectMany(c => c.Grids.Where(g => g.Snapshots.Count > 0).Select(g => new GridChoice(c.Id, g.Id, $"{c.Name} › {g.Name}")))
                .OrderBy(c => c.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();
            GridPicker.ItemsSource = choices;
            GridPicker.IsEnabled = Clients.Workspace is not null && choices.Count > 0;
            GridPicker.PlaceholderText = Clients.Workspace is null
                ? "Unlock Clients to switch grids"
                : choices.Count == 0 ? "No saved grids yet" : "Switch client › grid";
            GridPicker.SelectedItem = LoadedSnapshot is { } open
                ? choices.FirstOrDefault(c => c.ClientId == open.Client.Id && c.GridId == open.Grid.Id)
                : null;

            var previous = (SaveClientPicker.SelectedItem as ClientChoice)?.Id;
            var clientChoices = clients.Select(c => new ClientChoice(c.Id, c.Name)).ToList();
            SaveClientPicker.ItemsSource = clientChoices;
            SaveClientPicker.SelectedItem = clientChoices.FirstOrDefault(c => c.Id == previous)
                                            ?? (clientChoices.Count == 1 ? clientChoices[0] : null);
        }
        finally
        {
            _updatingPickers = false;
        }
        UpdateSaveBar();
    }

    private void UpdateSaveBar()
    {
        SaveBar.IsVisible = LoadedInventory is not null && LoadedSnapshot is null;
        if (!SaveBar.IsVisible) return;

        var unlocked = Clients.Workspace is not null;
        var hasClients = SaveClientPicker.ItemCount > 0;
        SaveClientPicker.IsVisible = SaveToClientButton.IsVisible = unlocked && hasClients;
        SaveBarText.Text = !unlocked
            ? $"{LoadedInventory!.GridName} isn't saved to a client. Unlock Clients to save it."
            : !hasClients
                ? $"{LoadedInventory!.GridName} isn't saved to a client. Create a client in Clients to save it."
                : $"{LoadedInventory!.GridName} isn't saved to a client yet.";
    }

    /// <summary>Saves the backup open in Inventory to the chosen client, as a snapshot of its grid.</summary>
    public void SaveLoadedToClient(string? clientId = null)
    {
        var workspace = Clients.Workspace;
        clientId ??= (SaveClientPicker.SelectedItem as ClientChoice)?.Id;
        if (workspace is null || LoadedInventory is not { } inventory || LoadedSource is not { } source) return;
        if (clientId is null)
        {
            StatusText.Text = "Choose a client to save this grid to.";
            return;
        }

        try
        {
            var (grid, info) = workspace.AddSnapshot(clientId, inventory, source.FileName, source.Bytes, source.Sha256);
            var client = workspace.GetClient(clientId);
            Clients.RefreshClients(clientId);
            ShowSnapshot(client, client.FindGrid(grid.Id)!, info, inventory, switchToInventory: false);
            StatusText.Text = $"Saved {source.FileName} to {client.Name} › {grid.Name}.";
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
        {
            StatusText.Text = $"Could not save: {ex.Message}";
        }
    }

    private sealed record GridChoice(string ClientId, string GridId, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record ClientChoice(string Id, string Name)
    {
        public override string ToString() => Name;
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
