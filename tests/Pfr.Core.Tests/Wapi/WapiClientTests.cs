using System.Net;
using Pfr.Core.Clients;
using Pfr.Core.Wapi;

namespace Pfr.Core.Tests.Wapi;

public sealed class WapiClientTests : IDisposable
{
    private readonly FakeWapiServer _server = new();

    public void Dispose() => _server.Dispose();

    private GridConnection Connection(string? pin = "server", string? version = null, string? password = null) => new()
    {
        Address = "127.0.0.1",
        Port = _server.Port,
        WapiVersion = version,
        CertificateFingerprint = pin == "server" ? _server.Fingerprint : pin,
        ReadOnly = new Credential { Username = _server.User, Password = password ?? _server.Password },
    };

    // ---- Connecting ------------------------------------------------------------

    [Fact]
    public async Task Connects_detects_the_newest_version_and_reads_the_grid_name()
    {
        using var wapi = WapiClient.ForReadOnly(Connection());

        var info = await wapi.ConnectAsync();

        Assert.Equal("2.12", info.ApiVersion); // 2.12 is newer than 2.9: compared as numbers, not text
        Assert.Equal("Lab Grid", info.GridName);
        Assert.Equal(_server.Fingerprint, info.CertificateFingerprint);
        Assert.Equal("2.12", wapi.ApiVersion);
        Assert.NotEmpty(_server.Requests);
        Assert.All(_server.Requests, r => Assert.Equal(_server.ExpectedAuthorization, r.Authorization));
    }

    [Fact]
    public async Task A_version_set_on_the_connection_is_used_without_asking_the_grid()
    {
        using var wapi = WapiClient.ForReadOnly(Connection(version: "v2.10"));

        var info = await wapi.ConnectAsync();

        Assert.Equal("2.10", info.ApiVersion);
        Assert.DoesNotContain(_server.Requests, r => r.PathAndQuery.Contains("_schema", StringComparison.Ordinal));
        Assert.Contains(_server.Requests, r => r.PathAndQuery.StartsWith("/wapi/v2.10/grid", StringComparison.Ordinal));
    }

    [Fact]
    public void A_connection_without_a_read_only_account_cannot_be_used()
    {
        var connection = Connection();
        connection.ReadOnly = null;
        Assert.Throws<WapiException>(() => WapiClient.ForReadOnly(connection));
    }

    [Fact]
    public async Task A_wrong_password_is_reported_without_echoing_it()
    {
        using var wapi = WapiClient.ForReadOnly(Connection(password: "not-the-password"));

        var ex = await Assert.ThrowsAsync<WapiRequestException>(() => wapi.ConnectAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains(_server.User, ex.Message);
        Assert.DoesNotContain("not-the-password", ex.Message);
    }

    [Fact]
    public async Task A_grid_that_cannot_be_reached_is_reported()
    {
        var connection = Connection();
        _server.Dispose();
        using var wapi = WapiClient.ForReadOnly(connection);

        var ex = await Assert.ThrowsAsync<WapiException>(() => wapi.ConnectAsync());
        Assert.Contains("127.0.0.1", ex.Message);
    }

    // ---- Certificate pinning -----------------------------------------------------

    [Fact]
    public async Task A_certificate_that_is_not_pinned_yet_is_refused_before_any_credentials_are_sent()
    {
        using var wapi = WapiClient.ForReadOnly(Connection(pin: null));

        var ex = await Assert.ThrowsAsync<WapiCertificateException>(() => wapi.ConnectAsync());

        Assert.Equal(CertificateProblem.NotPinned, ex.Problem);
        Assert.Equal(_server.Fingerprint, ex.ObservedFingerprint);
        Assert.Null(ex.PinnedFingerprint);
        Assert.Empty(_server.Requests); // dropped in the TLS handshake: no HTTP request, so no user id or password
    }

    [Fact]
    public async Task A_different_certificate_from_the_pinned_one_is_refused_before_any_credentials_are_sent()
    {
        var other = new string('0', 64);
        using var wapi = WapiClient.ForReadOnly(Connection(pin: other));

        var ex = await Assert.ThrowsAsync<WapiCertificateException>(() => wapi.ConnectAsync());

        Assert.Equal(CertificateProblem.Mismatch, ex.Problem);
        Assert.Equal(other, ex.PinnedFingerprint);
        Assert.Equal(_server.Fingerprint, ex.ObservedFingerprint);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task The_pinned_certificate_is_accepted_even_though_it_is_self_signed()
    {
        // Pinning replaces chain validation, as with an SSH host key.
        using var wapi = WapiClient.ForReadOnly(Connection());
        Assert.Equal("Lab Grid", (await wapi.ConnectAsync()).GridName);
    }

    [Fact]
    public async Task The_pin_is_still_enforced_after_a_trusted_connection()
    {
        var connection = Connection();
        using (var first = WapiClient.ForReadOnly(connection))
        {
            await first.ConnectAsync();
        }

        connection.CertificateFingerprint = new string('a', 64);
        using var second = WapiClient.ForReadOnly(connection);
        await Assert.ThrowsAsync<WapiCertificateException>(() => second.ConnectAsync());
    }

    // ---- Read-only ------------------------------------------------------------------

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public void Write_methods_are_blocked(string method)
    {
        var ex = Assert.Throws<WapiException>(() =>
            WapiClient.EnsureAllowed(new HttpMethod(method), new Uri("https://gm.test/wapi/v2.12/record:a/ZG5z")));
        Assert.Contains("read-only", ex.Message);
    }

    [Theory]
    [InlineData("https://gm.test/wapi/v2.12/record:a")]
    [InlineData("https://gm.test/wapi/v2.12/network")]
    [InlineData("https://gm.test/wapi/v2.12/fileop")]
    [InlineData("https://gm.test/wapi/v2.12/fileop?_function=restore_database")]
    [InlineData("https://gm.test/wapi/v2.12/fileop?_function=uploadinit")]
    [InlineData("https://gm.test/wapi/v2.12/fileop?_function=csv_import")]
    [InlineData("https://gm.test/wapi/v2.12/grid/b25l:Lab?_function=restartservices")]
    public void Posts_other_than_the_backup_download_calls_are_blocked(string url) =>
        Assert.Throws<WapiException>(() => WapiClient.EnsureAllowed(HttpMethod.Post, new Uri(url)));

    [Theory]
    [InlineData("GET", "https://gm.test/wapi/v2.12/grid?_return_fields=name")]
    [InlineData("GET", "https://gm.test/wapi/v1.0/?_schema")]
    [InlineData("POST", "https://gm.test/wapi/v2.12/fileop?_function=getgriddata")]
    [InlineData("POST", "https://gm.test/wapi/v2.12/fileop?_function=downloadcomplete")]
    public void Reads_and_the_backup_download_calls_are_allowed(string method, string url) =>
        WapiClient.EnsureAllowed(new HttpMethod(method), new Uri(url));

    // ---- Paging ---------------------------------------------------------------------

    [Fact]
    public async Task Paged_results_are_followed_to_the_last_page()
    {
        _server.Extra = request =>
        {
            if (!request.PathAndQuery.Contains("/network?", StringComparison.Ordinal)) return null;
            return request.PathAndQuery.Contains("_page_id=p2", StringComparison.Ordinal)
                ? FakeResponse.Json(200, """{"result":[{"network":"10.0.2.0/24"}]}""")
                : FakeResponse.Json(200, """{"result":[{"network":"10.0.0.0/24"},{"network":"10.0.1.0/24"}],"next_page_id":"p2"}""");
        };
        using var wapi = WapiClient.ForReadOnly(Connection());

        var networks = new List<string>();
        await foreach (var item in wapi.GetAllAsync("network", returnFields: "network,comment", pageSize: 2))
        {
            networks.Add(item.GetProperty("network").GetString()!);
        }

        Assert.Equal(["10.0.0.0/24", "10.0.1.0/24", "10.0.2.0/24"], networks);
        var pages = _server.Requests.Where(r => r.PathAndQuery.Contains("/network?", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, pages.Count);
        Assert.Contains("_paging=1", pages[0].PathAndQuery);
        Assert.Contains("_max_results=2", pages[0].PathAndQuery);
        Assert.Contains("_return_as_object=1", pages[0].PathAndQuery);
        Assert.Contains("_page_id=p2", pages[1].PathAndQuery);
    }

    // ---- Backup download ---------------------------------------------------------------

    [Fact]
    public async Task A_backup_is_downloaded_from_the_connected_host_and_the_grid_is_told_when_done()
    {
        using var wapi = WapiClient.ForReadOnly(Connection());

        var received = await wapi.DownloadBackupAsync(async stream =>
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return copy.ToArray();
        });

        Assert.Equal(_server.BackupBytes, received);

        // The grid named another host in the download address; only its path was used, on the host we trust.
        var requests = _server.Requests;
        Assert.Contains(requests, r => r.Method == "POST" && r.PathAndQuery.Contains("_function=getgriddata", StringComparison.Ordinal)
                                       && r.Body.Contains("BACKUP", StringComparison.Ordinal));
        Assert.Contains(requests, r => r.Method == "GET" && r.PathAndQuery == "/http_direct_file_io/req_id-DOWNLOAD-1/database.bak");
        var done = Assert.Single(requests, r => r.PathAndQuery.Contains("_function=downloadcomplete", StringComparison.Ordinal));
        Assert.Contains(_server.Token, done.Body);
    }

    [Fact]
    public async Task The_grid_is_told_the_download_is_complete_even_if_reading_it_fails()
    {
        using var wapi = WapiClient.ForReadOnly(Connection());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wapi.DownloadBackupAsync<int>(_ => throw new InvalidOperationException("parse failed")));

        Assert.Contains(_server.Requests, r => r.PathAndQuery.Contains("_function=downloadcomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_grid_that_does_not_offer_a_download_is_reported()
    {
        _server.Extra = request =>
            request.Method == "POST" && request.PathAndQuery.Contains("getgriddata", StringComparison.Ordinal)
                ? FakeResponse.Json(200, "{}")
                : null;
        using var wapi = WapiClient.ForReadOnly(Connection());

        var ex = await Assert.ThrowsAsync<WapiException>(() => wapi.DownloadBackupAsync(_ => Task.FromResult(0)));
        Assert.Contains("did not return a backup download", ex.Message);
    }

    [Fact]
    public async Task Being_refused_permission_for_a_backup_names_the_account()
    {
        _server.Extra = request =>
            request.Method == "POST" && request.PathAndQuery.Contains("getgriddata", StringComparison.Ordinal)
                ? FakeResponse.Json(403, """{"Error":"AdmConProtoError: Operation not permitted","text":"Not permitted to take a backup"}""")
                : null;
        using var wapi = WapiClient.ForReadOnly(Connection());

        var ex = await Assert.ThrowsAsync<WapiRequestException>(() => wapi.DownloadBackupAsync(_ => Task.FromResult(0)));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains(_server.User, ex.Message);
        Assert.Contains("Not permitted to take a backup", ex.Message);
    }
}

public sealed class CertificatePinTests
{
    private static readonly string Hex = string.Concat(Enumerable.Range(0, 32).Select(i => (i * 8 % 256).ToString("x2")));

    [Fact]
    public void Fingerprints_are_accepted_however_they_are_written()
    {
        var colons = CertificatePin.Format(Hex);
        Assert.Equal(95, colons.Length);
        Assert.Equal(Hex, CertificatePin.Normalize(colons));
        Assert.Equal(Hex, CertificatePin.Normalize(Hex.ToUpperInvariant()));
        Assert.Equal(Hex, CertificatePin.Normalize("  " + colons.Replace(':', ' ') + " "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_means_no_pin(string? text) => Assert.Null(CertificatePin.Normalize(text));

    [Theory]
    [InlineData("abc")]
    [InlineData("zz")]
    public void Anything_else_is_refused(string text) =>
        Assert.Throws<ArgumentException>(() => CertificatePin.Normalize(text));

    [Fact]
    public void A_connection_keeps_its_fingerprint_tidy_and_refuses_a_bad_one()
    {
        var connection = new GridConnection { Address = "gm.test", CertificateFingerprint = CertificatePin.Format(Hex) };
        connection.Normalize();
        Assert.Equal(Hex, connection.CertificateFingerprint);

        connection.CertificateFingerprint = "  ";
        connection.Normalize();
        Assert.Null(connection.CertificateFingerprint);

        connection.CertificateFingerprint = "not a fingerprint";
        Assert.Throws<ArgumentException>(connection.Normalize);
    }
}
