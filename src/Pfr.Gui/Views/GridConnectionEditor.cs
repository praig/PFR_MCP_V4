using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Pfr.Core.Clients;
using Pfr.Gui.Controls;

namespace Pfr.Gui.Views;

/// <summary>
/// The WAPI address and accounts of one grid: the Connection tab of a client's Grids page.
/// Saved encrypted with the client (never in snapshots); passwords are masked unless "Show passwords" is ticked.
/// </summary>
public sealed class GridConnectionEditor : UserControl
{
    private const char Mask = '•';

    private readonly ClientWorkspace _workspace;
    private readonly TextBlock _title = Ui.Heading("Connection", 16);
    private readonly TextBlock _message = Ui.Message();
    private readonly StackPanel _form;
    private readonly CheckBox _reveal = new() { Content = "Show passwords", Margin = new Thickness(0, 8, 0, 0) };

    private string? _clientId;
    private ClientGrid? _grid;
    private GridConnection? _saved;

    public GridConnectionEditor(ClientWorkspace workspace)
    {
        _workspace = workspace;

        var save = Ui.Accent("Save connection");
        save.Click += (_, _) => Save();
        var remove = Ui.Confirming("Remove connection", Remove, "Click again to remove the saved connection");
        _reveal.IsCheckedChanged += (_, _) => ApplyReveal();

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
    public TextBox ReadOnlyUser { get; } = Field("User id");
    public TextBox ReadOnlyPassword { get; } = Field("Password", masked: true);
    public TextBox ReadWriteUser { get; } = Field("User id");
    public TextBox ReadWritePassword { get; } = Field("Password", masked: true);

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
        Fill(null);
        Ui.ShowInfo(_message, "Saved connection removed.");
    }

    private void Fill(GridConnection? c)
    {
        Address.Text = c?.Address ?? "";
        Port.Text = c is null ? "" : c.Port.ToString();
        WapiVersion.Text = c?.WapiVersion ?? "";
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
