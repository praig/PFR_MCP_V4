using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Backup;
using Pfr.Gui.Controls;

namespace Pfr.Gui.Views;

/// <summary>One client: details and contacts, its grids and their snapshots, and the engagement log.</summary>
public sealed class ClientDetailView : UserControl
{
    private static readonly FilePickerFileType BackupFiles = new("NIOS grid backup")
    {
        Patterns = ["*.bak", "*.tar.gz", "*.tgz", "*.tar", "*.xml"],
    };

    private readonly ClientWorkspace _workspace;
    private readonly IInventoryHost _host;
    private readonly TabControl _tabs;

    private ListBox _gridList = null!;
    private ListBox _snapshotList = null!;
    private GridConnectionEditor _connection = null!;
    private SiteLoginsEditor _logins = null!;
    private LiveStatusView _live = null!;
    private TextBox _gridName = null!;
    private TextBlock _gridMessage = null!;
    private ListBox _logList = null!;

    public ClientDetailView(ClientWorkspace workspace, Client client, IInventoryHost host)
    {
        _workspace = workspace;
        _host = host;
        Client = client;

        _tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Details", Content = BuildDetails() },
                new TabItem { Header = "Grids", Content = BuildGrids() },
                new TabItem { Header = "Engagement log", Content = BuildLog() },
                new TabItem { Header = "Logins & keys", Content = BuildLogins() },
            },
        };
        Content = new DockPanel { Margin = new Thickness(16, 8), Children = { _tabs } };
    }

    public Client Client { get; private set; }

    /// <summary>Raised with the client id after a rename or other change shown in the list.</summary>
    public event EventHandler<string>? ClientChanged;

    public event EventHandler? ClientDeleted;

    public IReadOnlyList<ClientGrid> Grids => Client.Grids;

    /// <summary>Snapshots of every grid.</summary>
    public IReadOnlyList<SnapshotInfo> Snapshots => Client.AllSnapshots.ToList();

    public ClientGrid? SelectedGrid => (_gridList.SelectedItem as GridItem)?.Grid;

    /// <summary>The Connection tab's form for the selected grid.</summary>
    public GridConnectionEditor ConnectionEditor => _connection;

    /// <summary>The Live status tab for the selected grid (read over WAPI).</summary>
    public LiveStatusView LiveStatus => _live;

    /// <summary>The "Logins &amp; keys" tab (the client's own sites and API keys).</summary>
    public SiteLoginsEditor LoginsEditor => _logins;

    /// <summary>Selects a grid in the Grids tab (showing its snapshots and connection).</summary>
    public void SelectGrid(string gridId) =>
        _gridList.SelectedItem = (_gridList.ItemsSource as IEnumerable<GridItem>)?.FirstOrDefault(i => i.Grid.Id == gridId);

    public void SelectTab(int index) => _tabs.SelectedIndex = index;

    // ---- Details and contacts ---------------------------------------------

    private Control BuildDetails()
    {
        var name = new TextBox { Text = Client.Name, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var notes = Ui.Multiline("Notes: environment, change windows, access, anything worth remembering", 140);
        notes.Text = Client.Notes;
        var message = Ui.Message();

        var save = Ui.Accent("Save details");
        save.Click += (_, _) =>
        {
            try
            {
                Client.Name = name.Text ?? "";
                Client.Notes = notes.Text ?? "";
                _workspace.SaveClient(Client);
                Ui.ShowInfo(message, "Saved.");
                ClientChanged?.Invoke(this, Client.Id);
            }
            catch (ArgumentException ex)
            {
                Ui.ShowError(message, ex.Message);
            }
        };

        var delete = Ui.Confirming("Delete client…", () =>
        {
            _workspace.DeleteClient(Client.Id);
            _host.SetStatus($"Deleted client {Client.Name} and all of its data.");
            ClientDeleted?.Invoke(this, EventArgs.Empty);
        }, "Click again to delete client and all its data");

        var created = Ui.Muted($"Created {Client.CreatedUtc.ToLocalTime():yyyy-MM-dd} · updated {Client.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(4, 12),
                Spacing = 2,
                Children =
                {
                    Ui.Label("Client name"), name,
                    Ui.Label("Notes"), notes,
                    new Border { Height = 8 },
                    Ui.Row(save, delete),
                    message,
                    new Border { Height = 4 },
                    created,
                    new Border { Height = 16, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Ui.Divider },
                    BuildContacts(),
                },
            },
        };
    }

    private Control BuildContacts()
    {
        var list = new ListBox { MaxHeight = 220, Background = Brushes.Transparent };
        var fName = new TextBox { Watermark = "Name" };
        var fRole = new TextBox { Watermark = "Role" };
        var fEmail = new TextBox { Watermark = "Email" };
        var fPhone = new TextBox { Watermark = "Phone" };
        var fNotes = new TextBox { Watermark = "Notes" };
        var message = Ui.Message();

        void Refresh(Contact? select = null)
        {
            list.ItemsSource = Client.Contacts.Select(c => new ContactItem(c)).ToList();
            list.SelectedItem = (list.ItemsSource as List<ContactItem>)!.FirstOrDefault(i => i.Contact == select);
        }

        void Fill(Contact? c)
        {
            fName.Text = c?.Name ?? "";
            fRole.Text = c?.Role ?? "";
            fEmail.Text = c?.Email ?? "";
            fPhone.Text = c?.Phone ?? "";
            fNotes.Text = c?.Notes ?? "";
        }

        void Read(Contact c)
        {
            c.Name = (fName.Text ?? "").Trim();
            c.Role = (fRole.Text ?? "").Trim();
            c.Email = (fEmail.Text ?? "").Trim();
            c.Phone = (fPhone.Text ?? "").Trim();
            c.Notes = (fNotes.Text ?? "").Trim();
        }

        void Save(string done)
        {
            _workspace.SaveClient(Client);
            Ui.ShowInfo(message, done);
        }

        list.SelectionChanged += (_, _) => Fill((list.SelectedItem as ContactItem)?.Contact);

        var add = Ui.Accent("Add contact");
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(fName.Text))
            {
                Ui.ShowError(message, "A contact needs a name.");
                return;
            }
            var contact = new Contact();
            Read(contact);
            Client.Contacts.Add(contact);
            Save("Contact added.");
            Refresh(contact);
        };

        var update = new Button { Content = "Update selected" };
        update.Click += (_, _) =>
        {
            if (list.SelectedItem is not ContactItem item)
            {
                Ui.ShowError(message, "Select a contact to update.");
                return;
            }
            Read(item.Contact);
            Save("Contact updated.");
            Refresh(item.Contact);
        };

        var remove = Ui.Confirming("Remove selected", () =>
        {
            if (list.SelectedItem is not ContactItem item) return;
            Client.Contacts.Remove(item.Contact);
            Save("Contact removed.");
            Fill(null);
            Refresh();
        });

        var clear = new Button { Content = "Clear form" };
        clear.Click += (_, _) =>
        {
            list.SelectedItem = null;
            Fill(null);
        };

        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        void Place(Control c, int row, int col, int span = 1)
        {
            c.Margin = new Thickness(0, 0, col == 0 && span == 1 ? 6 : 0, 6);
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            Grid.SetColumnSpan(c, span);
            form.Children.Add(c);
        }
        Place(fName, 0, 0);
        Place(fRole, 0, 1);
        Place(fEmail, 1, 0);
        Place(fPhone, 1, 1);
        Place(fNotes, 2, 0, 2);

        Refresh();
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.Heading("Contacts", 16),
                list,
                form,
                Ui.Row(add, update, remove, clear),
                message,
            },
        };
    }

    // ---- Grids and snapshots ------------------------------------------------

    private Control BuildGrids()
    {
        _connection = new GridConnectionEditor(_workspace);
        _live = new LiveStatusView(_workspace);
        _connection.SnapshotAdded += (_, e) =>
        {
            Reload(e.Grid.Id);
            _host.SetStatus($"Pulled a backup of {e.Grid.Name} over WAPI into {Client.Name}.");
        };
        _gridList = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<GridItem>((g, _) => GridRow(g), supportsRecycling: false),
        };
        _snapshotList = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<SnapshotInfo>((s, _) => SnapshotRow(s), supportsRecycling: false),
        };
        _gridMessage = Ui.Message();
        var label = new TextBox
        {
            Watermark = "Label for the next snapshot (optional), e.g. \"Q4 health check\"",
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // Import: the backup goes to the grid with the same grid name, or a new grid.
        var import = Ui.Accent("Import backup…");
        import.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Import grid backup for {Client.Name}",
                AllowMultiple = false,
                FileTypeFilter = [BackupFiles, FilePickerFileTypes.All],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                await ImportAsync(path, label.Text ?? "");
                label.Text = "";
            }
        };

        var addCurrent = new Button { Content = "Add grid open in Inventory" };
        addCurrent.Click += (_, _) =>
        {
            AddCurrentGrid(label.Text ?? "");
            label.Text = "";
        };

        // Grid actions.
        _gridList.SelectionChanged += (_, _) =>
        {
            var grid = SelectedGrid;
            _snapshotList.ItemsSource = grid?.Snapshots.OrderByDescending(s => s.ImportedUtc).ToList();
            _gridName.Text = grid?.Name ?? "";
            _connection.Bind(Client.Id, grid);
            _live.Bind(Client.Id, grid);
        };
        _gridList.DoubleTapped += async (_, _) =>
        {
            if (SelectedGrid is { } g) await OpenLatestAsync(g.Id);
        };

        var openLatest = Ui.Accent("Open latest");
        openLatest.Click += async (_, _) =>
        {
            if (SelectedGrid is { } g) await OpenLatestAsync(g.Id);
            else Ui.ShowError(_gridMessage, "Select a grid.");
        };

        _gridName = new TextBox { Watermark = "Grid display name", Width = 220 };
        var rename = new Button { Content = "Rename" };
        rename.Click += (_, _) =>
        {
            if (SelectedGrid is not { } g)
            {
                Ui.ShowError(_gridMessage, "Select a grid to rename.");
                return;
            }
            try
            {
                Client.FindGrid(g.Id)!.Name = _gridName.Text ?? "";
                _workspace.SaveClient(Client);
                Reload(g.Id);
                Ui.ShowInfo(_gridMessage, "Grid renamed. New backups of it still go to this grid.");
            }
            catch (ArgumentException ex)
            {
                Client = _workspace.GetClient(Client.Id);
                Ui.ShowError(_gridMessage, ex.Message);
            }
        };

        var deleteGrid = Ui.Confirming("Delete grid", () =>
        {
            if (SelectedGrid is not { } g) return;
            _workspace.DeleteGrid(Client.Id, g.Id);
            Reload();
            Ui.ShowInfo(_gridMessage, $"Deleted grid {g.Name} and its snapshots.");
        }, "Click again to delete grid and all its snapshots");

        // Snapshot actions.
        var openSnapshot = new Button { Content = "Open snapshot" };
        openSnapshot.Click += async (_, _) => await OpenSelectedSnapshotAsync();
        _snapshotList.DoubleTapped += async (_, _) => await OpenSelectedSnapshotAsync();

        var deleteSnapshot = Ui.Confirming("Delete snapshot", () =>
        {
            if (SelectedGrid is not { } g || _snapshotList.SelectedItem is not SnapshotInfo s) return;
            _workspace.DeleteSnapshot(Client.Id, g.Id, s.Id);
            Reload(g.Id);
            Ui.ShowInfo(_gridMessage, "Snapshot deleted.");
        });

        var gridsPane = new DockPanel
        {
            Children =
            {
                Docked(new StackPanel
                {
                    Spacing = 6,
                    Margin = new Thickness(0, 0, 0, 6),
                    Children =
                    {
                        Ui.Heading("Grids", 16),
                        Ui.Row(openLatest, deleteGrid),
                        Ui.Row(_gridName, rename),
                    },
                }, Dock.Top),
                _gridList,
            },
        };
        var snapshotsPane = new DockPanel
        {
            Children =
            {
                Docked(new StackPanel
                {
                    Spacing = 6,
                    Margin = new Thickness(0, 0, 0, 6),
                    Children = { Ui.Heading("Snapshots of the selected grid", 16), Ui.Row(openSnapshot, deleteSnapshot) },
                }, Dock.Top),
                _snapshotList,
            },
        };

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("360,16,*") };
        split.Children.Add(gridsPane);
        var detailTabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Snapshots", Content = snapshotsPane },
                new TabItem { Header = "Connection", Content = _connection },
                new TabItem { Header = "Live status", Content = _live },
            },
        };
        Grid.SetColumn(detailTabs, 2);
        split.Children.Add(detailTabs);

        var top = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(4, 12, 4, 12),
            Children =
            {
                Ui.Muted("A client can have several grids. Each import is stored as a snapshot of its grid (matched by " +
                         "grid name), keeping the parsed inventory only: no passwords, keys or other secrets. " +
                         "Opening a grid shows its latest snapshot instantly; the backup isn't needed again."),
                label,
                Ui.Row(import, addCurrent),
                _gridMessage,
            },
        };
        DockPanel.SetDock(top, Dock.Top);
        ReloadGrids();
        return new DockPanel { Children = { top, split } };
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static Control GridRow(GridItem? item)
    {
        if (item is null) return new TextBlock();
        var g = item.Grid;
        var latest = g.LatestSnapshot;
        var foundAs = latest?.GridName is { } n && !string.Equals(n, g.Name, StringComparison.OrdinalIgnoreCase)
            ? $" · grid name \"{n}\""
            : "";
        return new StackPanel
        {
            Margin = new Thickness(0, 4),
            Children =
            {
                new TextBlock { Text = g.Name, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = latest is null
                        ? "no snapshots"
                        : $"{g.Snapshots.Count} snapshot{(g.Snapshots.Count == 1 ? "" : "s")} · latest {latest.ImportedUtc.ToLocalTime():yyyy-MM-dd} · NIOS {(latest.NiosVersion ?? "?").Split('-')[0]}{foundAs}",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
    }

    private static Control SnapshotRow(SnapshotInfo? s)
    {
        if (s is null) return new TextBlock();
        var title = s.Label.Length > 0 ? s.Label : s.SourceFileName;
        return new StackPanel
        {
            Margin = new Thickness(0, 4),
            Children =
            {
                new TextBlock
                {
                    Text = $"{s.ImportedUtc.ToLocalTime():yyyy-MM-dd HH:mm}   {title}",
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = $"NIOS {(s.NiosVersion ?? "?").Split('-')[0]} · {s.Members} members · {s.Zones} zones · " +
                           $"{s.Networks} networks · {s.DhcpRanges} DHCP ranges · {s.Records} records",
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = $"{s.SourceFileName} · {s.SourceBytes / 1024.0 / 1024.0:0.0} MB · SHA-256 {Short(s.SourceSha256)}",
                    Opacity = 0.55,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
    }

    private static string Short(string sha) => sha.Length > 16 ? sha[..16] + "…" : sha;

    private void ReloadGrids(string? selectGridId = null)
    {
        var items = Client.Grids.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => new GridItem(g)).ToList();
        selectGridId ??= SelectedGrid?.Id ?? items.FirstOrDefault()?.Grid.Id;
        _gridList.ItemsSource = items;
        _gridList.SelectedItem = items.FirstOrDefault(i => i.Grid.Id == selectGridId);
        if (_gridList.SelectedItem is null) _snapshotList.ItemsSource = null;
    }

    private void Reload(string? selectGridId = null)
    {
        Client = _workspace.GetClient(Client.Id);
        ReloadGrids(selectGridId);
        ClientChanged?.Invoke(this, Client.Id);
    }

    /// <summary>Parses and stores a backup as a snapshot of its grid (off the UI thread).</summary>
    public async Task ImportAsync(string path, string label = "")
    {
        var name = Path.GetFileName(path);
        Ui.ShowInfo(_gridMessage, $"Reading {name}…");
        try
        {
            var sha = await Task.Run(() => ClientWorkspace.Sha256Of(path));
            var duplicate = _workspace.FindDuplicate(Client.Id, sha);
            var (grid, info) = await Task.Run(() => _workspace.ImportBackup(Client.Id, path, label: label));
            Reload(grid.Id);
            _snapshotList.SelectedItem = (_snapshotList.ItemsSource as IEnumerable<SnapshotInfo>)?.FirstOrDefault(s => s.Id == info.Id);
            Ui.ShowInfo(_gridMessage, duplicate is null
                ? $"Imported {name} into grid {grid.Name}."
                : $"Imported {name} into grid {grid.Name}. Note: the same file was already imported on {duplicate.Value.Snapshot.ImportedUtc.ToLocalTime():yyyy-MM-dd}.");
            _host.SetStatus($"Saved snapshot of {grid.Name} to {Client.Name}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Xml.XmlException or ArgumentException)
        {
            Ui.ShowError(_gridMessage, $"Could not import {name}: {ex.Message}");
        }
    }

    /// <summary>Saves the grid currently open in Inventory as a snapshot (no re-parse).</summary>
    public void AddCurrentGrid(string label = "")
    {
        if (_host.LoadedInventory is not { } inventory || _host.LoadedSource is not { } source)
        {
            Ui.ShowError(_gridMessage, "No grid is open in Inventory. Open a backup there first, or use Import backup.");
            return;
        }
        try
        {
            var duplicate = _workspace.FindDuplicate(Client.Id, source.Sha256);
            var (grid, info) = _workspace.AddSnapshot(Client.Id, inventory, source.FileName, source.Bytes, source.Sha256, label: label);
            Reload(grid.Id);
            _host.ShowSnapshot(Client, grid, info, inventory, switchToInventory: false);
            Ui.ShowInfo(_gridMessage, duplicate is null
                ? $"Saved {source.FileName} to grid {grid.Name}."
                : $"Saved {source.FileName} to grid {grid.Name}. Note: the same file was already saved on {duplicate.Value.Snapshot.ImportedUtc.ToLocalTime():yyyy-MM-dd}.");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            Ui.ShowError(_gridMessage, ex.Message);
        }
    }

    private async Task OpenSelectedSnapshotAsync()
    {
        if (SelectedGrid is not { } g || _snapshotList.SelectedItem is not SnapshotInfo s)
        {
            Ui.ShowError(_gridMessage, "Select a snapshot to open.");
            return;
        }
        await OpenSnapshotAsync(g.Id, s.Id);
    }

    /// <summary>Opens a grid's newest snapshot in Inventory.</summary>
    public async Task OpenLatestAsync(string gridId)
    {
        if (Client.FindGrid(gridId)?.LatestSnapshot is { } latest) await OpenSnapshotAsync(gridId, latest.Id);
        else Ui.ShowError(_gridMessage, "That grid has no snapshots yet.");
    }

    public async Task OpenSnapshotAsync(string gridId, string snapshotId)
    {
        if (Client.FindGrid(gridId) is not { } grid || grid.Snapshots.FirstOrDefault(x => x.Id == snapshotId) is not { } s)
        {
            Ui.ShowError(_gridMessage, "That snapshot no longer exists.");
            return;
        }
        try
        {
            var inventory = await Task.Run(() => _workspace.LoadSnapshot(Client.Id, grid.Id, s.Id));
            _host.ShowSnapshot(Client, grid, s, inventory, switchToInventory: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                                       or System.Security.Cryptography.CryptographicException)
        {
            Ui.ShowError(_gridMessage, $"Could not open the snapshot: {ex.Message}");
        }
    }

    private sealed record GridItem(ClientGrid Grid);

    // ---- Logins and API keys ------------------------------------------------

    private Control BuildLogins()
    {
        _logins = new SiteLoginsEditor(_workspace, Client.Id);
        return _logins;
    }

    // ---- Engagement log ----------------------------------------------------

    private Control BuildLog()
    {
        _logList = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<LogEntry>((e, _) => LogRow(e), supportsRecycling: false),
        };
        var date = new CalendarDatePicker { SelectedDate = DateTime.Today, Width = 180 };
        var title = new TextBox { Watermark = "Title, e.g. \"Collected grid backup\"" };
        var text = Ui.Multiline("What was done, findings, follow-ups…", 110);
        var snapshot = new ComboBox { Width = 420, PlaceholderText = "Link to a snapshot (optional)" };
        var message = Ui.Message();
        LogEntry? editing = null;

        void LoadSnapshotChoices()
        {
            var choices = new List<SnapshotChoice> { new(null, "(no snapshot)") };
            choices.AddRange(Client.Grids
                .SelectMany(g => g.Snapshots.Select(s => (Grid: g, Snapshot: s)))
                .OrderByDescending(x => x.Snapshot.ImportedUtc)
                .Select(x => new SnapshotChoice(x.Snapshot.Id,
                    $"{x.Snapshot.ImportedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {x.Grid.Name}" +
                    (x.Snapshot.Label.Length > 0 ? $" · {x.Snapshot.Label}" : ""))));
            snapshot.ItemsSource = choices;
        }

        void Edit(LogEntry? entry)
        {
            editing = entry;
            LoadSnapshotChoices();
            date.SelectedDate = (entry?.Date ?? DateTimeOffset.Now).LocalDateTime.Date;
            title.Text = entry?.Title ?? "";
            text.Text = entry?.Text ?? "";
            snapshot.SelectedItem = (snapshot.ItemsSource as List<SnapshotChoice>)!
                .FirstOrDefault(c => c.Id == entry?.SnapshotId) ?? (snapshot.ItemsSource as List<SnapshotChoice>)![0];
        }

        void Refresh(string? selectId = null)
        {
            var entries = _workspace.GetLog(Client.Id);
            _logList.ItemsSource = entries;
            _logList.SelectedItem = entries.FirstOrDefault(e => e.Id == selectId);
        }

        _logList.SelectionChanged += (_, _) =>
        {
            if (_logList.SelectedItem is LogEntry e) Edit(e);
        };

        var save = Ui.Accent("Save entry");
        save.Click += (_, _) =>
        {
            var entry = editing ?? new LogEntry();
            var day = date.SelectedDate ?? DateTime.Today;
            // Keep the original time of day when only the date changes; new entries use now.
            var time = editing?.Date.LocalDateTime.TimeOfDay ?? DateTime.Now.TimeOfDay;
            entry.Date = new DateTimeOffset(day.Date + time);
            entry.Title = (title.Text ?? "").Trim();
            entry.Text = (text.Text ?? "").Trim();
            entry.SnapshotId = (snapshot.SelectedItem as SnapshotChoice)?.Id;
            try
            {
                _workspace.SaveLogEntry(Client.Id, entry);
                Ui.ShowInfo(message, editing is null ? "Entry added." : "Entry updated.");
                editing = entry;
                Refresh(entry.Id);
            }
            catch (ArgumentException ex)
            {
                Ui.ShowError(message, ex.Message);
            }
        };

        var newEntry = new Button { Content = "New entry" };
        newEntry.Click += (_, _) =>
        {
            _logList.SelectedItem = null;
            Edit(null);
            message.IsVisible = false;
        };

        var delete = Ui.Confirming("Delete entry", () =>
        {
            if (editing is null) return;
            _workspace.DeleteLogEntry(Client.Id, editing.Id);
            Edit(null);
            Refresh();
            Ui.ShowInfo(message, "Entry deleted.");
        });

        var form = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(4, 12, 4, 8),
            Children = { Ui.Row(date, newEntry), title, text, snapshot, Ui.Row(save, delete), message },
        };
        DockPanel.SetDock(form, Dock.Top);

        Edit(null);
        Refresh();
        return new DockPanel { Children = { form, _logList } };
    }

    /// <summary>Entries currently listed (newest first).</summary>
    public IReadOnlyList<LogEntry> LogEntries => _logList.ItemsSource as IReadOnlyList<LogEntry> ?? [];

    private Control LogRow(LogEntry? e)
    {
        if (e is null) return new TextBlock();
        var linked = e.SnapshotId is { } id && Client.FindSnapshot(id) is { } found
            ? $"  ·  {found.Grid.Name} snapshot {found.Snapshot.ImportedUtc.ToLocalTime():yyyy-MM-dd}"
            : "";
        var firstLine = e.Text.Split('\n', 2)[0];
        return new StackPanel
        {
            Margin = new Thickness(0, 4),
            Children =
            {
                new TextBlock
                {
                    Text = $"{e.Date.ToLocalTime():yyyy-MM-dd}   {(e.Title.Length > 0 ? e.Title : "(untitled)")}",
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = firstLine + linked,
                    Opacity = 0.7,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsVisible = firstLine.Length + linked.Length > 0,
                },
            },
        };
    }

    private sealed record ContactItem(Contact Contact)
    {
        public override string ToString() =>
            string.Join("  ·  ", new[] { Contact.Name, Contact.Role, Contact.Email, Contact.Phone }.Where(x => x.Length > 0));
    }

    private sealed record SnapshotChoice(string? Id, string Text)
    {
        public override string ToString() => Text;
    }
}
