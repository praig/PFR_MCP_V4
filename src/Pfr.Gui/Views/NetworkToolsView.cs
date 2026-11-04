using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Model;

namespace Pfr.Gui.Views;

/// <summary>
/// What the network tools can draw on from the rest of the application: the grid open in Inventory (its members are
/// handy targets) and, when it came from a client, the client's workspace (where SSH host keys are pinned and the
/// grid's saved accounts live).
/// </summary>
public sealed record ToolContext(GridInventory? Inventory, ClientWorkspace? Workspace, Client? Client, ClientGrid? Grid)
{
    public static readonly ToolContext None = new(null, null, null, null);
}

/// <summary>Ping, DNS lookups and SSH command capture (spec §11). Only hosts typed or chosen here are contacted.</summary>
public sealed class NetworkToolsView : UserControl
{
    private readonly TabControl _tabs = new();

    public NetworkToolsView(Func<ToolContext> context)
    {
        Ping = new PingPage(context);
        Dns = new DnsPage(context);
        Ssh = new SshPage(context);

        _tabs.Items.Add(new TabItem { Header = "Ping", Content = Ping });
        _tabs.Items.Add(new TabItem { Header = "DNS lookup", Content = Dns });
        _tabs.Items.Add(new TabItem { Header = "SSH commands", Content = Ssh });
        Content = _tabs;
    }

    public PingPage Ping { get; }

    public DnsPage Dns { get; }

    public SshPage Ssh { get; }

    /// <summary>The names of the tool pages, in order.</summary>
    public IReadOnlyList<string> PageTitles => _tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString() ?? "").ToList();

    public int SelectedPage
    {
        get => _tabs.SelectedIndex;
        set => _tabs.SelectedIndex = value;
    }

    /// <summary>Called when the tab is shown, or the open grid or client changed: refreshes the servers and accounts on offer.</summary>
    public void RefreshContext()
    {
        Dns.RefreshServers();
        Ssh.RefreshAccounts();
    }
}

/// <summary>Small helpers shared by the tool pages.</summary>
internal static class ToolUi
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    /// <summary>A label that sits level with the controls beside it.</summary>
    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Opacity = 0.7,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static NumericUpDown Numeric(decimal minimum, decimal maximum, decimal value, decimal increment = 1) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        Increment = increment,
        FormatString = "0",
        Width = 130,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A read-only, fixed-width text area for command and lookup output.</summary>
    public static TextBox Output()
    {
        var box = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = Mono,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 160,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        return box;
    }

    /// <summary>Inputs on top, taking what they need; the results below, taking the rest.</summary>
    public static Grid Page(Control inputs, Control results)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(16) };
        grid.Children.Add(inputs);
        Grid.SetRow(results, 1);
        grid.Children.Add(results);
        return grid;
    }
}
