using Avalonia;
using Avalonia.Controls;
using Pfr.Core.Clients;
using Pfr.Core.Wapi;
using Pfr.Gui.Controls;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Views;

/// <summary>
/// A grid's live state read over WAPI: members, HA state, services, NIOS versions and hotfixes. It uses the grid's
/// saved read-only account and pinned certificate, and only reads. The last result for each grid is kept while the
/// client page is open, so switching between grids doesn't lose it. Nothing is stored.
/// </summary>
public sealed class LiveStatusView : UserControl
{
    private readonly ClientWorkspace _workspace;
    private readonly Dictionary<string, LiveGridStatus> _results = new(StringComparer.Ordinal);
    private readonly TextBlock _title = Ui.Heading("Live status", 16);
    private readonly TextBlock _summary = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _message = Ui.Message();
    private readonly TextBlock _notes = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7, IsVisible = false };
    private readonly ContentControl _body = new();
    private readonly Button _refresh = Ui.Accent("Refresh status");

    private string? _clientId;
    private ClientGrid? _grid;

    public LiveStatusView(ClientWorkspace workspace)
    {
        _workspace = workspace;
        _refresh.Click += async (_, _) => await RefreshAsync();

        var top = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(4, 12, 4, 6),
            Children =
            {
                _title,
                Ui.Muted("Reads the grid over WAPI with its read-only account and pinned certificate. Nothing here is saved."),
                Ui.Row(_refresh),
                _summary,
                _message,
                _notes,
            },
        };
        DockPanel.SetDock(top, Dock.Top);
        Content = new DockPanel { Children = { top, _body } };
        Bind(null, null);
    }

    /// <summary>The status being shown, or null if none has been read for this grid yet.</summary>
    public LiveGridStatus? Current { get; private set; }

    public string SummaryText => _summary.Text ?? "";

    /// <summary>The last message shown (empty when none).</summary>
    public string MessageText => _message.IsVisible ? _message.Text ?? "" : "";

    /// <summary>What the grid did not offer or answer (empty when nothing was missing).</summary>
    public string NotesText => _notes.IsVisible ? _notes.Text ?? "" : "";

    /// <summary>Shows the last result for <paramref name="grid"/>, or a prompt to refresh; reads nothing from the grid.</summary>
    public void Bind(string? clientId, ClientGrid? grid)
    {
        _clientId = clientId;
        _grid = grid;
        _message.IsVisible = false;
        _refresh.IsEnabled = clientId is not null && grid is not null;
        _title.Text = grid is null ? "Live status: select a grid" : $"Live status of {grid.Name}";

        if (grid is not null && _results.TryGetValue(grid.Id, out var known)) Show(known);
        else Clear(grid is null ? "" : "Not read yet. Click Refresh status to read this grid's live state over WAPI.");
    }

    /// <summary>
    /// Reads the grid's live state and shows it. Returns false (with a message) if it can't: no saved connection, an
    /// untrusted certificate (trust it on the Connection tab), a refused account, or the grid not answering.
    /// </summary>
    public async Task<bool> RefreshAsync()
    {
        if (_clientId is null || _grid is null)
        {
            Ui.ShowError(_message, "Select a grid first.");
            return false;
        }

        var gridId = _grid.Id;
        GridConnection? connection;
        try
        {
            connection = _workspace.GetConnection(_clientId, gridId);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException
                                       or System.Text.Json.JsonException or InvalidDataException)
        {
            Ui.ShowError(_message, $"Could not read the saved connection: {ex.Message}");
            return false;
        }

        if (connection?.ReadOnly is null)
        {
            Ui.ShowError(_message, "Save this grid's connection with a read-only account on the Connection tab first.");
            return false;
        }

        Ui.ShowInfo(_message, "Reading the grid…");
        _refresh.IsEnabled = false;
        try
        {
            using var wapi = WapiClient.ForReadOnly(connection);
            var status = await LiveStatusCollector.CollectAsync(wapi);
            _results[gridId] = status;
            if (_grid?.Id == gridId) Show(status);
            return true;
        }
        catch (WapiCertificateException ex)
        {
            var kind = ex.Problem == CertificateProblem.NotPinned ? "has not been trusted yet" : "is different from the pinned one";
            Ui.ShowError(_message, $"The grid's certificate {kind} (SHA-256 {CertificatePin.Format(ex.ObservedFingerprint)}). " +
                                   "No user id or password was sent. Review and trust it on the Connection tab (Test connection), then refresh.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is a UI event: report the problem instead of letting it end the app.
            Ui.ShowError(_message, ex.Message);
            return false;
        }
        finally
        {
            _refresh.IsEnabled = _clientId is not null && _grid is not null;
        }
    }

    private void Show(LiveGridStatus status)
    {
        Current = status;
        _message.IsVisible = false;
        _summary.Text = $"{(status.GridName is { Length: > 0 } name ? $"Grid \"{name}\"" : "Grid")} · WAPI v{status.ApiVersion} · " +
                        $"{LiveStatusReport.HealthText(status.Health)} · {status.Members.Count} member{(status.Members.Count == 1 ? "" : "s")} · " +
                        $"NIOS {status.VersionSummary} · as of {status.CollectedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        _notes.Text = string.Join("\n", status.Notes);
        _notes.IsVisible = status.Notes.Count > 0;
        _body.Content = new TreeTable(LiveStatusTables.Members(status));
    }

    private void Clear(string prompt)
    {
        Current = null;
        _summary.Text = prompt;
        _notes.Text = "";
        _notes.IsVisible = false;
        _body.Content = null;
    }
}
