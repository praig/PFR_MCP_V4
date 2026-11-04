using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Pfr.Core.Network;
using Pfr.Gui.Controls;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Views;

/// <summary>
/// Pings a list of hosts and shows reachability, loss and round-trip times as they come in. "Add the open grid's
/// members" fills the list from the grid shown in Inventory, so a whole grid can be checked in one go.
/// </summary>
public sealed class PingPage : UserControl
{
    private readonly Func<ToolContext> _context;
    private readonly TextBox _targets = Ui.Multiline(
        "Host names or addresses, one per line or separated by commas.\nAn address followed by # and a name is shown under that name.", 110);
    private readonly NumericUpDown _count = ToolUi.Numeric(1, 100, 4);
    private readonly NumericUpDown _timeout = ToolUi.Numeric(100, 10_000, 2_000, 100);
    private readonly CheckBox _ipv6 = new() { Content = "Prefer IPv6" };
    private readonly Button _ping = Ui.Accent("Ping");
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly Button _addMembers = new() { Content = "Add the open grid's members" };
    private readonly Button _clear = new() { Content = "Clear" };
    private readonly TextBlock _message = Ui.Message();
    private readonly ContentControl _results = new();
    private readonly object _gate = new();

    private PingResult[] _latest = [];
    private CancellationTokenSource? _cancel;
    private bool _refreshQueued;

    public PingPage(Func<ToolContext> context)
    {
        _context = context;

        var inputs = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 10) };
        inputs.Children.Add(Ui.Heading("Ping"));
        inputs.Children.Add(Ui.Muted("Checks that hosts answer and how fast. Only the hosts listed here are contacted."));
        inputs.Children.Add(_targets);
        inputs.Children.Add(Ui.Row(_addMembers, _clear));
        inputs.Children.Add(Ui.Row(ToolUi.Caption("Pings per host"), _count, ToolUi.Caption("Wait for a reply (ms)"), _timeout, _ipv6));
        inputs.Children.Add(Ui.Row(_ping, _stop));
        inputs.Children.Add(_message);
        Content = ToolUi.Page(inputs, _results);

        _ping.Click += async (_, _) => await RunAsync();
        _stop.Click += (_, _) => Stop();
        _addMembers.Click += (_, _) => AddMembers();
        _clear.Click += (_, _) =>
        {
            _targets.Text = "";
            _message.IsVisible = false;
        };
    }

    /// <summary>What is typed in the target box.</summary>
    public string TargetsText
    {
        get => _targets.Text ?? "";
        set => _targets.Text = value;
    }

    public int PingCount
    {
        get => (int)(_count.Value ?? 4m);
        set => _count.Value = value;
    }

    public int TimeoutMs
    {
        get => (int)(_timeout.Value ?? 2000m);
        set => _timeout.Value = value;
    }

    /// <summary>Wait between one ping and the next to the same host. Fixed at a second in the window; tests shorten it.</summary>
    public int IntervalMs { get; set; } = 1000;

    public bool PreferIpv6
    {
        get => _ipv6.IsChecked == true;
        set => _ipv6.IsChecked = value;
    }

    public bool IsRunning => _cancel is not null;

    /// <summary>Stops pinging; what has been measured so far stays on show.</summary>
    public void Stop() => _cancel?.Cancel();

    public string MessageText => _message.IsVisible ? _message.Text ?? "" : "";

    /// <summary>The latest result for each target, in the order the targets were listed.</summary>
    public IReadOnlyList<PingResult> Results
    {
        get
        {
            lock (_gate) return _latest.ToArray();
        }
    }

    /// <summary>The results as shown, or null before anything has been pinged.</summary>
    public FilterableTable? Table => _results.Content as FilterableTable;

    /// <summary>Adds the open grid's members (and each node of an HA pair) to the list. Returns how many were added.</summary>
    public int AddMembers()
    {
        if (_context().Inventory is not { } inventory)
        {
            Ui.ShowError(_message, "No grid is open. Open one in Inventory and its members can be added here.");
            return 0;
        }

        var listed = PingTargets.Parse(TargetsText).Select(t => t.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = PingTargets.FromMembers(inventory).Where(t => !listed.Contains(t.Host)).ToList();
        if (added.Count == 0)
        {
            Ui.ShowInfo(_message, $"{inventory.GridName} has no members that aren't listed already.");
            return 0;
        }

        var existing = TargetsText.TrimEnd();
        TargetsText = (existing.Length > 0 ? existing + "\n" : "")
                      + string.Join("\n", added.Select(t => t.Host == t.Label ? t.Host : $"{t.Host}  # {t.Label}"));
        Ui.ShowInfo(_message, $"Added {added.Count} address{(added.Count == 1 ? "" : "es")} from {inventory.GridName}.");
        return added.Count;
    }

    /// <summary>Pings everything listed. A pinger and a name resolver can be supplied (the tests do).</summary>
    public async Task RunAsync(IPinger? pinger = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        if (_cancel is not null) return;

        var targets = PingTargets.Parse(TargetsText);
        if (targets.Count == 0)
        {
            Ui.ShowError(_message, "Type at least one host name or address to ping.");
            return;
        }

        var settings = new PingSettings
        {
            Count = PingCount,
            Timeout = TimeSpan.FromMilliseconds(TimeoutMs),
            Interval = TimeSpan.FromMilliseconds(IntervalMs),
            PreferIpv6 = PreferIpv6,
        };

        _cancel = new CancellationTokenSource();
        var cancel = _cancel;
        SetRunning(true);
        _message.IsVisible = false;
        lock (_gate) _latest = targets.Select(t => new PingResult(t, null, null, [], false)).ToArray();
        ShowResults();

        try
        {
            var progress = new Reporter(result =>
            {
                lock (_gate)
                {
                    var index = Array.FindIndex(_latest, r => r.Target == result.Target);
                    if (index >= 0) _latest[index] = result;
                }
                ScheduleRefresh();
            });

            var results = await PingRunner.RunAsync(targets, settings, pinger, resolve, progress, cancel.Token);
            lock (_gate) _latest = results.ToArray();
            ShowResults();

            var answered = results.Count(r => r.Received > 0);
            Ui.ShowInfo(_message,
                $"{answered} of {results.Count} answered{(cancel.IsCancellationRequested ? " before it was stopped" : "")}.");
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        _ping.IsEnabled = !running;
        _addMembers.IsEnabled = !running;
        _stop.IsEnabled = running;
    }

    /// <summary>Redraws the table soon, but not for every single ping.</summary>
    private void ScheduleRefresh()
    {
        lock (_gate)
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_gate) _refreshQueued = false;
            if (_cancel is not null) ShowResults();
        }, DispatcherPriority.Background);
    }

    private void ShowResults()
    {
        PingResult[] snapshot;
        lock (_gate) snapshot = _latest.ToArray();
        _results.Content = new FilterableTable(BuildTable(snapshot));
    }

    private static TableDefinition BuildTable(IReadOnlyList<PingResult> results) => new(
        "ping results",
        [
            new TableColumn("Target", ColumnKind.Text, 230),
            new TableColumn("Address", ColumnKind.Ip, 170),
            new TableColumn("Sent", ColumnKind.Number, 70),
            new TableColumn("Received", ColumnKind.Number, 90),
            new TableColumn("Loss", ColumnKind.Number, 70),
            new TableColumn("Min", ColumnKind.Number, 80),
            new TableColumn("Average", ColumnKind.Number, 90),
            new TableColumn("Max", ColumnKind.Number, 80),
            new TableColumn("Status", ColumnKind.Text, 260),
        ],
        results.Select(r => new TableRow(
        [
            r.Target.Label, r.Address ?? "", r.Sent.ToString(), r.Received.ToString(),
            r.Sent == 0 ? "" : $"{r.LossPercent:0}%",
            PingReport.Rtt(r.Min), PingReport.Rtt(r.Average), PingReport.Rtt(r.Max), r.Status,
        ])).ToList());

    private sealed class Reporter(Action<PingResult> onReport) : IProgress<PingResult>
    {
        public void Report(PingResult value) => onReport(value);
    }
}
