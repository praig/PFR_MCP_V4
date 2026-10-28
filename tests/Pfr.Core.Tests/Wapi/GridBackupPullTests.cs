using Pfr.Core.Clients;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Clients;
using Pfr.Core.Tests.Nios;
using Pfr.Core.Wapi;

namespace Pfr.Core.Tests.Wapi;

public sealed class GridBackupPullTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfr-pull-{Guid.NewGuid():N}");
    private readonly FakeWapiServer _server = new();
    private readonly ClientWorkspace _ws;

    public GridBackupPullTests()
    {
        var store = new SecureStore(_dir);
        store.Initialize("correct horse battery", SecureStoreTests.TestIterations);
        _ws = new ClientWorkspace(store);
    }

    public void Dispose()
    {
        _server.Dispose();
        _ws.Store.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private WapiClient Wapi() => WapiClient.ForReadOnly(new GridConnection
    {
        Address = "127.0.0.1",
        Port = _server.Port,
        CertificateFingerprint = _server.Fingerprint,
        ReadOnly = new Credential { Username = _server.User, Password = _server.Password },
    });

    [Fact]
    public async Task A_pulled_backup_becomes_a_snapshot_identified_like_a_downloaded_file()
    {
        var client = _ws.CreateClient("Acme");
        var grid = _ws.CreateGrid(client.Id, "Production");
        using var wapi = Wapi();

        var (savedGrid, snapshot) = await GridBackupPull.PullAsync(_ws, client.Id, grid.Id, wapi, "Pulled over WAPI");

        Assert.Equal(grid.Id, savedGrid.Id);
        Assert.Equal("Lab Grid", snapshot.GridName);
        Assert.Equal("Pulled over WAPI", snapshot.Label);
        Assert.Equal(3, snapshot.Members);
        Assert.Contains("127.0.0.1", snapshot.SourceFileName);

        // The hash and size cover the whole download, not just the part the parser read.
        Assert.Equal(_server.BackupSha256, snapshot.SourceSha256);
        Assert.Equal(_server.BackupBytes.Length, snapshot.SourceBytes);

        // It is the same inventory a file import of the same backup gives.
        var stored = _ws.LoadSnapshot(client.Id, grid.Id, snapshot.Id);
        var path = Path.Combine(Path.GetTempPath(), $"pfr-pull-file-{Guid.NewGuid():N}.bak");
        try
        {
            File.WriteAllBytes(path, _server.BackupBytes);
            Assert.Equal(BackupParser.ParseFile(path).Members.Count, stored.Members.Count);
            Assert.Equal(ClientWorkspace.Sha256Of(path), snapshot.SourceSha256);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Pulling_again_adds_another_snapshot_to_the_same_grid()
    {
        var client = _ws.CreateClient("Acme");
        var grid = _ws.CreateGrid(client.Id, "Production");
        using var wapi = Wapi();

        await GridBackupPull.PullAsync(_ws, client.Id, grid.Id, wapi);
        await GridBackupPull.PullAsync(_ws, client.Id, grid.Id, wapi);

        Assert.Equal(2, Assert.Single(_ws.GetClient(client.Id).Grids).Snapshots.Count);
    }

    [Fact]
    public async Task A_backup_from_a_different_grid_than_the_one_on_file_is_refused()
    {
        var client = _ws.CreateClient("Acme");
        var other = BackupParser.Parse(OneDbFixture.XmlStream());
        other.GridName = "Another Grid";
        var (grid, _) = _ws.AddSnapshot(client.Id, other, "other.bak", 1, "x");
        using var wapi = Wapi();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => GridBackupPull.PullAsync(_ws, client.Id, grid.Id, wapi));

        Assert.Contains("Lab Grid", ex.Message);
        Assert.Contains("Another Grid", ex.Message);
        Assert.Contains("Nothing was saved", ex.Message);
        Assert.Single(_ws.GetClient(client.Id).Grids.Single().Snapshots);
    }

    [Fact]
    public async Task The_grid_that_no_longer_exists_is_refused_before_anything_is_downloaded()
    {
        var client = _ws.CreateClient("Acme");
        using var wapi = Wapi();

        await Assert.ThrowsAsync<ArgumentException>(() => GridBackupPull.PullAsync(_ws, client.Id, "no-such-grid", wapi));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Something_that_is_not_a_backup_is_refused_and_nothing_is_saved()
    {
        _server.Extra = request =>
            request.Method == "GET" && request.PathAndQuery.StartsWith("/http_direct_file_io/", StringComparison.Ordinal)
                ? new FakeResponse(200, "text/plain", "this is not a grid backup, just some text"u8.ToArray())
                : null;
        var client = _ws.CreateClient("Acme");
        var grid = _ws.CreateGrid(client.Id, "Production");
        using var wapi = Wapi();

        await Assert.ThrowsAsync<InvalidDataException>(() => GridBackupPull.PullAsync(_ws, client.Id, grid.Id, wapi));

        Assert.Empty(_ws.GetClient(client.Id).Grids.Single().Snapshots);
        // The grid is still told the download is over.
        Assert.Contains(_server.Requests, r => r.PathAndQuery.Contains("_function=downloadcomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_is_written_to_disk_by_a_pull_except_the_encrypted_snapshot()
    {
        var client = _ws.CreateClient("Acme");
        var grid = _ws.CreateGrid(client.Id, "Production");
        using var wapi = Wapi();

        await GridBackupPull.PullAsync(_ws, client.Id, grid.Id, wapi);

        var names = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain(names, n => n is not null && (n.EndsWith(".bak", StringComparison.Ordinal) || n.EndsWith(".xml", StringComparison.Ordinal)));
        Assert.Contains("store.json", names);
    }
}
