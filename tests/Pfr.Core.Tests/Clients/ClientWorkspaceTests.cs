using System.Text.Json;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Nios;

namespace Pfr.Core.Tests.Clients;

public sealed class ClientWorkspaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfr-ws-{Guid.NewGuid():N}");
    private readonly ClientWorkspace _ws;

    public ClientWorkspaceTests()
    {
        var store = new SecureStore(_dir);
        store.Initialize("correct horse battery", SecureStoreTests.TestIterations);
        _ws = new ClientWorkspace(store);
    }

    public void Dispose()
    {
        _ws.Store.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string WriteBackup()
    {
        var path = Path.Combine(_dir, "..", $"pfr-ws-backup-{Guid.NewGuid():N}.bak");
        using var file = File.Create(path);
        OneDbFixture.GzipTarBackup().CopyTo(file);
        return Path.GetFullPath(path);
    }

    [Fact]
    public void Clients_with_contacts_and_notes_round_trip()
    {
        var client = _ws.CreateClient("  Acme Corp ");
        Assert.Equal("Acme Corp", client.Name);

        client.Notes = "Two data centres. Change window: Sundays.";
        client.Contacts.Add(new Contact { Name = "Pat Lee", Role = "Network lead", Email = "pat@acme.test" });
        _ws.SaveClient(client);

        var loaded = Assert.Single(_ws.ListClients());
        Assert.Equal(client.Notes, loaded.Notes);
        Assert.Equal("Pat Lee", Assert.Single(loaded.Contacts).Name);

        // Client names never appear on disk: folders are ids and files are encrypted.
        var files = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories);
        Assert.All(files, f => Assert.DoesNotContain("Acme", f));                    // paths
        Assert.All(files, f => Assert.DoesNotContain("Acme", File.ReadAllText(f)));  // contents
    }

    [Fact]
    public void Client_names_must_be_present_and_unique()
    {
        _ws.CreateClient("Acme");
        Assert.Throws<ArgumentException>(() => _ws.CreateClient("acme"));
        Assert.Throws<ArgumentException>(() => _ws.CreateClient("   "));

        var other = _ws.CreateClient("Globex");
        other.Name = "ACME";
        Assert.Throws<ArgumentException>(() => _ws.SaveClient(other));
    }

    [Fact]
    public void Importing_a_backup_creates_a_grid_and_stores_the_full_inventory()
    {
        var client = _ws.CreateClient("Acme");
        var backup = WriteBackup();
        try
        {
            var (grid, info) = _ws.ImportBackup(client.Id, backup, label: "Q4 health check");

            Assert.Equal("Lab Grid", grid.Name); // named after the grid in the backup
            Assert.Equal("Lab Grid", info.GridName);
            Assert.Equal(3, info.Members);
            Assert.Equal("Q4 health check", info.Label);
            Assert.Equal(ClientWorkspace.Sha256Of(backup), info.SourceSha256);
            Assert.Equal(new FileInfo(backup).Length, info.SourceBytes);
            Assert.Equal(info.Id, Assert.Single(Assert.Single(_ws.GetClient(client.Id).Grids).Snapshots).Id);

            // Stored inventory equals a fresh parse, field for field.
            var original = BackupParser.ParseFile(backup);
            var loaded = _ws.LoadSnapshot(client.Id, grid.Id, info.Id);
            Assert.Equal(JsonSerializer.Serialize(original, StoreJson.Options), JsonSerializer.Serialize(loaded, StoreJson.Options));
            Assert.Equal(new Ttl(3600, TtlSource.Zone), loaded.Zones.Single(z => z.Key == "._default.test.lab").DefaultTtl);
            Assert.Equal(2, loaded.Members.Single(m => m.Oid == 16).Nodes.Count);

            Assert.Equal(info.Id, _ws.FindDuplicate(client.Id, info.SourceSha256)?.Snapshot.Id);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public void Backups_of_the_same_grid_go_to_the_same_grid_and_others_get_their_own()
    {
        var client = _ws.CreateClient("Acme");
        var lab = BackupParser.Parse(OneDbFixture.XmlStream());
        var other = BackupParser.Parse(OneDbFixture.XmlStream());
        other.GridName = "DMZ Grid";

        var (first, _) = _ws.AddSnapshot(client.Id, lab, "jan.bak", 1, "a");
        var (second, _) = _ws.AddSnapshot(client.Id, lab, "feb.bak", 1, "b");
        var (third, _) = _ws.AddSnapshot(client.Id, other, "dmz.bak", 1, "c");

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, third.Id);

        var grids = _ws.GetClient(client.Id).Grids;
        Assert.Equal(["DMZ Grid", "Lab Grid"], grids.Select(g => g.Name).Order());
        Assert.Equal(2, grids.Single(g => g.Name == "Lab Grid").Snapshots.Count);
    }

    [Fact]
    public void A_renamed_grid_still_receives_its_backups_and_explicit_grids_win()
    {
        var client = _ws.CreateClient("Acme");
        var lab = BackupParser.Parse(OneDbFixture.XmlStream());
        var (grid, _) = _ws.AddSnapshot(client.Id, lab, "jan.bak", 1, "a");

        var c = _ws.GetClient(client.Id);
        c.FindGrid(grid.Id)!.Name = "Production";
        _ws.SaveClient(c);
        var (again, _) = _ws.AddSnapshot(client.Id, lab, "feb.bak", 1, "b");
        Assert.Equal(grid.Id, again.Id); // matched by the grid name inside the backups

        var spare = _ws.CreateGrid(client.Id, "Lab copy");
        var (chosen, _) = _ws.AddSnapshot(client.Id, lab, "mar.bak", 1, "c", gridId: spare.Id);
        Assert.Equal(spare.Id, chosen.Id);
    }

    [Fact]
    public void Latest_snapshot_opens_without_the_original_backup()
    {
        var client = _ws.CreateClient("Acme");
        var lab = BackupParser.Parse(OneDbFixture.XmlStream());
        var (grid, _) = _ws.AddSnapshot(client.Id, lab, "jan.bak", 1, "a");
        lab.Members.RemoveAt(0); // the same grid a month later, one member fewer
        var (_, newer) = _ws.AddSnapshot(client.Id, lab, "feb.bak", 1, "b");

        var latest = _ws.LoadLatest(client.Id, grid.Id);
        Assert.Equal(newer.Id, latest?.Snapshot.Id);
        Assert.Equal(2, latest?.Inventory.Members.Count);
        Assert.Null(_ws.LoadLatest(client.Id, _ws.CreateGrid(client.Id, "Empty").Id));
    }

    [Fact]
    public void Grid_names_are_unique_per_client()
    {
        var client = _ws.CreateClient("Acme");
        _ws.CreateGrid(client.Id, "Production");
        Assert.Equal("production (2)", _ws.CreateGrid(client.Id, "production").Name); // keeps the typed casing

        var c = _ws.GetClient(client.Id);
        c.Grids[1].Name = "PRODUCTION";
        Assert.Throws<ArgumentException>(() => _ws.SaveClient(c));
    }

    [Fact]
    public void Snapshots_hold_no_secrets()
    {
        var client = _ws.CreateClient("Acme");
        var (grid, info) = _ws.AddSnapshot(client.Id, BackupParser.Parse(OneDbFixture.XmlStream()), "grid.bak", 1, "abc");
        var json = JsonSerializer.Serialize(_ws.LoadSnapshot(client.Id, grid.Id, info.Id), StoreJson.Options);
        Assert.DoesNotContain("SUPERSECRET", json);
        Assert.DoesNotContain("LICENSEKEY", json);
    }

    [Fact]
    public void Deleting_snapshots_grids_and_clients_removes_their_data()
    {
        var client = _ws.CreateClient("Acme");
        var inv = BackupParser.Parse(OneDbFixture.XmlStream());
        var (grid, a) = _ws.AddSnapshot(client.Id, inv, "a.bak", 1, "aaa");
        var (_, b) = _ws.AddSnapshot(client.Id, inv, "b.bak", 1, "bbb");

        _ws.DeleteSnapshot(client.Id, grid.Id, a.Id);
        Assert.Equal(b.Id, Assert.Single(_ws.GetClient(client.Id).Grids.Single().Snapshots).Id);
        Assert.False(_ws.Store.Exists($"clients/{client.Id}/grids/{grid.Id}/snapshots/{a.Id}.pfr"));

        _ws.DeleteGrid(client.Id, grid.Id);
        Assert.Empty(_ws.GetClient(client.Id).Grids);
        Assert.False(Directory.Exists(Path.Combine(_dir, "clients", client.Id, "grids", grid.Id)));

        _ws.DeleteClient(client.Id);
        Assert.Empty(_ws.ListClients());
        Assert.False(Directory.Exists(Path.Combine(_dir, "clients", client.Id)));
    }

    [Fact]
    public void Engagement_log_add_edit_delete_newest_first()
    {
        var client = _ws.CreateClient("Acme");
        var older = _ws.SaveLogEntry(client.Id, new LogEntry
            { Date = DateTimeOffset.Now.AddDays(-7), Title = "Kick-off", Text = "Scoped the health check." });
        var newer = _ws.SaveLogEntry(client.Id, new LogEntry { Title = "Backup collected" });

        Assert.Equal([newer.Id, older.Id], _ws.GetLog(client.Id).Select(e => e.Id));

        older.Text = "Scoped the health check and DHCP review.";
        _ws.SaveLogEntry(client.Id, older);
        Assert.Equal(2, _ws.GetLog(client.Id).Count);
        Assert.Contains("DHCP review", _ws.GetLog(client.Id).Single(e => e.Id == older.Id).Text);

        _ws.DeleteLogEntry(client.Id, newer.Id);
        Assert.Equal(older.Id, Assert.Single(_ws.GetLog(client.Id)).Id);

        Assert.Throws<ArgumentException>(() => _ws.SaveLogEntry(client.Id, new LogEntry()));
    }

    [Fact]
    public void Data_is_readable_again_after_reopening_with_the_password()
    {
        var client = _ws.CreateClient("Acme");
        _ws.SaveLogEntry(client.Id, new LogEntry { Title = "Visit" });
        _ws.Store.Lock();

        var store = new SecureStore(_dir);
        Assert.True(store.Unlock("correct horse battery"));
        var reopened = new ClientWorkspace(store);
        Assert.Equal("Acme", Assert.Single(reopened.ListClients()).Name);
        Assert.Equal("Visit", Assert.Single(reopened.GetLog(client.Id)).Title);
    }

    [Fact]
    public void Backups_without_a_grid_name_are_refused_and_nothing_is_stored()
    {
        var client = _ws.CreateClient("Acme");
        var unnamed = BackupParser.Parse(OneDbFixture.XmlStream());
        unnamed.GridName = "  ";

        var ex = Assert.Throws<InvalidDataException>(() => _ws.AddSnapshot(client.Id, unnamed, "nameless.bak", 1, "x"));
        Assert.Contains("no grid name", ex.Message);
        Assert.Contains("nameless.bak", ex.Message);

        Assert.Empty(_ws.GetClient(client.Id).Grids);
        Assert.False(Directory.Exists(Path.Combine(_dir, "clients", client.Id, "grids")));
    }

    [Fact]
    public void Last_viewed_grid_is_remembered_and_forgotten_when_deleted()
    {
        var client = _ws.CreateClient("Acme");
        var (grid, snap) = _ws.AddSnapshot(client.Id, BackupParser.Parse(OneDbFixture.XmlStream()), "a.bak", 1, "a");
        Assert.Null(_ws.LastViewed());

        _ws.RememberLastViewed(client.Id, grid.Id, snap.Id);
        var last = _ws.LastViewed();
        Assert.Equal((client.Id, grid.Id, snap.Id), (last!.Value.Client.Id, last.Value.Grid.Id, last.Value.Snapshot.Id));

        _ws.DeleteSnapshot(client.Id, grid.Id, snap.Id);
        Assert.Null(_ws.LastViewed());
    }
}
