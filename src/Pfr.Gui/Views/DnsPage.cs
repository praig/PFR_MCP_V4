using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Pfr.Core.Network;
using Pfr.Gui.Controls;

namespace Pfr.Gui.Views;

/// <summary>
/// dig-style lookups against a server the engineer chooses: any record type, UDP or TCP, DNSSEC and recursion flags.
/// Several names at once (one per line) are looked up as a batch, an address is looked up in reverse, and "Compare"
/// asks every DNS member of the open grid the same question to show where they differ.
/// </summary>
public sealed partial class DnsPage : UserControl
{
    private static readonly string[] RecordTypes =
    [
        "A", "AAAA", "CNAME", "MX", "NS", "SOA", "TXT", "SRV", "PTR", "CAA", "DNAME", "NAPTR",
        "DS", "DNSKEY", "RRSIG", "NSEC", "NSEC3PARAM", "TLSA", "SSHFP", "SVCB", "HTTPS", "SPF", "ANY",
    ];

    private readonly Func<ToolContext> _context;
    private readonly TextBox _server = new() { Width = 300, Watermark = "DNS server: an address or a name" };
    private readonly ComboBox _picker = new() { Width = 300, PlaceholderText = "or pick one of the grid's DNS members" };
    private readonly TextBox _name = Ui.Multiline(
        "A name to look up, or an address for a reverse lookup.\nSeveral lines look each one up (a record type after a name overrides the one above).", 80);
    private readonly ComboBox _type = new() { Width = 130, ItemsSource = RecordTypes.ToList(), SelectedIndex = 0 };
    private readonly ComboBox _class = new() { Width = 90, ItemsSource = new[] { "IN", "CH" }, SelectedIndex = 0 };
    private readonly CheckBox _tcp = new() { Content = "Use TCP" };
    private readonly CheckBox _dnssec = new() { Content = "DNSSEC records" };
    private readonly CheckBox _noRecurse = new() { Content = "No recursion" };
    private readonly CheckBox _short = new() { Content = "Answers only" };
    private readonly Button _lookUp = Ui.Accent("Look up");
    private readonly Button _compare = new() { Content = "Compare the grid's DNS members" };
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly TextBlock _message = Ui.Message();
    private readonly TextBox _output = ToolUi.Output();

    private CancellationTokenSource? _cancel;

    public DnsPage(Func<ToolContext> context)
    {
        _context = context;

        var inputs = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 10) };
        inputs.Children.Add(Ui.Heading("DNS lookup"));
        inputs.Children.Add(Ui.Muted("Asks the server you choose, and only that server. Output is in the form dig writes it."));
        inputs.Children.Add(Ui.Row(ToolUi.Caption("Server"), _server, _picker));
        inputs.Children.Add(_name);
        inputs.Children.Add(Ui.Row(ToolUi.Caption("Record type"), _type, ToolUi.Caption("Class"), _class));
        inputs.Children.Add(Ui.Row(_tcp, _dnssec, _noRecurse, _short));
        inputs.Children.Add(Ui.Row(_lookUp, _compare, _stop));
        inputs.Children.Add(_message);
        Content = ToolUi.Page(inputs, _output);

        _picker.SelectionChanged += (_, _) =>
        {
            if (_picker.SelectedItem is string chosen) _server.Text = chosen;
        };
        _lookUp.Click += async (_, _) => await LookUpAsync();
        _compare.Click += async (_, _) => await CompareAsync();
        _stop.Click += (_, _) => _cancel?.Cancel();
        RefreshServers();
    }

    /// <summary>The server asked: an address, a name, or a member as picked from the list ("gm.lab.test (10.0.0.1)").</summary>
    public string ServerText
    {
        get => _server.Text ?? "";
        set => _server.Text = value;
    }

    public string NameText
    {
        get => _name.Text ?? "";
        set => _name.Text = value;
    }

    public string TypeText
    {
        get => _type.SelectedItem as string ?? "A";
        set
        {
            value = value.Trim().ToUpperInvariant();
            if (!RecordTypes.Contains(value)) throw new ArgumentException($"{value} is not one of the record types offered.");
            _type.SelectedItem = value;
        }
    }

    public bool Tcp
    {
        get => _tcp.IsChecked == true;
        set => _tcp.IsChecked = value;
    }

    public bool Dnssec
    {
        get => _dnssec.IsChecked == true;
        set => _dnssec.IsChecked = value;
    }

    public bool NoRecursion
    {
        get => _noRecurse.IsChecked == true;
        set => _noRecurse.IsChecked = value;
    }

    public bool AnswersOnly
    {
        get => _short.IsChecked == true;
        set => _short.IsChecked = value;
    }

    /// <summary>IN, or CH for questions about the server itself (version.bind).</summary>
    public string ClassText
    {
        get => _class.SelectedItem as string ?? "IN";
        set => _class.SelectedItem = value;
    }

    public string Output => _output.Text ?? "";

    public string MessageText => _message.IsVisible ? _message.Text ?? "" : "";

    public bool IsRunning => _cancel is not null;

    /// <summary>The members offered in the picker, as "name (address)".</summary>
    public IReadOnlyList<string> ServerChoices => _picker.ItemsSource as IReadOnlyList<string> ?? [];

    /// <summary>Fills the picker from the open grid. What is typed in the server box is left alone.</summary>
    public void RefreshServers()
    {
        var choices = _context().Inventory is { } inventory
            ? DnsServers.FromMembers(inventory).Select(s => $"{s.Label} ({s.Address})").ToList()
            : [];
        _picker.ItemsSource = choices;
    }

    /// <summary>Looks up what is typed: one name, an address in reverse, or a batch when there are several lines.</summary>
    public async Task LookUpAsync(IDnsTransport? transport = null)
    {
        if (_cancel is not null) return;
        _message.IsVisible = false;

        _cancel = new CancellationTokenSource();
        var cancel = _cancel;
        SetRunning(true);
        try
        {
            var (items, problems) = Questions();
            if (items.Count == 0)
            {
                throw new DnsQueryException(problems.Count > 0
                    ? string.Join(" ", problems)
                    : "Type a name to look up (or an address, to look it up in reverse).");
            }

            var options = await OptionsAsync(cancel.Token);
            if (items.Count == 1)
            {
                var response = await DnsLookup.QueryAsync(items[0].Question, options, transport, cancel.Token);
                _output.Text = DigFormatter.Format(response, AnswersOnly);
            }
            else
            {
                var results = await DnsBatch.RunAsync(items, options, transport, ct: cancel.Token);
                _output.Text = string.Concat(problems.Select(p => p + "\n")) + DnsBatch.Text(results);
            }
        }
        catch (Exception ex) when (ex is DnsQueryException or ArgumentException)
        {
            _output.Text = "";
            Ui.ShowError(_message, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Ui.ShowInfo(_message, "Stopped.");
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            SetRunning(false);
        }
    }

    /// <summary>Asks every DNS member of the open grid the first name typed, and shows where the answers differ.</summary>
    public async Task CompareAsync(IDnsTransport? transport = null)
    {
        if (_cancel is not null) return;
        _message.IsVisible = false;

        _cancel = new CancellationTokenSource();
        var cancel = _cancel;
        SetRunning(true);
        try
        {
            var (items, problems) = Questions();
            if (items.Count == 0)
            {
                throw new DnsQueryException(problems.Count > 0
                    ? string.Join(" ", problems)
                    : "Type the name to ask every DNS member about.");
            }
            if (_context().Inventory is not { } inventory)
                throw new DnsQueryException("No grid is open. Open one in Inventory and its DNS members can be compared.");

            var servers = DnsServers.FromMembers(inventory);
            if (servers.Count == 0) throw new DnsQueryException($"None of {inventory.GridName}'s members serves DNS.");

            // The server box is not used: each member is asked in turn.
            var template = new DnsQueryOptions
            {
                Server = servers[0].Address, Tcp = Tcp, Dnssec = Dnssec, Recurse = !NoRecursion, Timeout = TimeSpan.FromSeconds(5),
            };
            var comparison = await DnsCompare.RunAsync(items[0].Question, servers, template, transport, cancel.Token);
            _output.Text = DnsCompare.Text(comparison);
        }
        catch (Exception ex) when (ex is DnsQueryException or ArgumentException)
        {
            _output.Text = "";
            Ui.ShowError(_message, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Ui.ShowInfo(_message, "Stopped.");
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            SetRunning(false);
        }
    }

    /// <summary>The questions typed, in the chosen class; lines that could not be read come back as problems.</summary>
    private (List<DnsBatchItem> Items, List<string> Problems) Questions()
    {
        DnsTypes.TryParse(TypeText, out DnsType type);
        var dnsClass = ClassText == "CH" ? DnsClass.CH : DnsClass.IN;

        var (items, problems) = DnsBatch.Parse(NameText, type);
        return (items.Select(i => i with { Question = i.Question with { Class = dnsClass } }).ToList(), problems);
    }

    private async Task<DnsQueryOptions> OptionsAsync(CancellationToken ct)
    {
        var text = ServerText.Trim();
        IPAddress server;
        if (text.Length == 0)
        {
            server = DnsServers.Resolvers().FirstOrDefault()
                     ?? throw new DnsQueryException("Choose or type the DNS server to ask. This computer has no resolver configured to fall back on.");
        }
        else
        {
            // A member picked from the list reads "gm.lab.test (10.0.0.1)": the address is in the brackets.
            var bracketed = InBrackets().Match(text);
            server = bracketed.Success && IPAddress.TryParse(bracketed.Groups["address"].Value, out var picked)
                ? picked
                : await DnsServers.ResolveAsync(text, ct);
        }

        return new DnsQueryOptions
        {
            Server = server, Tcp = Tcp, Dnssec = Dnssec, Recurse = !NoRecursion, Timeout = TimeSpan.FromSeconds(5),
        };
    }

    private void SetRunning(bool running)
    {
        _lookUp.IsEnabled = !running;
        _compare.IsEnabled = !running;
        _stop.IsEnabled = running;
    }

    [GeneratedRegex(@"\((?<address>[^)]+)\)\s*$")]
    private static partial Regex InBrackets();
}
