using System.Text.Json.Serialization;

namespace Pfr.Core.Clients;

/// <summary>A client engagement workspace. A client can have several grids.</summary>
public sealed class Client
{
    /// <summary>Random id; also the folder name, so client names never appear on disk unencrypted.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<Contact> Contacts { get; } = [];
    public List<ClientGrid> Grids { get; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public IEnumerable<SnapshotInfo> AllSnapshots => Grids.SelectMany(g => g.Snapshots);

    [JsonIgnore]
    public SnapshotInfo? LatestSnapshot => AllSnapshots.OrderBy(s => s.ImportedUtc).LastOrDefault();

    public ClientGrid? FindGrid(string gridId) => Grids.FirstOrDefault(g => g.Id == gridId);

    /// <summary>The grid holding a snapshot, and the snapshot.</summary>
    public (ClientGrid Grid, SnapshotInfo Snapshot)? FindSnapshot(string snapshotId)
    {
        foreach (var grid in Grids)
        {
            foreach (var snapshot in grid.Snapshots)
            {
                if (snapshot.Id == snapshotId) return (grid, snapshot);
            }
        }
        return null;
    }
}

/// <summary>One of a client's NIOS grids, with its snapshots over time.</summary>
public sealed class ClientGrid
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name; defaults to the grid name found in the first backup.</summary>
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<SnapshotInfo> Snapshots { get; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public SnapshotInfo? LatestSnapshot => Snapshots.OrderBy(s => s.ImportedUtc).LastOrDefault();
}

public sealed class Contact
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>
/// One imported grid backup. Only the parsed inventory is stored (no secrets);
/// the original file is identified by name, size and SHA-256.
/// </summary>
public sealed class SnapshotInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset ImportedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Label { get; set; } = "";
    public string SourceFileName { get; set; } = "";
    public long SourceBytes { get; set; }
    public string SourceSha256 { get; set; } = "";
    public string? GridName { get; set; }
    public string? NiosVersion { get; set; }
    public int Members { get; set; }
    public int Zones { get; set; }
    public int Networks { get; set; }
    public int DhcpRanges { get; set; }
    public int Records { get; set; }

    [JsonIgnore]
    public string Title => Label.Length > 0 ? Label : SourceFileName;
}

/// <summary>A dated note of work done for a client.</summary>
public sealed class LogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Optional link to a snapshot taken during this work.</summary>
    public string? SnapshotId { get; set; }
}
