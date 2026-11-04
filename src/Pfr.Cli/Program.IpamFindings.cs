using Pfr.Core.Ipam;

namespace Pfr.Cli;

// Findings on a client's address plan (spec §13.7).
public static partial class Program
{
    private const string IpamFindingsUsage =
        "Usage: pfr ipam-findings --client <name> [--grid <name>] [--kind <id>[,<id>...]] [--warnings] [--items <n>] [--json]\n" +
        "       pfr ipam-findings --list-kinds";

    private static int RunIpamFindings(Args cli)
    {
        if (cli.Has("--list-kinds"))
        {
            foreach (var (_, id, title) in IpamFindings.Kinds) Console.WriteLine($"{id,-24} {title}");
            return 0;
        }

        if (cli.Value("--client") is not { } clientName) return Fail(IpamFindingsUsage, 2);

        HashSet<IpamFindingKind>? kinds = null;
        if (cli.Value("--kind") is { } kindList)
        {
            kinds = [];
            foreach (var id in kindList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IpamFindings.TryParseKind(id, out var kind))
                    throw new ArgumentException($"\"{id}\" is not a kind of finding. Run 'pfr ipam-findings --list-kinds' to see them.");
                kinds.Add(kind);
            }
        }

        var itemsPerFinding = 5;
        if (cli.Value("--items") is { } itemsText && (!int.TryParse(itemsText, out itemsPerFinding) || itemsPerFinding < 0))
            throw new ArgumentException($"--items needs a whole number, zero or more (not \"{itemsText}\").");

        var workspace = Unlock(cli);
        using var store = workspace.Store;
        var client = FindClient(workspace, clientName);
        var grid = cli.Value("--grid") is { } gridName ? FindGrid(client, gridName).Name : null;

        var notes = new List<string>();
        var inputs = IpamSources.LoadGrids(workspace, client, notes);
        if (inputs.Count == 0)
            return Fail($"{client.Name} has no grid snapshots yet, so there is no address plan. Import a backup with: pfr import --client \"{client.Name}\" <backup>");
        if (grid is not null && inputs.All(i => !string.Equals(i.GridName, grid, StringComparison.OrdinalIgnoreCase)))
            return Fail($"{grid} has no snapshot yet, so it is not in the plan.");

        // Every grid is in the plan whatever --grid says: the findings compare grids. --grid only chooses which to show.
        var plan = IpamBuilder.Build(inputs);
        var findings = IpamFindings.Filter(IpamFindings.Analyze(plan), kinds, cli.Has("--warnings") ? IpamSeverity.Warning : null, grid);

        Console.Write(cli.Has("--json")
            ? IpamFindingsReport.Json(plan, findings, client.Name, notes) + Environment.NewLine
            : IpamFindingsReport.Text(plan, findings, client.Name, notes, itemsPerFinding));

        return findings.Any(f => f.Severity == IpamSeverity.Warning) ? 1 : 0; // so a script can tell
    }
}
