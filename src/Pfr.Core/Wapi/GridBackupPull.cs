using Pfr.Core.Clients;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Wapi;

/// <summary>
/// Pulls a fresh backup from a grid over WAPI and stores it as a snapshot of one of a client's grids.
/// The download is parsed as it arrives and never written to disk, so the secrets inside a backup stay
/// off the engineer's machine; only the parsed inventory (no secrets) is stored, as with a file import.
/// </summary>
public static class GridBackupPull
{
    /// <summary>
    /// Downloads the backup, parses it, and adds it as a snapshot of grid <paramref name="gridId"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The backup is from a different grid than the one's existing snapshots (a wrong address), or has no grid
    /// name. Nothing is stored.
    /// </exception>
    public static async Task<(ClientGrid Grid, SnapshotInfo Snapshot)> PullAsync(
        ClientWorkspace workspace, string clientId, string gridId, WapiClient wapi, string label = "",
        CancellationToken ct = default)
    {
        var grid = workspace.GetClient(clientId).FindGrid(gridId)
                   ?? throw new ArgumentException("That grid no longer exists.");

        var pulled = await wapi.DownloadBackupAsync(stream => Task.Run(() => Parse(stream), ct), ct).ConfigureAwait(false);
        var source = $"WAPI backup from {wapi.Host}";

        BackupParser.EnsureGridName(pulled.Inventory, source);
        var known = grid.Snapshots
            .Select(s => s.GridName?.Trim())
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();
        if (known.Count > 0 && !known.Any(name => string.Equals(name, pulled.Inventory.GridName!.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"The backup pulled from {wapi.Host} is from grid \"{pulled.Inventory.GridName}\", but \"{grid.Name}\" " +
                $"holds snapshots of \"{known[0]}\". Check the grid's address. Nothing was saved.");
        }

        return workspace.AddSnapshot(clientId, pulled.Inventory, source, pulled.Bytes, pulled.Sha256, gridId, label);
    }

    private static PulledBackup Parse(Stream download)
    {
        var counted = new HashingStream(download);
        using var oneDb = BackupSource.OpenOneDb(counted);
        var inventory = BackupParser.Parse(oneDb);

        // The parser can stop before the end of the download; read the rest so the hash and size cover it all.
        counted.CopyTo(Stream.Null);
        return new PulledBackup(inventory, counted.FinishHex(), counted.BytesRead);
    }

    private sealed record PulledBackup(GridInventory Inventory, string Sha256, long Bytes);
}
