using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Pfr.Core.Clients;
using Pfr.Core.Network;
using Pfr.Gui.Controls;

namespace Pfr.Gui.Views;

/// <summary>Where the SSH account comes from.</summary>
public enum SshAccountSource
{
    /// <summary>A user id and password typed here.</summary>
    Typed,

    /// <summary>The open grid's saved read-only account.</summary>
    GridReadOnly,

    /// <summary>The open grid's saved read-write account.</summary>
    GridReadWrite,
}

/// <summary>
/// Runs commands on one or more hosts over SSH and shows what they print. Read-only NIOS commands (show, ping,
/// traceroute) unless the engineer allows more. A host's key is pinned the first time it is trusted and checked every
/// time after, before any password is sent.
/// </summary>
public sealed class SshPage : UserControl
{
    private static readonly string[] AccountNames =
        ["Type a user id and password", "The grid's saved read-only account", "The grid's saved read-write account"];

    private readonly Func<ToolContext> _context;
    private readonly TextBox _hosts = Ui.Multiline(
        "Hosts: names or addresses, one per line.\nAn address followed by # and a name is shown under that name.", 90);
    private readonly Button _addMembers = new() { Content = "Add the open grid's members" };
    private readonly NumericUpDown _port = ToolUi.Numeric(1, 65_535, 22);
    private readonly ComboBox _account = new() { Width = 320, ItemsSource = AccountNames, SelectedIndex = 0 };
    private readonly TextBox _user = new() { Width = 180, Watermark = "User id" };
    private readonly TextBox _password = Ui.Password("Password");
    private readonly TextBlock _accountNote = Ui.Muted("");
    private readonly TextBox _commands = Ui.Multiline("One command per line, run in this order on every host.", 90);
    private readonly CheckBox _allowChanges = new() { Content = "Allow commands that change the appliance" };
    private readonly TextBlock _changesWarning = Ui.Muted("These run exactly as typed and can change or restart the appliance.");
    private readonly Button _run = Ui.Accent("Run");
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly Button _forget;
    private readonly Border _keyPanel;
    private readonly TextBlock _keyHeadline = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox _keyFingerprint = new() { IsReadOnly = true, FontFamily = ToolUi.Mono };
    private readonly Button _trust = Ui.Accent("Trust and pin this key");
    private readonly Button _distrust = new() { Content = "Don't trust it" };
    private readonly TextBlock _message = Ui.Message();
    private readonly TextBox _output = ToolUi.Output();

    /// <summary>Host keys used when the grid isn't saved to a client: remembered only until the application closes.</summary>
    private readonly MemorySshHostKeys _sessionKeys = new();

    private readonly object _gate = new();
    private readonly List<SshSessionResult> _done = [];
    private List<SshTarget> _running = [];
    private TaskCompletionSource<bool>? _decision;
    private CancellationTokenSource? _cancel;

    public SshPage(Func<ToolContext> context)
    {
        _context = context;
        _commands.Text = "show version";
        _password.Width = 220;

        _forget = Ui.Confirming("Forget the pinned keys of these hosts", () => ForgetPinnedKeys(), "Click again to forget them");
        _trust.Click += (_, _) => TrustPendingKey();
        _distrust.Click += (_, _) => RejectPendingKey();

        _keyPanel = new Border
        {
            IsVisible = false,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Ui.Divider,
            Background = Ui.Surface,
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    _keyHeadline,
                    _keyFingerprint,
                    Ui.Muted("Compare the fingerprint with the appliance's own before trusting it. A wrong key means the password would go to the wrong machine."),
                    Ui.Row(_trust, _distrust),
                },
            },
        };

        var inputs = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 10) };
        inputs.Children.Add(Ui.Heading("SSH commands"));
        inputs.Children.Add(Ui.Muted("Runs commands on the hosts listed and shows their output. Only those hosts are contacted."));
        inputs.Children.Add(_hosts);
        inputs.Children.Add(Ui.Row(_addMembers, ToolUi.Caption("SSH port"), _port));
        inputs.Children.Add(Ui.Row(ToolUi.Caption("Sign in with"), _account, _user, _password));
        inputs.Children.Add(_accountNote);
        inputs.Children.Add(_commands);
        inputs.Children.Add(_allowChanges);
        inputs.Children.Add(_changesWarning);
        inputs.Children.Add(Ui.Row(_run, _stop, _forget));
        inputs.Children.Add(_keyPanel);
        inputs.Children.Add(_message);
        Content = ToolUi.Page(inputs, _output);

        _changesWarning.Foreground = Ui.Danger;
        _changesWarning.IsVisible = false;
        _allowChanges.IsCheckedChanged += (_, _) => _changesWarning.IsVisible = AllowChanges;
        _account.SelectionChanged += (_, _) => UpdateAccountFields();
        _stop.Click += (_, _) => Stop();
        _addMembers.Click += (_, _) => AddMembers();
        _run.Click += async (_, _) => await RunAsync();
        RefreshAccounts();
    }

    public string HostsText
    {
        get => _hosts.Text ?? "";
        set => _hosts.Text = value;
    }

    public string CommandsText
    {
        get => _commands.Text ?? "";
        set => _commands.Text = value;
    }

    public int Port
    {
        get => (int)(_port.Value ?? 22m);
        set => _port.Value = value;
    }

    public SshAccountSource Source
    {
        get => (SshAccountSource)Math.Max(0, _account.SelectedIndex);
        set => _account.SelectedIndex = (int)value;
    }

    public string User
    {
        get => _user.Text ?? "";
        set => _user.Text = value;
    }

    public string Password
    {
        get => _password.Text ?? "";
        set => _password.Text = value;
    }

    public bool AllowChanges
    {
        get => _allowChanges.IsChecked == true;
        set => _allowChanges.IsChecked = value;
    }

    public IReadOnlyList<string> AccountChoices => AccountNames;

    public string Output => _output.Text ?? "";

    public string MessageText => _message.IsVisible ? _message.Text ?? "" : "";

    /// <summary>The note under the account choice (which grid accounts exist, and where host keys are kept).</summary>
    public string AccountNote => _accountNote.Text ?? "";

    public bool IsRunning => _cancel is not null;

    /// <summary>Stops the run (and any host key question waiting for an answer).</summary>
    public void Stop() => _cancel?.Cancel();

    /// <summary>The host key the engineer is being asked about, or null.</summary>
    public SshHostKeyException? PendingKey { get; private set; }

    public bool KeyQuestionVisible => _keyPanel.IsVisible;

    /// <summary>What the key question shows: the host, the algorithm and the fingerprint.</summary>
    public string KeyQuestionText => $"{_keyHeadline.Text}\n{_keyFingerprint.Text}";

    /// <summary>Notes which of the open grid's accounts are saved, and where host keys will be kept.</summary>
    public void RefreshAccounts()
    {
        var context = _context();
        var connection = SavedConnection(context);

        var notes = new List<string>();
        if (connection is null)
        {
            notes.Add(context.Grid is null ? "No saved grid is open, so the account has to be typed." : $"{context.Grid.Name} has no saved connection, so the account has to be typed.");
        }
        else
        {
            notes.Add($"Saved for {context.Grid!.Name}: "
                      + (connection.ReadOnly is { } ro ? $"read-only account {ro.Username}" : "no read-only account")
                      + ", "
                      + (connection.ReadWrite is { } rw ? $"read-write account {rw.Username}." : "no read-write account."));
        }
        notes.Add(context is { Workspace: not null, Client: { } client }
            ? $"Host keys are pinned for {client.Name}."
            : "This grid isn't saved to a client, so host keys are only remembered until the application closes.");
        _accountNote.Text = string.Join(" ", notes);

        // Offer the saved read-only account to someone who hasn't typed anything.
        if (connection?.ReadOnly is not null && Source == SshAccountSource.Typed && User.Length == 0)
            Source = SshAccountSource.GridReadOnly;
        UpdateAccountFields();
    }

    /// <summary>Adds the open grid's members to the host list. Returns how many were added.</summary>
    public int AddMembers()
    {
        if (_context().Inventory is not { } inventory)
        {
            Ui.ShowError(_message, "No grid is open. Open one in Inventory and its members can be added here.");
            return 0;
        }

        var listed = PingTargets.Parse(HostsText).Select(t => t.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = inventory.Members
            .Select(m => new PingTarget(m.HostName, m.Ipv4Address ?? m.Ipv6Address ?? m.HostName))
            .Where(t => !listed.Contains(t.Host))
            .ToList();
        if (added.Count == 0)
        {
            Ui.ShowInfo(_message, $"{inventory.GridName} has no members that aren't listed already.");
            return 0;
        }

        var existing = HostsText.TrimEnd();
        HostsText = (existing.Length > 0 ? existing + "\n" : "")
                    + string.Join("\n", added.Select(t => t.Host == t.Label ? t.Host : $"{t.Host}  # {t.Label}"));
        Ui.ShowInfo(_message, $"Added {added.Count} member{(added.Count == 1 ? "" : "s")} of {inventory.GridName}.");
        return added.Count;
    }

    /// <summary>
    /// Runs the commands on every host. An SSH client can be supplied (the tests do). A host whose key is not pinned stops
    /// the run and asks, in the window, whether to trust the key it presented.
    /// </summary>
    public async Task RunAsync(ISshClient? client = null)
    {
        if (_cancel is not null) return;
        _message.IsVisible = false;

        _cancel = new CancellationTokenSource();
        var cancel = _cancel;
        SetRunning(true);
        try
        {
            var targets = PingTargets.Parse(HostsText).Select(t => new SshTarget(t.Label, t.Host, Port)).ToList();
            if (targets.Count == 0) throw new ArgumentException("Type at least one host to run the commands on.");

            var commands = CommandsText.Split('\n').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            if (commands.Count == 0) throw new ArgumentException("Type at least one command to run.");

            var account = AccountToUse();
            var runner = new SshRunner(client ?? new SshNetClient(), Keys());

            lock (_gate)
            {
                _running = targets;
                _done.Clear();
            }
            _output.Text = "";
            Ui.ShowInfo(_message, $"Running on {targets.Count} host{(targets.Count == 1 ? "" : "s")}…");

            var progress = new Reporter(session =>
            {
                lock (_gate) _done.Add(session);
                Dispatcher.UIThread.Post(ShowProgress);
            });
            var sessions = await runner.RunAsync(targets, account, commands, new SshRunOptions { AllowChanges = AllowChanges },
                ConfirmKeyAsync, progress, cancel.Token);

            _output.Text = SshReport.Text(sessions);
            var succeeded = sessions.Count(s => s.Succeeded);
            Ui.ShowInfo(_message, $"{succeeded} of {sessions.Count} host{(sessions.Count == 1 ? "" : "s")} done without a problem.");
        }
        catch (ArgumentException ex)
        {
            Ui.ShowError(_message, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Ui.ShowInfo(_message, "Stopped.");
        }
        finally
        {
            HideKeyQuestion(decision: false);
            _cancel = null;
            cancel.Dispose();
            SetRunning(false);
        }
    }

    /// <summary>The engineer trusts the key being asked about: it is pinned and the run goes on.</summary>
    public void TrustPendingKey() => HideKeyQuestion(decision: true);

    /// <summary>The engineer does not trust it: nothing is pinned and that host is skipped.</summary>
    public void RejectPendingKey() => HideKeyQuestion(decision: false);

    /// <summary>Forgets the pinned keys of the hosts listed, so they have to be confirmed again. Returns how many were forgotten.</summary>
    public int ForgetPinnedKeys()
    {
        var keys = Keys();
        var forgotten = 0;
        foreach (var target in PingTargets.Parse(HostsText))
        {
            if (keys.Find(target.Host, Port) is null) continue;
            keys.Forget(target.Host, Port);
            forgotten++;
        }

        Ui.ShowInfo(_message, forgotten == 0
            ? "None of the hosts listed has a pinned key."
            : $"Forgot the pinned key of {forgotten} host{(forgotten == 1 ? "" : "s")}. {(forgotten == 1 ? "It" : "They")} will be asked about again next time.");
        return forgotten;
    }

    private Task<bool> ConfirmKeyAsync(SshHostKeyException ex, CancellationToken ct)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => decision.TrySetResult(false));

        if (Dispatcher.UIThread.CheckAccess()) ShowKeyQuestion(ex, decision);
        else Dispatcher.UIThread.Post(() => ShowKeyQuestion(ex, decision));
        return decision.Task;
    }

    private void ShowKeyQuestion(SshHostKeyException ex, TaskCompletionSource<bool> decision)
    {
        _decision = decision;
        PendingKey = ex;
        _keyHeadline.Text = $"{ex.Target.Host} has no pinned host key. It presented this {ex.Presented.Algorithm} key:";
        _keyFingerprint.Text = ex.Presented.Fingerprint;
        _keyPanel.IsVisible = true;
    }

    private void HideKeyQuestion(bool decision)
    {
        _decision?.TrySetResult(decision);
        _decision = null;
        PendingKey = null;
        _keyPanel.IsVisible = false;
    }

    private void ShowProgress()
    {
        List<SshSessionResult> sessions;
        lock (_gate)
        {
            sessions = _done.OrderBy(s => _running.IndexOf(s.Target)).ToList();
        }
        if (_cancel is not null) _output.Text = SshReport.Text(sessions);
    }

    private SshAccount AccountToUse()
    {
        if (Source == SshAccountSource.Typed)
        {
            if (User.Trim().Length == 0 || Password.Length == 0)
                throw new ArgumentException("Type the user id and password to sign in with, or use the open grid's saved account.");
            return new SshAccount(User.Trim(), Password);
        }

        var readWrite = Source == SshAccountSource.GridReadWrite;
        var connection = SavedConnection(_context());
        var credential = readWrite ? connection?.ReadWrite : connection?.ReadOnly;
        return credential is null
            ? throw new ArgumentException($"The open grid has no saved {(readWrite ? "read-write" : "read-only")} account. Type a user id and password instead.")
            : new SshAccount(credential.Username, credential.Password);
    }

    private static GridConnection? SavedConnection(ToolContext context) =>
        context is { Workspace: { } workspace, Client: { } client, Grid: { } grid } ? workspace.GetConnection(client.Id, grid.Id) : null;

    /// <summary>The client's own store when the grid is saved to one; otherwise a store that lasts until the app closes.</summary>
    private ISshHostKeyStore Keys() =>
        _context() is { Workspace: { } workspace, Client: { } client } ? new ClientSshHostKeys(workspace, client.Id) : _sessionKeys;

    private void UpdateAccountFields()
    {
        var typed = Source == SshAccountSource.Typed;
        _user.IsEnabled = typed;
        _password.IsEnabled = typed;
    }

    private void SetRunning(bool running)
    {
        _run.IsEnabled = !running;
        _addMembers.IsEnabled = !running;
        _forget.IsEnabled = !running;
        _stop.IsEnabled = running;
    }

    private sealed class Reporter(Action<SshSessionResult> onReport) : IProgress<SshSessionResult>
    {
        public void Report(SshSessionResult value) => onReport(value);
    }
}
