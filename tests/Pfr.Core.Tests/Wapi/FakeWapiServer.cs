using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pfr.Core.Tests.Nios;
using Pfr.Core.Wapi;

namespace Pfr.Core.Tests.Wapi;

/// <summary>A request the fake grid received (only logged once the TLS handshake succeeded).</summary>
internal sealed record FakeRequest(string Method, string PathAndQuery, string? Authorization, string Body);

internal sealed record FakeResponse(int Status, string ContentType, byte[] Body)
{
    public static FakeResponse Json(int status, string json) => new(status, "application/json", Encoding.UTF8.GetBytes(json));
}

/// <summary>
/// A small HTTPS server on the loopback interface that behaves like a grid's WAPI, with a real self-signed
/// certificate, so certificate pinning is tested against real TLS. Serves the version list, the grid object,
/// the backup download calls and the backup file; tests can add routes through <see cref="Extra"/>.
/// </summary>
internal sealed class FakeWapiServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<FakeRequest> _requests = new();

    private const string DefaultMembers = """
        [
          {"_ref":"member/ZG5z:gm.lab.test","host_name":"gm.lab.test","enable_ha":false,
           "node_info":[{"ha_status":"NOT_CONFIGURED","hwmodel":"IB-V926","service_status":[
              {"service":"AUTH_NAMED","status":"WORKING","description":"Running"},
              {"service":"DHCPD","status":"WORKING","description":"Running"},
              {"service":"NTP_SYNC","status":"INACTIVE","description":"NTP is disabled"}]}]},
          {"_ref":"member/ZG5y:ns1.lab.test","host_name":"ns1.lab.test","enable_ha":true,
           "node_info":[
             {"ha_status":"ACTIVE","hwmodel":"IB-V926","service_status":[
                {"service":"AUTH_NAMED","status":"WORKING","description":"Running"},
                {"service":"DHCPD","status":"WORKING","description":"Running"}]},
             {"ha_status":"PASSIVE","hwmodel":"IB-V926","service_status":[
                {"service":"AUTH_NAMED","status":"WORKING","description":"Running"},
                {"service":"DHCPD","status":"FAILED","description":"The DHCP service failed to start"},
                {"service":"SWAP_USAGE","status":"WARNING","description":"Swap usage is 62%"}]}]}
        ]
        """;

    private const string DefaultUpgradeStatus = """
        [
          {"_ref":"upgradestatus/b25l:gm","type":"VNODE","member":"gm.lab.test","current_version":"9.0.8-12345",
           "hotfixes":[{"version":"Hotfix-9.0.8-1","status_text":"Applied"}]},
          {"_ref":"upgradestatus/b25m:ns1","type":"VNODE","member":"ns1.lab.test","current_version":"9.0.8-12345","hotfixes":[]}
        ]
        """;

    public FakeWapiServer()
    {
        Certificate = CreateCertificate();
        Fingerprint = CertificatePin.Fingerprint(Certificate);

        using var backup = OneDbFixture.GzipTarBackup();
        BackupBytes = backup.ToArray();
        BackupSha256 = Convert.ToHexString(SHA256.HashData(BackupBytes)).ToLowerInvariant();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 of the certificate, as lowercase hex.</summary>
    public string Fingerprint { get; }

    public int Port { get; }

    public string User { get; } = "pfr-ro";
    public string Password { get; } = "Ro-Sup3rSecret!";
    public string GridName { get; set; } = "Lab Grid";
    public string[] SupportedVersions { get; set; } = ["1.0", "2.9", "2.12", "2.10"];
    public string Token { get; } = "token-1234";

    /// <summary>The field names offered per object type through <c>?_schema</c>.</summary>
    public Dictionary<string, string[]> SchemaFields { get; } = new()
    {
        ["member"] = ["host_name", "enable_ha", "node_info", "platform"],
        ["upgradestatus"] = ["member", "type", "current_version", "hotfixes"],
    };

    /// <summary>The answer to reads of <c>member</c>.</summary>
    public string MembersJson { get; set; } = DefaultMembers;

    /// <summary>The answer to reads of <c>upgradestatus</c>.</summary>
    public string UpgradeStatusJson { get; set; } = DefaultUpgradeStatus;

    /// <summary>When set, reads of <c>upgradestatus</c> are answered with this error status instead.</summary>
    public int? UpgradeStatusError { get; set; }

    /// <summary>The host the grid claims in its backup download address; deliberately not this server.</summary>
    public string AnnouncedHost { get; set; } = "elsewhere.invalid:9";

    /// <summary>The backup served for download (a real gzip tar holding onedb.xml).</summary>
    public byte[] BackupBytes { get; }

    public string BackupSha256 { get; }

    public string ExpectedAuthorization =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));

    /// <summary>Checked before the built-in routes; return null to fall through to them.</summary>
    public Func<FakeRequest, FakeResponse?>? Extra { get; set; }

    public IReadOnlyList<FakeRequest> Requests => _requests.ToArray();

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        Certificate.Dispose();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fake-grid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Re-load from PFX so the private key is usable by the TLS stack on every platform.
        return new X509Certificate2(selfSigned.Export(X509ContentType.Pfx));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(tcp));
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        using (tcp)
        {
            try
            {
                using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(Certificate, clientCertificateRequired: false,
                    enabledSslProtocols: SslProtocols.None, checkCertificateRevocation: false);

                var request = await ReadRequestAsync(ssl);
                if (request is null) return;
                _requests.Enqueue(request);
                await WriteResponseAsync(ssl, Respond(request));
            }
            catch (Exception)
            {
                // The client refused the certificate (that is what several tests check) or hung up.
            }
        }
    }

    private FakeResponse Respond(FakeRequest request)
    {
        if (request.Authorization != ExpectedAuthorization)
        {
            return FakeResponse.Json(401,
                "{\"Error\":\"AdmConProtoError: Authorization Required\",\"code\":\"Client.Ibap.Proto\",\"text\":\"Authorization Required\"}");
        }

        if (Extra?.Invoke(request) is { } custom) return custom;

        var path = request.PathAndQuery;
        if (request.Method == "GET" && path.StartsWith("/wapi/v1.0/?_schema", StringComparison.Ordinal))
        {
            var versions = string.Join(",", SupportedVersions.Select(v => $"\"{v}\""));
            return FakeResponse.Json(200, $"{{\"requested_version\":\"1.0\",\"supported_versions\":[{versions}],\"supported_objects\":[\"grid\"]}}");
        }

        if (request.Method == "GET" && path.StartsWith("/wapi/v", StringComparison.Ordinal) && path.Contains("/grid?", StringComparison.Ordinal))
            return FakeResponse.Json(200, $"[{{\"_ref\":\"grid/b25lLmNsdXN0ZXIkMA:{GridName}\",\"name\":\"{GridName}\"}}]");

        if (request.Method == "POST" && path.Contains("fileop?_function=getgriddata", StringComparison.Ordinal))
        {
            return FakeResponse.Json(200,
                $"{{\"token\":\"{Token}\",\"url\":\"https://{AnnouncedHost}/http_direct_file_io/req_id-DOWNLOAD-1/database.bak\"}}");
        }

        if (request.Method == "GET" && path.StartsWith("/http_direct_file_io/", StringComparison.Ordinal))
            return new FakeResponse(200, "application/force-download", BackupBytes);

        if (request.Method == "POST" && path.Contains("fileop?_function=downloadcomplete", StringComparison.Ordinal))
            return FakeResponse.Json(200, "{}");

        var segments = path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.Method == "GET" && segments.Length == 3 && segments[0] == "wapi" && segments[1].StartsWith('v'))
        {
            var type = segments[2];
            if (path.Contains("_schema", StringComparison.Ordinal) && SchemaFields.TryGetValue(type, out var fields))
            {
                var list = string.Join(",", fields.Select(f => $"{{\"name\":\"{f}\"}}"));
                return FakeResponse.Json(200, $"{{\"type\":\"{type}\",\"fields\":[{list}]}}");
            }
            if (type == "member") return FakeResponse.Json(200, MembersJson);
            if (type == "upgradestatus")
            {
                return UpgradeStatusError is { } status
                    ? FakeResponse.Json(status, "{\"Error\":\"AdmConProtoError: Invalid request\",\"text\":\"The type search field is required\"}")
                    : FakeResponse.Json(200, UpgradeStatusJson);
            }
        }

        return FakeResponse.Json(404, "{\"Error\":\"AdmConProtoError: Unknown path\",\"text\":\"Unknown path\"}");
    }

    private static async Task<FakeRequest?> ReadRequestAsync(Stream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            head.Add(one[0]);
            var n = head.Count;
            if (n >= 4 && head[n - 4] == (byte)'\r' && head[n - 3] == (byte)'\n' && head[n - 2] == (byte)'\r' && head[n - 1] == (byte)'\n')
                break;
        }

        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        string? authorization = null;
        var length = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) authorization = value;
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) length = int.Parse(value);
        }

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read, length - read));
            if (n == 0) break;
            read += n;
        }
        return new FakeRequest(first[0], first[1], authorization, Encoding.UTF8.GetString(body, 0, read));
    }

    private static async Task WriteResponseAsync(Stream stream, FakeResponse response)
    {
        var reason = response.Status == 200 ? "OK" : response.Status == 401 ? "Unauthorized" : "Error";
        var head = $"HTTP/1.1 {response.Status} {reason}\r\nContent-Type: {response.ContentType}\r\n" +
                   $"Content-Length: {response.Body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(response.Body);
        await stream.FlushAsync();
    }
}
