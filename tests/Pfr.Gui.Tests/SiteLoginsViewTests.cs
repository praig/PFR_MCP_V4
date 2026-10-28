using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pfr.Core.Clients;
using Pfr.Core.Storage;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class SiteLoginsViewTests
{
    private const string Password = "correct horse battery";
    private const string SitePassword = "Portal-Sup3rSecret!";
    private const string ApiKey = "AKIA-Sup3rSecretApiKey-123";
    private static readonly string? ScreenshotDir = Environment.GetEnvironmentVariable("PFR_SCREENSHOT_DIR");

    private static void Snapshot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        if (ScreenshotDir is null) return;
        Directory.CreateDirectory(ScreenshotDir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, name + ".png"));
    }

    private static (string Folder, Client Client) StoreWithClient(Action<ClientWorkspace, Client>? seed = null)
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var ws = new ClientWorkspace(store);
        var client = ws.CreateClient("Acme Corp");
        seed?.Invoke(ws, client);
        store.Lock();
        return (folder, client);
    }

    private static (MainWindow Window, ClientDetailView Detail) OpenClient(string folder, Client client)
    {
        var window = TestWindows.New(folder);
        window.Show();
        Assert.True(window.Clients.TryUnlock(Password));
        window.Clients.SelectClient(client.Id);
        var detail = Assert.IsType<ClientDetailView>(window.Clients.CurrentDetail);
        detail.SelectTab(3);
        return (window, detail);
    }

    private static void Fill(SiteLoginsEditor editor, string title, string url = "", string user = "", string password = "", string apiKey = "", string notes = "")
    {
        editor.TitleBox.Text = title;
        editor.UrlBox.Text = url;
        editor.UserBox.Text = user;
        editor.PasswordBox.Text = password;
        editor.ApiKeyBox.Text = apiKey;
        editor.NotesBox.Text = notes;
    }

    [AvaloniaFact]
    public void A_login_is_added_listed_and_shown_again_when_selected()
    {
        var (folder, client) = StoreWithClient();
        var (window, detail) = OpenClient(folder, client);
        var editor = detail.LoginsEditor;
        Assert.Empty(editor.Entries);

        Fill(editor, "Acme VPN portal", "https://vpn.acme.test", "pfr-engineer", SitePassword, ApiKey, "MFA via authenticator app.");
        Assert.True(editor.Save());
        Assert.Equal("Login added.", editor.MessageText);
        Snapshot(window, "32-client-logins");

        var saved = Assert.Single(window.Clients.Workspace!.GetLogins(client.Id));
        Assert.Equal(("pfr-engineer", SitePassword, ApiKey), (saved.Username, saved.Password, saved.ApiKey));
        Assert.Equal(saved.Id, Assert.Single(editor.Entries).Id);
        Assert.Equal(saved.Id, editor.Selected?.Id); // stays selected after saving

        // A second login; switching between them fills the form each time.
        editor.NewEntry();
        Assert.Equal("", editor.TitleBox.Text);
        Fill(editor, "Cloud API", apiKey: "other-key");
        Assert.True(editor.Save());
        Assert.Equal(["Acme VPN portal", "Cloud API"], editor.Entries.Select(l => l.Title));

        editor.Select(saved.Id);
        Assert.Equal("https://vpn.acme.test", editor.UrlBox.Text);
        Assert.Equal(SitePassword, editor.PasswordBox.Text);
        Assert.Equal("MFA via authenticator app.", editor.NotesBox.Text);
    }

    [AvaloniaFact]
    public void Existing_logins_appear_when_the_client_is_opened()
    {
        var (folder, client) = StoreWithClient((ws, c) =>
        {
            ws.SaveLogin(c.Id, new SiteLogin { Title = "Ticketing", Username = "pat", Password = SitePassword });
            ws.SaveLogin(c.Id, new SiteLogin { Title = "Cloud API", ApiKey = ApiKey });
        });
        var (_, detail) = OpenClient(folder, client);

        Assert.Equal(["Cloud API", "Ticketing"], detail.LoginsEditor.Entries.Select(l => l.Title));
    }

    [AvaloniaFact]
    public void Password_and_api_key_are_masked_until_show_is_ticked()
    {
        var (folder, client) = StoreWithClient((ws, c) =>
            ws.SaveLogin(c.Id, new SiteLogin { Title = "Portal", Password = SitePassword, ApiKey = ApiKey }));
        var (_, detail) = OpenClient(folder, client);
        var editor = detail.LoginsEditor;
        editor.Select(editor.Entries.Single().Id);

        Assert.False(editor.SecretsRevealed);
        Assert.Equal('•', editor.PasswordBox.PasswordChar);
        Assert.Equal('•', editor.ApiKeyBox.PasswordChar);
        Assert.Equal('\0', editor.UserBox.PasswordChar); // only the secrets are masked

        editor.SecretsRevealed = true;
        Assert.Equal('\0', editor.PasswordBox.PasswordChar);
        Assert.Equal('\0', editor.ApiKeyBox.PasswordChar);

        // Selecting another login (or the same again) masks them once more.
        editor.NewEntry();
        Assert.False(editor.SecretsRevealed);
        Assert.Equal('•', editor.PasswordBox.PasswordChar);
    }

    [AvaloniaFact]
    public void A_missing_or_duplicate_title_shows_a_message_and_saves_nothing()
    {
        var (folder, client) = StoreWithClient((ws, c) => ws.SaveLogin(c.Id, new SiteLogin { Title = "Portal" }));
        var (window, detail) = OpenClient(folder, client);
        var editor = detail.LoginsEditor;
        var ws = window.Clients.Workspace!;

        Fill(editor, "   ", user: "pat", password: SitePassword);
        Assert.False(editor.Save());
        Assert.Contains("needs a title", editor.MessageText);

        Fill(editor, "portal", user: "pat", password: SitePassword);
        Assert.False(editor.Save());
        Assert.Contains("already has a login titled", editor.MessageText);
        Assert.DoesNotContain(SitePassword, editor.MessageText);
        Snapshot(window, "33-client-logins-error");

        Assert.Equal("Portal", Assert.Single(ws.GetLogins(client.Id)).Title);
    }

    [AvaloniaFact]
    public void Editing_a_selected_login_updates_it_instead_of_adding_another()
    {
        var (folder, client) = StoreWithClient((ws, c) =>
            ws.SaveLogin(c.Id, new SiteLogin { Title = "Portal", Username = "pat", Password = "old" }));
        var (window, detail) = OpenClient(folder, client);
        var editor = detail.LoginsEditor;
        var original = editor.Entries.Single();

        editor.Select(original.Id);
        editor.PasswordBox.Text = "new";
        Assert.True(editor.Save());
        Assert.Equal("Login saved.", editor.MessageText);

        var saved = Assert.Single(window.Clients.Workspace!.GetLogins(client.Id));
        Assert.Equal((original.Id, "new", "pat"), (saved.Id, saved.Password, saved.Username));
    }

    [AvaloniaFact]
    public void Deleting_a_login_removes_it_and_clears_the_form()
    {
        var (folder, client) = StoreWithClient((ws, c) =>
        {
            ws.SaveLogin(c.Id, new SiteLogin { Title = "One", Username = "a" });
            ws.SaveLogin(c.Id, new SiteLogin { Title = "Two", Username = "b" });
        });
        var (window, detail) = OpenClient(folder, client);
        var editor = detail.LoginsEditor;

        editor.Remove();
        Assert.Contains("Select a login", editor.MessageText);
        Assert.Equal(2, window.Clients.Workspace!.GetLogins(client.Id).Count);

        editor.Select(editor.Entries.Single(l => l.Title == "One").Id);
        editor.Remove();

        Assert.Equal("Two", Assert.Single(window.Clients.Workspace!.GetLogins(client.Id)).Title);
        Assert.Equal("Two", Assert.Single(editor.Entries).Title);
        Assert.Equal("", editor.TitleBox.Text);
        Assert.Null(editor.Selected);
        Assert.Equal("Login deleted.", editor.MessageText);
    }
}
