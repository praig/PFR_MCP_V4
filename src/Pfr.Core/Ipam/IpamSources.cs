using Pfr.Core.Clients;

namespace Pfr.Core.Ipam;

/// <summary>
/// Gathers what goes into a client's address plan: each grid's newest stored snapshot (spec §13.2). Nothing is read
/// from the original backups, and nothing is stored: the plan is built when it is asked for.
/// </summary>
public static class IpamSources
{
    /// <summary>The latest snapshot of each of the client's grids. A grid with none is left out and noted.</summary>
    public static List<IpamGridInput> LoadGrids(ClientWorkspace workspace, Client client, List<string>? notes = null)
    {
        var inputs = new List<IpamGridInput>();
        foreach (var grid in client.Grids.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (workspace.LoadLatest(client.Id, grid.Id) is not { } latest)
            {
                notes?.Add($"{grid.Name} has no snapshot yet, so it is not in the plan.");
                continue;
            }

            inputs.Add(new IpamGridInput(grid.Name, latest.Snapshot.ImportedUtc, latest.Snapshot.Id, latest.Inventory));
        }
        return inputs;
    }
}
