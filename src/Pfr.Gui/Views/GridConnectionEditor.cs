using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Pfr.Core.Clients;
using Pfr.Core.Wapi;
using Pfr.Gui.Controls;

namespace Pfr.Gui.Views;

/// <summary>Raised when a backup pulled over WAPI has been saved as a snapshot of the grid.</summary>
public sealed record SnapshotAddedEventArgs(ClientGrid Grid, SnapshotInfo Snapshot);

/// <summary>
/// The WAPI address and accounts of one grid: the Connection tab of a client's Grids page.
/// Saved encrypted with the client (never in snapshots); passwords are masked unless "Show passwords" is ticked.
/// It can also test the connection and pull a fresh backup over WAPI, using the read-only account and the grid's
/// pinned certificate: an unknown or changed certificate is shown for the engineer to confirm, never silently accepted.
/// </summary>
public sealed class GridConnectionEditor : UserControl
{
    private const char Mask = '•';

    private readonly ClientWorkspace _workspace;
    private readonly TextBlock _title = Ui.Heading("Connection", 16);
    private readonly TextBlock _message = Ui.Message();
    private readonly StackPanel _form;
    private readonly CheckBox _reveal = new() { Content = "Show passwords", Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _trustText = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _trustButton = new() { Content = "Trust and pin this certificate" };
    private readonly Border _trustPanel;

    private string? _clientId;
    private ClientGrid? _grid;
    private GridConnection? _saved;
    private WapiCertificateException? _pending;

    public GridConnectionEditor(ClientWorkspace workspace)
    {
        _workspace = workspace;

        var save = Ui.Accent("Save connection");
        save.Click += (_, _) => Save();
        var remove = Ui.Confirming("Remove connection", Remove, "Click again to remove the saved connection");
        _reveal.IsCheckedChanged += (_, _) => ApplyReveal();

        var test = new Button { Content = "Test connection" };
        test.Click += async (_, _) => await TestAsync();
        var pull = new Button { Content = "Pull backup now" };
        pull.Click += async (_, _) => await PullBackupAsync();
        _trustButton.Click += (_, _) => TrustPending();

        _trustPanel = new Border
        {
            IsVisible = false,
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = Ui.Danger,
            Child = new StackPanel { Spacing = 8, Children = { _trustText, _trustButton } },
        };

        _form = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Ui.Muted("Stored encrypted with this client, using your master password, and never copied into snapshots " +
                         "or reports. The read-only account is for everyday work; add a read-write account only if you " +
                         "will apply changes to this grid."),
                Ui.Label("Grid Master address"), Address,
                Ui.Label("Port"), Port,
                Ui.Label("WAPI version (optional)"), WapiVersion,
                Ui.Label("Certificate fingerprint, SHA-256 (pinned the first time you connect)"), FingerprintBox,
                new Border { Height = 12 },
                Ui.Heading("Read-only account", 14),
                Ui.Label("User id"), ReadOnlyUser,
                Ui.Label("Password"), ReadOnlyPassword,
                new Border { Height = 12 },
                Ui.Heading("Read-write account (optional)", 14),
                Ui.Label("User id"), ReadWriteUser,
                Ui.Label("Password"), ReadWritePassword,
                _reveal,
                new Border { Height = 8 },
                Ui.Row(save, remove),
                new Border { Height = 16 },
                Ui.Heading("Connect to the grid", 14),
                Ui.Muted("Uses the read-only account over HTTPS, and only if the grid's certificate is the pinned one. " +
                         "Pull backup now downloads a fresh grid backup, parses it as it arrives (it is never saved to " +
                         "disk) and stores it as a snapshot of this grid; the account needs permission to take backups."),
                Ui.Row(test, pull),
                _trustPanel,
            },
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(4, 12),
                Spacing = 4,
                Children = { _title, _form, _message },
            },
        };
        Bind(null, null);
    }

    public TextBox Address { get; } = Field("Host name or IP address, e.g. gm.example.com");
    public TextBox Port { get; } = Field("443");
    public TextBox WapiVersion { get; } = Field("Leave empty to detect, or e.g. v2.12");
    public TextBox FingerprintBox { get; } = Field("Pinned on first connect, or paste the grid's SHA-256 fingerprint");
    public TextBox ReadOnlyUser { get; } = Field("User id");
    public TextBox ReadOnlyPassword { get; } = Field("Password", masked: true);
    public TextBox ReadWriteUser { get; } = Field("User id");
    public TextBox ReadWritePassword { get; } = Field("Password", masked: true);

    /// <summary>Raised after a backup pulled over WAPI is saved, so the page can refresh its snapshot list.</summary>
    public event EventHandler<SnapshotAddedEventArgs>? SnapshotAdded;

    /// <summary>The certificate waiting for the engineer's decision, if a connection was refused over it.</summary>
    public WapiCertificateException? PendingCertificate => _pending;

    /// <summary>True while the passwords are shown as plain text (the "Show passwords" box).</summary>
    public bool PasswordsRevealed
    {
        get => _reveal.IsChecked == true;
        set
        {
            _reveal.IsChecked = value;
            ApplyReveal();
        }
    }

    /// <summary>The last message shown under the form (empty when none).</summary>
    public string MessageText => _message.IsVisible ? _message.Text ?? "" : "";

    private static TextBox Field(string watermark, bool masked = false) => new()
    {
        Watermark = watermark,
        Width = 320,
        HorizontalAlignment = HorizontalAlignment.Left,
        PasswordChar = masked ? Mask : '\0',
    };

    /// <summary>Shows the saved connection of <paramref name="grid"/> (or an empty, disabled form for none).</summary>
    public void Bind(string? clientId, ClientGrid? grid)
    {
        _clientId = clientId;
        _grid = grid;
        _saved = null;
        _message.IsVisible = false;
        HidePending();
        PasswordsRevealed = false;
        _form.IsEnabled = clientId is not null && grid is not null;
        _title.Text = grid is null ? "Connection: select a grid" : $"Connection for {grid.Name}";

        if (clientId is not null && grid is not null)
        {
            try
            {
                _saved = _workspace.GetConnection(clientId, grid.Id);
            }
            catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or InvalidDataException)
            {
                Ui.ShowError(_message, $"Could not read the saved connection: {ex.Message}");
            }
        }
        Fill(_saved);
    }

    /// <summary>Saves what is in the form. Returns false (with a message) if it can't be saved.</summary>
    public bool Save()
    {
        if (_clientId is null || _grid is null)
        {
            Ui.ShowError(_message, "Select a grid first.");
            return false;
        }

        var portText = (Port.Text ?? "").Trim();
        var port = GridConnection.DefaultPort;
        if (portText.Length > 0 && !int.TryParse(portText, out port))
        {
            Ui.ShowError(_message, $"\"{portText}\" is not a valid port.");
            return false;
        }

        var connection = _saved ?? new GridConnection();
        connection.Address = Address.Text ?? "";
        connection.Port = port;
        connection.WapiVersion = WapiVersion.Text;
        connection.CertificateFingerprint = FingerprintBox.Text;
        connection.ReadOnly = new Credential { Username = ReadOnlyUser.Text ?? "", Password = ReadOnlyPassword.Text ?? "" };
        connection.ReadWrite = new Credential { Username = ReadWriteUser.Text ?? "", Password = ReadWritePassword.Text ?? "" };

        try
        {
            _saved = _workspace.SaveConnection(_clientId, _grid.Id, connection);
            Fill(_saved); // shows the tidied values (e.g. a pasted https:// removed)
            Ui.ShowInfo(_message, "Connection saved.");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException)
        {
            Ui.ShowError(_message, ex.Message);
            return false;
        }
    }

    /// <summary>Deletes the saved connection (the grid and its snapshots are untouched).</summary>
    public void Remove()
    {
        if (_clientId is null || _grid is null) return;
        _workspace.DeleteConnection(_clientId, _grid.Id);
        _saved = null;
        HidePending();
        Fill(null);
        Ui.ShowInfo(_message, "Saved connection removed.");
    }

    /// <summary>
    /// Saves the form, then connects with the read-only account to check the certificate, the account and the
    /// WAPI version. An unknown or changed certificate is not accepted: it is shown to be trusted or not.
    /// </summary>
    public Task<bool> TestAsync() =>
        RunAsync("Connecting to the grid…", async wapi =>
        {
            var info = await wapi.ConnectAsync();
            Ui.ShowInfo(_message, $"Connected to grid \"{info.GridName ?? "(unnamed)"}\" using WAPI v{info.ApiVersion}. " +
                                  "The certificate is the pinned one.");
        });

    /// <summary>
    /// Saves the form, then pulls a fresh backup over WAPI and stores it as a snapshot of this grid. The backup is
    /// parsed as it downloads and never written to disk.
    /// </summary>
    public Task<bool> PullBackupAsync(string label = "") =>
        RunAsync("Asking the grid for a backup. This can take a few minutes…", async wapi =>
        {
            var (grid, snapshot) = await GridBackupPull.PullAsync(_workspace, _clientId!, _grid!.Id, wapi, label);
            SnapshotAdded?.Invoke(this, new SnapshotAddedEventArgs(grid, snapshot));
            Ui.ShowInfo(_message, $"Saved a snapshot of {grid.Name} pulled over WAPI: {snapshot.Members} members, " +
                                  $"{snapshot.Zones} zones, {snapshot.Networks} networks, {snapshot.Records} records.");
        });

    /// <summary>
    /// Pins the certificate the grid presented, after the engineer has compared its fingerprint with the grid's own.
    /// Replaces an earlier pin if the certificate had changed.
    /// </summary>
    public bool TrustPending()
    {
        if (_pending is null || _clientId is null || _grid is null) return false;
        try
        {
            _saved = _workspace.PinCertificate(_clientId, _grid.Id, _pending.ObservedFingerprint);
            Fill(_saved);
            HidePending();
            Ui.ShowInfo(_message, "Certificate pinned. Run Test connection or Pull backup again.");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException)
        {
            Ui.ShowError(_message, ex.Message);
            return false;
        }
    }

    private async Task<bool> RunAsync(string busy, Func<WapiClient, Task> action)
    {
        if (_clientId is null || _grid is null)
        {
            Ui.ShowError(_message, "Select a grid first.");
            return false;
        }
        if (!Save()) return false;

        var connection = _saved!;
        if (connection.ReadOnly is null)
        {
            Ui.ShowError(_message, "Add a read-only account (user id and password) first.");
            return false;
        }

        HidePending();
        Ui.ShowInfo(_message, busy);
        try
        {
            using var wapi = WapiClient.ForReadOnly(connection);
            await action(wapi);
            return true;
        }
        catch (WapiCertificateException ex)
        {
            ShowCertificatePrompt(ex);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is a UI event: report the problem instead of letting it end the app.
            Ui.ShowError(_message, ex.Message);
            return false;
        }
    }

    private void ShowCertificatePrompt(WapiCertificateException ex)
    {
        _pending = ex;
        var presented = CertificatePin.Format(ex.ObservedFingerprint);
        if (ex.Problem == CertificateProblem.NotPinned)
        {
            _trustText.Text = "The grid presented a certificate you have not trusted yet. No user id or password was sent.\n" +
                              $"SHA-256: {presented}\n" +
                              "Compare this with the grid's own certificate (for example in Grid Manager) before trusting it.";
            _trustButton.Content = "Trust and pin this certificate";
        }
        else
        {
            _trustText.Text = "WARNING: the grid presented a different certificate from the pinned one. " +
                              "No user id or password was sent.\n" +
                              $"Pinned:    {CertificatePin.Format(ex.PinnedFingerprint ?? "")}\n" +
                              $"Presented: {presented}\n" +
                              "A renewed certificate looks like this, and so does an attack. Only replace the pin if you know it was renewed.";
            _trustButton.Content = "Replace the pinned certificate";
        }
        _message.IsVisible = false;
        _trustPanel.IsVisible = true;
    }

    private void HidePending()
    {
        _pending = null;
        _trustPanel.IsVisible = false;
    }

    private void Fill(GridConnection? c)
    {
        Address.Text = c?.Address ?? "";
        Port.Text = c is null ? "" : c.Port.ToString();
        WapiVersion.Text = c?.WapiVersion ?? "";
        FingerprintBox.Text = c?.CertificateFingerprint is { } pin ? CertificatePin.Format(pin) : "";
        ReadOnlyUser.Text = c?.ReadOnly?.Username ?? "";
        ReadOnlyPassword.Text = c?.ReadOnly?.Password ?? "";
        ReadWriteUser.Text = c?.ReadWrite?.Username ?? "";
        ReadWritePassword.Text = c?.ReadWrite?.Password ?? "";
    }

    private void ApplyReveal()
    {
        var character = PasswordsRevealed ? '\0' : Mask;
        ReadOnlyPassword.PasswordChar = character;
        ReadWritePassword.PasswordChar = character;
    }
}
