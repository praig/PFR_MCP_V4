using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Clients;
using Pfr.Gui.Controls;

namespace Pfr.Gui.Views;

/// <summary>
/// A client's logins and API keys for its own sites and services: the "Logins &amp; keys" tab of the client page.
/// Saved encrypted with the client; passwords and API keys are masked unless "Show secrets" is ticked.
/// </summary>
public sealed class SiteLoginsEditor : UserControl
{
    private const char Mask = '•';

    private readonly ClientWorkspace _workspace;
    private readonly string _clientId;
    private readonly ListBox _list;
    private readonly TextBlock _message = Ui.Message();
    private readonly CheckBox _reveal = new() { Content = "Show password and API key", Margin = new Thickness(0, 8, 0, 0) };

    private SiteLogin? _editing;

    public SiteLoginsEditor(ClientWorkspace workspace, string clientId)
    {
        _workspace = workspace;
        _clientId = clientId;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<SiteLogin>((l, _) => Row(l), supportsRecycling: false),
        };
        _list.SelectionChanged += (_, _) => Load(_list.SelectedItem as SiteLogin);
        _reveal.IsCheckedChanged += (_, _) => ApplyReveal();

        var newEntry = new Button { Content = "New login" };
        newEntry.Click += (_, _) => NewEntry();
        var save = Ui.Accent("Save");
        save.Click += (_, _) => Save();
        var delete = Ui.Confirming("Delete", Remove, "Click again to delete this login");

        var listPane = new DockPanel
        {
            Children =
            {
                Docked(new StackPanel
                {
                    Spacing = 6,
                    Margin = new Thickness(0, 0, 0, 6),
                    Children = { Ui.Heading("Logins and API keys", 16), Ui.Row(newEntry) },
                }, Dock.Top),
                _list,
            },
        };

        var form = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(0, 0, 8, 0),
                Children =
                {
                    Ui.Label("Title"), TitleBox,
                    Ui.Label("Site or service"), UrlBox,
                    Ui.Label("User id"), UserBox,
                    Ui.Label("Password"), PasswordBox,
                    Ui.Label("API key"), ApiKeyBox,
                    Ui.Label("Notes"), NotesBox,
                    _reveal,
                    new Border { Height = 8 },
                    Ui.Row(save, delete),
                    _message,
                },
            },
        };

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("340,16,*") };
        split.Children.Add(listPane);
        Grid.SetColumn(form, 2);
        split.Children.Add(form);

        var intro = Ui.Muted("Logins and API keys for this client's own sites and services (portals, VPNs, cloud consoles, " +
                             "ticketing). Stored encrypted with your master password, never in snapshots or reports. " +
                             "User id, password and API key are each optional.");
        intro.Margin = new Thickness(4, 12, 4, 12);
        DockPanel.SetDock(intro, Dock.Top);
        Content = new DockPanel { Children = { intro, split } };

        Refresh();
    }

    public TextBox TitleBox { get; } = Field("Title, e.g. Acme VPN portal");
    public TextBox UrlBox { get; } = Field("URL, host name or app name");
    public TextBox UserBox { get; } = Field("User id");
    public TextBox PasswordBox { get; } = Field("Password", masked: true);
    public TextBox ApiKeyBox { get; } = Field("API key", masked: true);
    public TextBox NotesBox { get; } = NotesField();

    /// <summary>The client's logins as listed (by title).</summary>
    public IReadOnlyList<SiteLogin> Entries => _list.ItemsSource as IReadOnlyList<SiteLogin> ?? [];

    /// <summary>The login being edited, or null for a new one.</summary>
    public SiteLogin? Selected => _editing;

    /// <summary>True while the password and API key are shown as plain text.</summary>
    public bool SecretsRevealed
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
        MaxWidth = 460,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        PasswordChar = masked ? Mask : '\0',
    };

    private static TextBox NotesField() => new()
    {
        Watermark = "Anything else worth remembering: MFA, who owns the account, rotation date",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 90,
        MaxWidth = 460,
        VerticalContentAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static Control Row(SiteLogin? login)
    {
        if (login is null) return new TextBlock();
        var parts = new List<string>();
        if (login.Url.Length > 0) parts.Add(login.Url);
        if (login.Username.Length > 0) parts.Add($"user {login.Username}");
        if (login.Password.Length > 0) parts.Add("password");
        if (login.ApiKey.Length > 0) parts.Add("API key");
        return new StackPanel
        {
            Margin = new Thickness(0, 4),
            Children =
            {
                new TextBlock { Text = login.Title, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = string.Join(" · ", parts),
                    Opacity = 0.7,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsVisible = parts.Count > 0,
                },
            },
        };
    }

    /// <summary>Selects a login in the list (showing it in the form).</summary>
    public void Select(string loginId) =>
        _list.SelectedItem = Entries.FirstOrDefault(l => l.Id == loginId);

    /// <summary>Clears the form to add a new login.</summary>
    public void NewEntry()
    {
        _list.SelectedItem = null;
        Load(null);
    }

    /// <summary>Saves the form as a new login, or as changes to the selected one. Returns false (with a message) if it can't.</summary>
    public bool Save()
    {
        var login = new SiteLogin();
        if (_editing is not null) login.Id = _editing.Id;
        login.Title = TitleBox.Text ?? "";
        login.Url = UrlBox.Text ?? "";
        login.Username = UserBox.Text ?? "";
        login.Password = PasswordBox.Text ?? "";
        login.ApiKey = ApiKeyBox.Text ?? "";
        login.Notes = NotesBox.Text ?? "";

        try
        {
            var wasNew = _editing is null;
            var saved = _workspace.SaveLogin(_clientId, login);
            Refresh(saved.Id);
            Ui.ShowInfo(_message, wasNew ? "Login added." : "Login saved.");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException)
        {
            Ui.ShowError(_message, ex.Message);
            return false;
        }
    }

    /// <summary>Deletes the login being edited.</summary>
    public void Remove()
    {
        if (_editing is null)
        {
            Ui.ShowError(_message, "Select a login to delete.");
            return;
        }

        try
        {
            _workspace.DeleteLogin(_clientId, _editing.Id);
            Refresh();
            Ui.ShowInfo(_message, "Login deleted.");
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            Ui.ShowError(_message, ex.Message);
        }
    }

    private void Refresh(string? selectId = null)
    {
        List<SiteLogin> logins;
        string? problem = null;
        try
        {
            logins = _workspace.GetLogins(_clientId);
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or InvalidDataException)
        {
            logins = [];
            problem = $"Could not read the saved logins: {ex.Message}";
        }

        var selected = logins.FirstOrDefault(l => l.Id == selectId);
        _list.ItemsSource = logins;
        _list.SelectedItem = selected;
        Load(selected); // explicit, so the form is right whether or not the list raised a selection change
        if (problem is not null) Ui.ShowError(_message, problem);
    }

    private void Load(SiteLogin? login)
    {
        _editing = login;
        TitleBox.Text = login?.Title ?? "";
        UrlBox.Text = login?.Url ?? "";
        UserBox.Text = login?.Username ?? "";
        PasswordBox.Text = login?.Password ?? "";
        ApiKeyBox.Text = login?.ApiKey ?? "";
        NotesBox.Text = login?.Notes ?? "";
        SecretsRevealed = false;
        _message.IsVisible = false;
    }

    private void ApplyReveal()
    {
        var character = SecretsRevealed ? '\0' : Mask;
        PasswordBox.PasswordChar = character;
        ApiKeyBox.PasswordChar = character;
    }
}
