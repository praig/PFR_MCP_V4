using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Model;
using Pfr.Core.Storage;
using Pfr.Gui.Controls;

namespace Pfr.Gui.Views;

/// <summary>Identifies the grid currently loaded in Inventory, so it can be saved to a client.</summary>
public sealed record LoadedSource(string FileName, long Bytes, string Sha256);

/// <summary>What the Clients tab needs from the main window.</summary>
public interface IInventoryHost
{
    GridInventory? LoadedInventory { get; }
    LoadedSource? LoadedSource { get; }

    /// <summary>Shows a stored snapshot in Inventory (optionally switching to the Inventory tab).</summary>
    void ShowSnapshot(Client client, ClientGrid grid, SnapshotInfo snapshot, GridInventory inventory, bool switchToInventory);

    void SetStatus(string text);
}

/// <summary>
/// Clients module: create or unlock the encrypted store, then manage clients,
/// their contacts, notes, snapshots and engagement log.
/// </summary>
public sealed class ClientsView : UserControl
{
    private readonly SecureStore _store;
    private readonly IInventoryHost _host;
    private readonly int _iterations;
    private ClientWorkspace? _workspace;

    private ListBox? _clientList;
    private ContentControl? _detail;

    public ClientsView(SecureStore store, IInventoryHost host, int iterations = SecureStore.DefaultIterations)
    {
        _store = store;
        _host = host;
        _iterations = iterations;
        ShowForState();
    }

    public ClientWorkspace? Workspace => _workspace;

    public IReadOnlyList<Client> Clients =>
        (_clientList?.ItemsSource as IEnumerable<object>)?.OfType<ClientItem>().Select(i => i.Client).ToList() ?? [];

    public ClientDetailView? CurrentDetail => _detail?.Content as ClientDetailView;

    /// <summary>Raised after unlocking, locking or creating the store.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when clients or grids are added, renamed or removed.</summary>
    public event EventHandler? DataChanged;

    public void ShowForState()
    {
        switch (_store.State)
        {
            case StoreState.NotInitialized: ShowSetup(); break;
            case StoreState.Locked: ShowUnlock(); break;
            default: ShowClients(); break;
        }
    }

    // ---- Setup and unlock -------------------------------------------------

    private void ShowSetup()
    {
        var first = Ui.Password("Master password");
        var second = Ui.Password("Repeat master password");
        var message = Ui.Message();
        var create = Ui.Accent("Create");
        var writable = DataLocation.CheckWritable(_store.Root);

        async void Submit()
        {
            if (first.Text != second.Text)
            {
                Ui.ShowError(message, "The passwords don't match.");
                return;
            }
            if ((first.Text ?? "").Length < SecureStore.MinimumPasswordLength)
            {
                Ui.ShowError(message, $"Use at least {SecureStore.MinimumPasswordLength} characters.");
                return;
            }

            create.IsEnabled = false;
            Ui.ShowInfo(message, "Creating encrypted store…");
            try
            {
                var password = first.Text!;
                await Task.Run(() => _store.Initialize(password, _iterations));
                first.Text = second.Text = "";
                _host.SetStatus("Client store created and unlocked.");
                ShowClients(); // creates the workspace, so listeners see it
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or InvalidOperationException)
            {
                Ui.ShowError(message, ex.Message);
                create.IsEnabled = true;
            }
        }

        create.Click += (_, _) => Submit();
        second.KeyDown += (_, e) => { if (e.Key == Key.Enter) Submit(); };

        var panel = Centered(
            Ui.Heading("Set up client data"),
            Ui.Muted("Client workspaces (contacts, notes, engagement log and grid snapshots) are stored " +
                     "encrypted next to the application, so the app and its data travel together."),
            Ui.Label("Data folder"),
            new SelectableTextBlock { Text = _store.Root, TextWrapping = TextWrapping.Wrap },
            Ui.Label($"Choose a master password (at least {SecureStore.MinimumPasswordLength} characters)"),
            first,
            new Border { Height = 8 },
            second,
            new TextBlock
            {
                Text = "If you forget this password the data cannot be recovered.",
                Foreground = Ui.Danger,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            },
            new Border { Height = 12 },
            create,
            message);

        if (writable is not null)
        {
            Ui.ShowError(message, writable);
            create.IsEnabled = false;
        }
        Content = panel;
    }

    private void ShowUnlock(string? notice = null)
    {
        var password = Ui.Password("Master password");
        var message = Ui.Message();
        var unlock = Ui.Accent("Unlock");
        if (notice is not null) Ui.ShowInfo(message, notice);

        async void Submit()
        {
            unlock.IsEnabled = false;
            Ui.ShowInfo(message, "Unlocking…");
            var text = password.Text ?? "";
            bool ok;
            try
            {
                ok = await Task.Run(() => _store.Unlock(text));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                           or FormatException)
            {
                Ui.ShowError(message, ex.Message);
                unlock.IsEnabled = true;
                return;
            }

            password.Text = "";
            if (!ok)
            {
                Ui.ShowError(message, "That password is not correct.");
                unlock.IsEnabled = true;
                password.Focus();
                return;
            }

            ShowClients(); // creates the workspace, so listeners see it
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        unlock.Click += (_, _) => Submit();
        password.KeyDown += (_, e) => { if (e.Key == Key.Enter) Submit(); };

        Content = Centered(
            Ui.Heading("Client data is locked"),
            Ui.Muted("Enter the master password to open client workspaces."),
            new Border { Height = 12 },
            password,
            new Border { Height = 12 },
            unlock,
            message);
        password.AttachedToVisualTree += (_, _) => password.Focus();
    }

    /// <summary>Unlocks programmatically (used by tests and the CLI-style flow).</summary>
    public bool TryUnlock(string password)
    {
        if (!_store.Unlock(password)) return false;
        ShowClients(); // creates the workspace, so listeners see it
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Lock()
    {
        _store.Lock();
        _workspace = null;
        _clientList = null;
        _detail = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
        ShowUnlock("Locked. Client data stays encrypted on disk.");
    }

    // ---- Client list ------------------------------------------------------

    private void ShowClients()
    {
        _workspace = new ClientWorkspace(_store);

        // Clients with their grids beneath them. Clicking a grid opens its latest snapshot.
        _clientList = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<object>((item, _) => ListRow(item), supportsRecycling: false),
        };
        _clientList.SelectionChanged += async (_, _) =>
        {
            if (_suppressSelection) return;
            switch (_clientList.SelectedItem)
            {
                case ClientItem c:
                    OpenClient(c.Client.Id);
                    break;
                case GridItem g:
                    OpenClient(g.Client.Id);
                    await OpenGridAsync(g.Client.Id, g.Grid.Id);
                    break;
            }
        };

        _detail = new ContentControl();

        var newName = new TextBox { Watermark = "New client name" };
        var add = Ui.Accent("Add");
        var message = Ui.Message();
        void AddClient()
        {
            try
            {
                var client = _workspace.CreateClient(newName.Text ?? "");
                newName.Text = "";
                message.IsVisible = false;
                RefreshClients(client.Id);
            }
            catch (ArgumentException ex)
            {
                Ui.ShowError(message, ex.Message);
            }
        }
        add.Click += (_, _) => AddClient();
        newName.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddClient(); };

        var lockButton = new Button { Content = "Lock", HorizontalAlignment = HorizontalAlignment.Right };
        lockButton.Click += (_, _) => Lock();

        var addRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        addRow.Children.Add(newName);
        Grid.SetColumn(add, 1);
        add.Margin = new Thickness(6, 0, 0, 0);
        addRow.Children.Add(add);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Ui.Heading("Clients", 18));
        Grid.SetColumn(lockButton, 1);
        header.Children.Add(lockButton);

        var top = new StackPanel { Children = { header, addRow, message } };
        DockPanel.SetDock(top, Dock.Top);
        var left = new Border
        {
            Width = 280,
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = Ui.Divider,
            Padding = new Thickness(12),
            Child = new DockPanel { Children = { top, _clientList } },
        };
        DockPanel.SetDock(left, Dock.Left);

        Content = new DockPanel { Children = { left, _detail } };
        RefreshClients();
    }

    /// <summary>Reloads the list, keeping (or moving to) the given client.</summary>
    public void RefreshClients(string? selectId = null)
    {
        if (_workspace is null || _clientList is null) return;
        var problems = new List<string>();
        var clients = _workspace.ListClients(problems);
        var items = new List<object>();
        foreach (var c in clients)
        {
            items.Add(new ClientItem(c));
            items.AddRange(c.Grids.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => new GridItem(c, g)));
        }

        selectId ??= CurrentDetail?.Client.Id;
        _clientList.ItemsSource = items;
        if (problems.Count > 0) _host.SetStatus(string.Join(" ", problems));
        DataChanged?.Invoke(this, EventArgs.Empty);

        var selected = items.OfType<ClientItem>().FirstOrDefault(i => i.Client.Id == selectId);
        if (selected is not null)
        {
            _clientList.SelectedItem = selected;
            if (CurrentDetail?.Client.Id == selected.Client.Id)
            {
                // Same client: keep its page (and open tab), just with fresh data.
                _detail!.Content = null;
            }
            OpenClient(selected.Client.Id);
        }
        else if (clients.Count == 0)
        {
            _detail!.Content = Centered(
                Ui.Heading("No clients yet"),
                Ui.Muted("Add a client on the left, then import grid backups into it, keep contacts and notes, " +
                         "and log engagement work."));
        }
        else
        {
            _detail!.Content = Centered(Ui.Muted("Select a client on the left."));
        }
    }

    /// <summary>Rebuilds the client/grid list without replacing the open client page.</summary>
    private void RefreshList()
    {
        if (_workspace is null || _clientList is null) return;
        var keep = CurrentDetail?.Client.Id;
        var items = new List<object>();
        foreach (var c in _workspace.ListClients())
        {
            items.Add(new ClientItem(c));
            items.AddRange(c.Grids.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => new GridItem(c, g)));
        }
        _suppressSelection = true;
        _clientList.ItemsSource = items;
        _clientList.SelectedItem = items.OfType<ClientItem>().FirstOrDefault(i => i.Client.Id == keep);
        _suppressSelection = false;
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool _suppressSelection;

    public void SelectClient(string id)
    {
        var items = _clientList?.ItemsSource as IEnumerable<object>;
        if (items?.OfType<ClientItem>().FirstOrDefault(i => i.Client.Id == id) is { } item) _clientList!.SelectedItem = item;
    }

    /// <summary>Opens a grid's newest snapshot in Inventory: no backup needed.</summary>
    public async Task OpenGridAsync(string clientId, string gridId, bool switchToInventory = true)
    {
        if (_workspace is null) return;
        try
        {
            var client = _workspace.GetClient(clientId);
            if (client.FindGrid(gridId) is not { } grid) return;
            var latest = await Task.Run(() => _workspace.LoadLatest(clientId, gridId));
            if (latest is null)
            {
                _host.SetStatus($"{client.Name} › {grid.Name} has no snapshots yet.");
                return;
            }
            _host.ShowSnapshot(client, grid, latest.Value.Snapshot, latest.Value.Inventory, switchToInventory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException
                                       or System.Security.Cryptography.CryptographicException)
        {
            _host.SetStatus($"Could not open the grid: {ex.Message}");
        }
    }

    private void OpenClient(string id)
    {
        if (_workspace is null || _detail is null) return;
        if (CurrentDetail?.Client.Id == id) return;
        var detail = new ClientDetailView(_workspace, _workspace.GetClient(id), _host);
        detail.ClientChanged += (_, _) => RefreshList();
        detail.ClientDeleted += (_, _) => RefreshClients();
        _detail.Content = detail;
    }

    private static Control Centered(params Control[] children)
    {
        var stack = new StackPanel { MaxWidth = 520, Margin = new Thickness(24) };
        foreach (var c in children) stack.Children.Add(c);
        return new ScrollViewer
        {
            Content = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = stack,
            },
        };
    }

    private static Control ListRow(object? item) => item switch
    {
        ClientItem c => new StackPanel
        {
            Margin = new Thickness(0, 6, 0, 2),
            Children =
            {
                new TextBlock { Text = c.Client.Name, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                new TextBlock
                {
                    Text = c.Client.Grids.Count switch
                    {
                        0 => "no grids yet",
                        1 => "1 grid",
                        var n => $"{n} grids",
                    },
                    Opacity = 0.6,
                    FontSize = 12,
                },
            },
        },
        GridItem g => new StackPanel
        {
            Margin = new Thickness(18, 2, 0, 2),
            Children =
            {
                new TextBlock { Text = "▸ " + g.Grid.Name },
                new TextBlock
                {
                    Text = g.Grid.LatestSnapshot is { } s
                        ? $"   latest {s.ImportedUtc.ToLocalTime():yyyy-MM-dd} · NIOS {(s.NiosVersion ?? "?").Split('-')[0]}"
                        : "   no snapshots",
                    Opacity = 0.55,
                    FontSize = 12,
                },
            },
        },
        _ => new TextBlock(),
    };

    private sealed record ClientItem(Client Client);

    private sealed record GridItem(Client Client, ClientGrid Grid);
}
