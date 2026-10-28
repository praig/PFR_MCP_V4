using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Pfr.Core.Clients;

namespace Pfr.Core.Wapi;

/// <summary>What a successful connection learned about the grid.</summary>
public sealed record WapiConnectionInfo(string ApiVersion, string? GridName, string? CertificateFingerprint);

/// <summary>
/// A read-only client for a grid's WAPI.
/// <list type="bullet">
/// <item>The grid's TLS certificate must match the fingerprint pinned on the connection. Anything else drops
/// the connection during the TLS handshake, before a user id or password is sent; verification is never
/// switched off.</item>
/// <item>Only GET requests are made, plus the two <c>fileop</c> calls that request and finish a backup
/// download. Every other call is refused by <see cref="EnsureAllowed"/>.</item>
/// <item>The WAPI version is the connection's, or the newest the grid says it supports.</item>
/// </list>
/// Passwords are never put in exception messages.
/// </summary>
public sealed class WapiClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BackupPrepareTimeout = TimeSpan.FromMinutes(30);
    private static readonly string[] SchemaProbeVersions = ["v1.0", "v2.0"];

    /// <summary>The only non-GET calls allowed: ask for a grid backup download, and say it has finished.</summary>
    private static readonly string[] ReadOnlyFunctions = ["getgriddata", "downloadcomplete"];

    private readonly GridConnection _connection;
    private readonly Credential _account;
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly string? _pinned;
    private volatile string? _observed;
    private string? _version;

    public WapiClient(GridConnection connection, Credential account)
        : this(connection, account, null)
    {
    }

    /// <summary>For tests: use a different handler (no TLS pinning is applied to a custom handler).</summary>
    internal WapiClient(GridConnection connection, Credential account, HttpMessageHandler? handler)
    {
        _connection = connection;
        _account = account;
        _baseUri = new Uri(connection.BaseUrl);
        _pinned = CertificatePin.Normalize(connection.CertificateFingerprint);

        handler ??= new SocketsHttpHandler
        {
            UseProxy = false,         // grids are reached directly
            AllowAutoRedirect = false, // never forward credentials somewhere else
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateCertificate },
        };

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.Username}:{account.Password}")));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>A client that signs in with the connection's read-only account.</summary>
    /// <exception cref="WapiException">The connection has no read-only account.</exception>
    public static WapiClient ForReadOnly(GridConnection connection) =>
        new(connection, connection.ReadOnly
                        ?? throw new WapiException("This grid has no read-only account saved. Add one to the connection first."));

    /// <summary>The grid's address and port, for messages.</summary>
    public string Host => _baseUri.Authority;

    /// <summary>The WAPI version in use, once known (after <see cref="ConnectAsync"/> or the first request).</summary>
    public string? ApiVersion => _version;

    /// <summary>Checks the certificate, the account and the WAPI version, and reads the grid's name.</summary>
    public async Task<WapiConnectionInfo> ConnectAsync(CancellationToken ct = default)
    {
        var version = await EnsureVersionAsync(ct).ConfigureAwait(false);
        using var doc = await GetJsonAsync(Api(version, "grid?_return_fields=name"), ct).ConfigureAwait(false);

        string? name = null;
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
            && root[0].ValueKind == JsonValueKind.Object
            && root[0].TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            name = n.GetString();
        }
        return new WapiConnectionInfo(version, name, _observed);
    }

    /// <summary>
    /// Reads every object of a type, a page at a time (WAPI paging). Elements are copies, safe to keep.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> GetAllAsync(
        string objectType, string? returnFields = null, int pageSize = 1000,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var version = await EnsureVersionAsync(ct).ConfigureAwait(false);
        string? pageId = null;
        do
        {
            var query = new StringBuilder($"_paging=1&_return_as_object=1&_max_results={pageSize}");
            if (!string.IsNullOrEmpty(returnFields)) query.Append("&_return_fields=").Append(Uri.EscapeDataString(returnFields));
            if (pageId is not null) query.Append("&_page_id=").Append(Uri.EscapeDataString(pageId));

            using var doc = await GetJsonAsync(Api(version, $"{objectType}?{query}"), ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                throw new WapiException($"{Host} returned an unexpected answer for {objectType}.");
            }

            foreach (var item in result.EnumerateArray()) yield return item.Clone();

            pageId = root.TryGetProperty("next_page_id", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }
        while (pageId is not null);
    }

    /// <summary>
    /// Asks the grid for a backup and hands the download to <paramref name="consume"/> as a stream, so it can be
    /// parsed without being saved. Tells the grid when done, even if <paramref name="consume"/> fails.
    /// </summary>
    public async Task<T> DownloadBackupAsync<T>(Func<Stream, Task<T>> consume, CancellationToken ct = default)
    {
        var version = await EnsureVersionAsync(ct).ConfigureAwait(false);

        string token;
        string url;
        using (var response = await SendAsync(HttpMethod.Post, Api(version, "fileop?_function=getgriddata"),
                   "{\"type\":\"BACKUP\"}", BackupPrepareTimeout, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
        using (var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false))
        {
            token = RequiredText(doc.RootElement, "token");
            url = RequiredText(doc.RootElement, "url");
        }

        // Only the path of the returned address is used, on the host we are already connected to and trust:
        // credentials are never sent to an address the grid's answer merely names.
        var download = new Uri(_baseUri, DownloadPath(url));

        try
        {
            using var file = await SendAsync(HttpMethod.Get, download, null, Timeout.InfiniteTimeSpan,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!file.IsSuccessStatusCode) throw await ErrorAsync(file, ct).ConfigureAwait(false);

            await using var stream = await file.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await consume(stream).ConfigureAwait(false);
        }
        finally
        {
            await CompleteDownloadAsync(version, token).ConfigureAwait(false);
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// The read-only rule: GET is always fine; the only POSTs are the two backup download calls on
    /// <c>fileop</c>. Anything else (PUT, DELETE, POST to other objects or functions) is refused.
    /// </summary>
    /// <exception cref="WapiException">The call is not allowed.</exception>
    internal static void EnsureAllowed(HttpMethod method, Uri uri)
    {
        if (method == HttpMethod.Get) return;

        if (method == HttpMethod.Post
            && uri.AbsolutePath.EndsWith("/fileop", StringComparison.Ordinal)
            && QueryValue(uri.Query, "_function") is { } function
            && ReadOnlyFunctions.Contains(function))
        {
            return;
        }

        throw new WapiException($"Blocked {method.Method} {uri.AbsolutePath}: this client is read-only.");
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0 && pair[..equals] == key) return Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return null;
    }

    private Uri Api(string version, string relative) => new(_baseUri, $"/wapi/v{version}/{relative}");

    private static string DownloadPath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            ? absolute.PathAndQuery
            : url.StartsWith('/') ? url : "/" + url;

    private string RequiredText(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : throw new WapiException($"{Host} did not return a backup download ({name} is missing).");

    /// <summary>Called by the TLS layer: the connection is accepted only if the certificate is the pinned one.</summary>
    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        var observed = CertificatePin.Fingerprint(certificate);
        _observed = observed;
        return _pinned is not null && string.Equals(observed, _pinned, StringComparison.Ordinal);
    }

    private async Task<string> EnsureVersionAsync(CancellationToken ct)
    {
        if (_version is not null) return _version;

        var configured = (_connection.WapiVersion ?? "").Trim().TrimStart('v', 'V');
        if (configured.Length > 0) return _version = configured;

        WapiException? last = null;
        foreach (var probe in SchemaProbeVersions)
        {
            try
            {
                using var doc = await GetJsonAsync(new Uri(_baseUri, $"/wapi/{probe}/?_schema"), ct).ConfigureAwait(false);
                if (NewestVersion(doc.RootElement) is { } newest) return _version = newest;
            }
            catch (WapiRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                last = ex;
            }
        }

        throw last ?? new WapiException(
            $"{Host} did not say which WAPI versions it supports. Set the WAPI version on the connection.");
    }

    /// <summary>The highest version in the grid's <c>supported_versions</c>, compared as numbers (2.12 is newer than 2.9).</summary>
    private static string? NewestVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("supported_versions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        Version? bestVersion = null;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var text = item.GetString();
            if (text is null || !Version.TryParse(text, out var version)) continue;
            if (bestVersion is null || version > bestVersion)
            {
                best = text;
                bestVersion = version;
            }
        }
        return best;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, uri, null, RequestTimeout,
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct).ConfigureAwait(false);

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new WapiException($"{Host} did not return the expected data. Is this a Grid Master's WAPI address?", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string? jsonBody, TimeSpan timeout,
        HttpCompletionOption completion, CancellationToken ct)
    {
        EnsureAllowed(method, uri);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, uri);
        if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        try
        {
            return await _http.SendAsync(request, completion, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException && _observed is not null)
        {
            throw new WapiCertificateException(
                _pinned is null ? CertificateProblem.NotPinned : CertificateProblem.Mismatch, _observed, _pinned, Host);
        }
        catch (HttpRequestException ex)
        {
            throw new WapiException($"Could not connect to {Host}: {ex.InnerException?.Message ?? ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WapiException($"{Host} did not respond in time.");
        }
    }

    /// <summary>A friendly error for a non-success answer. The user id is named; the password never is.</summary>
    private async Task<WapiRequestException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var detail = "";
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "text", "Error" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        detail = value.GetString() ?? "";
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            // No readable detail; the status says enough.
        }
        if (detail.Length > 300) detail = detail[..300] + "…";

        var status = response.StatusCode;
        var message = status switch
        {
            HttpStatusCode.Unauthorized => $"{Host} rejected the user id or password for \"{_account.Username}\".",
            HttpStatusCode.Forbidden => $"The account \"{_account.Username}\" is not allowed to do this on the grid" +
                                        (detail.Length > 0 ? $": {detail}" : "."),
            _ => $"{Host} answered {(int)status} {response.ReasonPhrase}" + (detail.Length > 0 ? $": {detail}" : "."),
        };
        return new WapiRequestException(status, message);
    }

    /// <summary>Tells the grid the download is finished so it can clean up. Best effort.</summary>
    private async Task CompleteDownloadAsync(string version, string token)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Post, Api(version, "fileop?_function=downloadcomplete"),
                JsonSerializer.Serialize(new { token }), RequestTimeout, HttpCompletionOption.ResponseContentRead,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (WapiException)
        {
            // The grid removes its temporary file on its own schedule anyway.
        }
    }
}
