using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Storage;

namespace Pfr.Core.Clients;

/// <summary>
/// Client workspaces kept in an encrypted <see cref="SecureStore"/>:
/// <code>
/// clients/&lt;client&gt;/client.pfr                         client, contacts, notes, grids and snapshot lists
/// clients/&lt;client&gt;/log.pfr                            engagement log
/// clients/&lt;client&gt;/grids/&lt;grid&gt;/snapshots/&lt;id&gt;.pfr    parsed inventory (gzip JSON)
/// </code>
/// Every file is encrypted; folder names are random ids. Snapshots open straight
/// from here, so switching grids or clients never needs the original backup.
/// </summary>
public sealed class ClientWorkspace(SecureStore store)
{
    private const string ClientsFolder = "clients";

    public SecureStore Store { get; } = store;

    private static string ClientFile(string id) => $"{ClientsFolder}/{id}/client.pfr";
    private static string LogFile(string id) => $"{ClientsFolder}/{id}/log.pfr";
    private static string GridFolder(string clientId, string gridId) => $"{ClientsFolder}/{clientId}/grids/{gridId}";
    private static string SnapshotFile(string clientId, string gridId, string snapshotId) =>
        $"{GridFolder(clientId, gridId)}/snapshots/{snapshotId}.pfr";

    // ---- Clients ---------------------------------------------------------

    /// <summary>All clients, by name. Unreadable entries are reported in <paramref name="problems"/>.</summary>
    public List<Client> ListClients(List<string>? problems = null)
    {
        var clients = new List<Client>();
        foreach (var id in Store.Folders(ClientsFolder))
        {
            if (!Store.Exists(ClientFile(id))) continue;
            try
            {
                clients.Add(Store.ReadJson<Client>(ClientFile(id)));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
            {
                problems?.Add($"Client folder {id} could not be read: {ex.Message}");
            }
        }
        return clients.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Client GetClient(string id) => Store.ReadJson<Client>(ClientFile(id));

    public Client CreateClient(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A client needs a name.");
        if (ListClients().Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A client named \"{name}\" already exists.");

        var client = new Client { Name = name };
        Store.WriteJson(ClientFile(client.Id), client);
        Store.WriteJson(LogFile(client.Id), new List<LogEntry>());
        return client;
    }

    public void SaveClient(Client client)
    {
        client.Name = client.Name.Trim();
        if (client.Name.Length == 0) throw new ArgumentException("A client needs a name.");
        if (ListClients().Any(c => c.Id != client.Id
                                   && string.Equals(c.Name, client.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A client named \"{client.Name}\" already exists.");
        foreach (var g in client.Grids) g.Name = g.Name.Trim();
        if (client.Grids.Any(g => g.Name.Length == 0)) throw new ArgumentException("A grid needs a name.");
        if (client.Grids.GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ArgumentException("Each of a client's grids needs a different name.");

        client.UpdatedUtc = DateTimeOffset.UtcNow;
        Store.WriteJson(ClientFile(client.Id), client);
    }

    /// <summary>Removes the client and everything stored for it.</summary>
    public void DeleteClient(string id) => Store.DeleteFolder($"{ClientsFolder}/{id}");

    // ---- Grids -------------------------------------------------------------

    public ClientGrid CreateGrid(string clientId, string name)
    {
        var client = GetClient(clientId);
        var grid = new ClientGrid { Name = UniqueGridName(client, name) };
        client.Grids.Add(grid);
        SaveClient(client);
        return grid;
    }

    /// <summary>Removes a grid and all of its snapshots.</summary>
    public void DeleteGrid(string clientId, string gridId)
    {
        var client = GetClient(clientId);
        client.Grids.RemoveAll(g => g.Id == gridId);
        SaveClient(client);
        Store.DeleteFolder(GridFolder(clientId, gridId));
    }

    /// <summary>
    /// The grid a new backup belongs to: the one with the same grid name if there is
    /// one, otherwise a new grid named after it.
    /// </summary>
    private static ClientGrid MatchOrCreateGrid(Client client, GridInventory inventory, string fallbackName)
    {
        var name = string.IsNullOrWhiteSpace(inventory.GridName) ? fallbackName : inventory.GridName!;
        var gridName = inventory.GridName;
        var match = client.Grids.FirstOrDefault(g =>
            (gridName is not null && g.Snapshots.Any(s =>
                string.Equals(s.GridName, gridName, StringComparison.OrdinalIgnoreCase)))
            || string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        var grid = new ClientGrid { Name = UniqueGridName(client, name) };
        client.Grids.Add(grid);
        return grid;
    }

    private static string UniqueGridName(Client client, string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Grid" : name.Trim();
        var candidate = name;
        for (var i = 2; client.Grids.Any(g => string.Equals(g.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = $"{name} ({i})";
        }
        return candidate;
    }

    // ---- Snapshots -------------------------------------------------------

    /// <summary>
    /// Parses a backup and stores it as a new snapshot. With no <paramref name="gridId"/>,
    /// it goes to the client's grid of the same name, or a new grid.
    /// </summary>
    public (ClientGrid Grid, SnapshotInfo Snapshot) ImportBackup(
        string clientId, string backupPath, string? gridId = null, string label = "")
    {
        var inventory = BackupParser.ParseFile(backupPath);
        var file = new FileInfo(backupPath);
        return AddSnapshot(clientId, inventory, file.Name, file.Length, Sha256Of(backupPath), gridId, label);
    }

    /// <summary>Stores an already-parsed inventory as a new snapshot (grid chosen as for <see cref="ImportBackup"/>).</summary>
    public (ClientGrid Grid, SnapshotInfo Snapshot) AddSnapshot(string clientId, GridInventory inventory,
        string sourceFileName, long sourceBytes, string sourceSha256, string? gridId = null, string label = "")
    {
        var client = GetClient(clientId);
        var grid = gridId is null
            ? MatchOrCreateGrid(client, inventory, Path.GetFileNameWithoutExtension(sourceFileName))
            : client.FindGrid(gridId) ?? throw new ArgumentException("That grid no longer exists.");

        var info = new SnapshotInfo
        {
            Label = label.Trim(),
            SourceFileName = sourceFileName,
            SourceBytes = sourceBytes,
            SourceSha256 = sourceSha256,
            GridName = inventory.GridName,
            NiosVersion = inventory.NiosVersion,
            Members = inventory.Members.Count,
            Zones = inventory.Zones.Count,
            Networks = inventory.Networks.Count(n => !n.IsContainer),
            DhcpRanges = inventory.DhcpRanges.Count,
            Records = inventory.Records.Count,
        };

        // Inventory first, then the index, so a crash never lists a snapshot that isn't there.
        Store.Write(SnapshotFile(clientId, grid.Id, info.Id),
            Compress(JsonSerializer.SerializeToUtf8Bytes(inventory, StoreJson.Options)));
        grid.Snapshots.Add(info);
        SaveClient(client);
        return (grid, info);
    }

    public GridInventory LoadSnapshot(string clientId, string gridId, string snapshotId)
    {
        var json = Decompress(Store.Read(SnapshotFile(clientId, gridId, snapshotId)));
        return JsonSerializer.Deserialize<GridInventory>(json, StoreJson.Options)
               ?? throw new InvalidDataException("Snapshot is empty.");
    }

    /// <summary>The newest snapshot of a grid, or null if it has none.</summary>
    public (SnapshotInfo Snapshot, GridInventory Inventory)? LoadLatest(string clientId, string gridId)
    {
        var grid = GetClient(clientId).FindGrid(gridId);
        return grid?.LatestSnapshot is { } latest ? (latest, LoadSnapshot(clientId, gridId, latest.Id)) : null;
    }

    public void DeleteSnapshot(string clientId, string gridId, string snapshotId)
    {
        var client = GetClient(clientId);
        client.FindGrid(gridId)?.Snapshots.RemoveAll(s => s.Id == snapshotId);
        SaveClient(client);
        Store.Delete(SnapshotFile(clientId, gridId, snapshotId));
    }

    /// <summary>An existing snapshot of the same file (same SHA-256), if any, and its grid.</summary>
    public (ClientGrid Grid, SnapshotInfo Snapshot)? FindDuplicate(string clientId, string sha256)
    {
        foreach (var grid in GetClient(clientId).Grids)
        {
            var match = grid.Snapshots.FirstOrDefault(s =>
                string.Equals(s.SourceSha256, sha256, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return (grid, match);
        }
        return null;
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ---- Engagement log --------------------------------------------------

    /// <summary>Entries newest first.</summary>
    public List<LogEntry> GetLog(string clientId) =>
        (Store.Exists(LogFile(clientId)) ? Store.ReadJson<List<LogEntry>>(LogFile(clientId)) : [])
        .OrderByDescending(e => e.Date).ToList();

    public LogEntry SaveLogEntry(string clientId, LogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Title) && string.IsNullOrWhiteSpace(entry.Text))
            throw new ArgumentException("A log entry needs a title or some text.");

        var log = GetLog(clientId);
        log.RemoveAll(e => e.Id == entry.Id);
        log.Add(entry);
        Store.WriteJson(LogFile(clientId), log);
        return entry;
    }

    public void DeleteLogEntry(string clientId, string entryId)
    {
        var log = GetLog(clientId);
        log.RemoveAll(e => e.Id == entryId);
        Store.WriteJson(LogFile(clientId), log);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
