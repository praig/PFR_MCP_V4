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

/// <summary>One client: details and contacts, snapshots, and the engagement log.</summary>
public sealed class ClientDetailView : UserControl
{
    private static readonly FilePickerFileType BackupFiles = new("NIOS grid backup")
    {
        Patterns = ["*.bak", "*.tar.gz", "*.tgz", "*.tar", "*.xml"],
    };

    private readonly ClientWorkspace _workspace;
    private readonly IInventoryHost _host;
    private readonly TabControl _tabs;

    private ListBox _snapshotList = null!;
    private TextBlock _snapshotMessage = null!;
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
                new TabItem { Header = "Snapshots", Content = BuildSnapshots() },
                new TabItem { Header = "Engagement log", Content = BuildLog() },
            },
        };
        Content = new DockPanel { Margin = new Thickness(16, 8), Children = { _tabs } };
    }

    public Client Client { get; private set; }

    /// <summary>Raised with the client id after a rename or other change shown in the list.</summary>
    public event EventHandler<string>? ClientChanged;

    public event EventHandler? ClientDeleted;

    public IReadOnlyList<SnapshotInfo> Snapshots => Client.Snapshots;

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

    // ---- Snapshots ---------------------------------------------------------

    private Control BuildSnapshots()
    {
        _snapshotList = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<SnapshotInfo>((s, _) => SnapshotRow(s), supportsRecycling: false),
        };
        _snapshotMessage = Ui.Message();
        var label = new TextBox
        {
            Watermark = "Label for the next snapshot (optional), e.g. \"Q4 health check\"",
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

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

        var open = new Button { Content = "Open in Inventory" };
        open.Click += async (_, _) => await OpenSelectedAsync();
        _snapshotList.DoubleTapped += async (_, _) => await OpenSelectedAsync();

        var delete = Ui.Confirming("Delete selected", () =>
        {
            if (_snapshotList.SelectedItem is not SnapshotInfo s) return;
            _workspace.DeleteSnapshot(Client.Id, s.Id);
            Reload();
            Ui.ShowInfo(_snapshotMessage, "Snapshot deleted.");
        });

        var top = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(4, 12, 4, 8),
            Children =
            {
                Ui.Muted("Each snapshot keeps the parsed inventory of a grid backup (no passwords, keys or other " +
                         "secrets) plus the original file's name, size and SHA-256."),
                label,
                Ui.Row(import, addCurrent),
                Ui.Row(open, delete),
                _snapshotMessage,
            },
        };
        DockPanel.SetDock(top, Dock.Top);
        ReloadSnapshots();
        return new DockPanel { Children = { top, _snapshotList } };
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
                    Text = $"{s.GridName ?? "Grid"} · NIOS {(s.NiosVersion ?? "?").Split('-')[0]} · " +
                           $"{s.Members} members · {s.Zones} zones · {s.Networks} networks · " +
                           $"{s.DhcpRanges} DHCP ranges · {s.Records} records",
                    Opacity = 0.75,
                },
                new TextBlock
                {
                    Text = $"{s.SourceFileName} · {s.SourceBytes / 1024.0 / 1024.0:0.0} MB · SHA-256 {Short(s.SourceSha256)}",
                    Opacity = 0.55,
                    FontSize = 12,
                },
            },
        };
    }

    private static string Short(string sha) => sha.Length > 16 ? sha[..16] + "…" : sha;

    private void ReloadSnapshots()
    {
        _snapshotList.ItemsSource = Client.Snapshots.OrderByDescending(s => s.ImportedUtc).ToList();
    }

    private void Reload()
    {
        Client = _workspace.GetClient(Client.Id);
        ReloadSnapshots();
        ClientChanged?.Invoke(this, Client.Id);
    }

    /// <summary>Parses and stores a backup as a new snapshot (off the UI thread).</summary>
    public async Task ImportAsync(string path, string label = "")
    {
        var name = Path.GetFileName(path);
        Ui.ShowInfo(_snapshotMessage, $"Reading {name}…");
        try
        {
            var sha = await Task.Run(() => ClientWorkspace.Sha256Of(path));
            var duplicate = _workspace.FindDuplicate(Client.Id, sha);
            var info = await Task.Run(() => _workspace.ImportBackup(Client.Id, path, label));
            Reload();
            _snapshotList.SelectedItem = Client.Snapshots.FirstOrDefault(s => s.Id == info.Id);
            Ui.ShowInfo(_snapshotMessage, duplicate is null
                ? $"Imported {name}."
                : $"Imported {name}. Note: the same file was already imported on {duplicate.ImportedUtc.ToLocalTime():yyyy-MM-dd}.");
            _host.SetStatus($"Saved snapshot of {info.GridName ?? name} to {Client.Name}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Xml.XmlException)
        {
            Ui.ShowError(_snapshotMessage, $"Could not import {name}: {ex.Message}");
        }
    }

    /// <summary>Saves the grid currently open in Inventory as a snapshot (no re-parse).</summary>
    public void AddCurrentGrid(string label = "")
    {
        if (_host.LoadedInventory is not { } inventory || _host.LoadedSource is not { } source)
        {
            Ui.ShowError(_snapshotMessage, "No grid is open in Inventory. Open a backup there first, or use Import backup.");
            return;
        }
        var duplicate = _workspace.FindDuplicate(Client.Id, source.Sha256);
        var info = _workspace.AddSnapshot(Client.Id, inventory, source.FileName, source.Bytes, source.Sha256, label);
        Reload();
        _snapshotList.SelectedItem = Client.Snapshots.FirstOrDefault(s => s.Id == info.Id);
        Ui.ShowInfo(_snapshotMessage, duplicate is null
            ? $"Saved {source.FileName} as a snapshot."
            : $"Saved {source.FileName}. Note: the same file was already saved on {duplicate.ImportedUtc.ToLocalTime():yyyy-MM-dd}.");
    }

    public async Task OpenSelectedAsync()
    {
        if (_snapshotList.SelectedItem is not SnapshotInfo s)
        {
            Ui.ShowError(_snapshotMessage, "Select a snapshot to open.");
            return;
        }
        await OpenSnapshotAsync(s.Id);
    }

    public async Task OpenSnapshotAsync(string snapshotId)
    {
        var s = Client.Snapshots.First(x => x.Id == snapshotId);
        try
        {
            var inventory = await Task.Run(() => _workspace.LoadSnapshot(Client.Id, s.Id));
            _host.ShowSnapshot(Client, s, inventory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                                       or System.Security.Cryptography.CryptographicException)
        {
            Ui.ShowError(_snapshotMessage, $"Could not open the snapshot: {ex.Message}");
        }
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
            choices.AddRange(Client.Snapshots.OrderByDescending(s => s.ImportedUtc).Select(s =>
                new SnapshotChoice(s.Id, $"{s.ImportedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {(s.Label.Length > 0 ? s.Label : s.SourceFileName)}")));
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
        var linked = e.SnapshotId is { } id && Client.Snapshots.FirstOrDefault(s => s.Id == id) is { } s
            ? $"  ·  snapshot {s.ImportedUtc.ToLocalTime():yyyy-MM-dd}"
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
