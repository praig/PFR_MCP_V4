using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pfr.Core.Clients;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Pfr.Core.Network;

/// <summary>A host to run commands on: what to call it, its address or name, and its SSH port.</summary>
public sealed record SshTarget(string Label, string Host, int Port = 22);

/// <summary>A user id and password for the host. <see cref="ToString"/> never gives the password.</summary>
public sealed record SshAccount(string User, string Password)
{
    public override string ToString() => User;
}

/// <summary>The key a host presented: its type, and its fingerprint in OpenSSH's form.</summary>
public sealed record SshHostKeyInfo(string Algorithm, string Fingerprint);

public sealed record SshCommandResult(
    string Command, string Output, string Error, int? ExitStatus, TimeSpan Elapsed, string? Failure);

/// <summary>What happened on one host: the key it presented and each command's output, or why it did not get that far.</summary>
public sealed record SshSessionResult(
    SshTarget Target, SshHostKeyInfo? HostKey, IReadOnlyList<SshCommandResult> Commands, string? Failure)
{
    public bool Succeeded => Failure is null && Commands.All(c => c.Failure is null);
}

public enum SshHostKeyProblem
{
    /// <summary>No key has been pinned for this host yet: the engineer has to confirm the one it presents.</summary>
    NotPinned,

    /// <summary>The host presented a different key from the one pinned.</summary>
    Mismatch,
}

/// <summary>The host's key is not one we trust. Nothing, least of all a password, was sent to it.</summary>
public sealed class SshHostKeyException(SshHostKeyProblem problem, SshTarget target, SshHostKeyInfo presented, string? pinned)
    : Exception(MessageFor(problem, target, presented, pinned))
{
    public SshHostKeyProblem Problem { get; } = problem;
    public SshTarget Target { get; } = target;
    public SshHostKeyInfo Presented { get; } = presented;
    public string? PinnedFingerprint { get; } = pinned;

    private static string MessageFor(SshHostKeyProblem problem, SshTarget target, SshHostKeyInfo presented, string? pinned) =>
        problem == SshHostKeyProblem.NotPinned
            ? $"The host key of {target.Host} has not been pinned yet. It presented {presented.Algorithm} {presented.Fingerprint}. " +
              "Compare that with the appliance's own, then pin it to continue."
            : $"{target.Host} presented a different host key from the one pinned. Pinned: {pinned}. Presented: {presented.Fingerprint}. " +
              "This can mean the appliance was rebuilt or replaced, or that something is intercepting the connection. Nothing was sent.";
}

/// <summary>The connection or sign-in failed, in words fit to show.</summary>
public sealed class SshFailureException(string message) : Exception(message);

/// <summary>SSH key fingerprints in the form OpenSSH shows them (SHA256: and 43 characters).</summary>
public static partial class SshFingerprint
{
    /// <summary>The fingerprint of a public key as the host sends it.</summary>
    public static string Of(byte[] publicKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKey)).TrimEnd('=');

    /// <summary>
    /// A fingerprint as typed or pasted ("SHA256:abc...", "abc...", with or without the padding) in the standard form,
    /// or null if it isn't one.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var body = text.Trim();
        if (body.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) body = body[7..];
        body = body.TrimEnd('=');
        return Body().IsMatch(body) ? "SHA256:" + body : null;
    }

    [GeneratedRegex("^[A-Za-z0-9+/]{43}$")]
    private static partial Regex Body();
}

/// <summary>
/// Which commands may be run. By default only read-only NIOS CLI commands (show, ping, traceroute) are allowed, one at
/// a time: nothing that changes the appliance, and no way to chain a second command onto a harmless one. Changes have
/// to be allowed deliberately.
/// </summary>
public static class SshCommandGuard
{
    private static readonly string[] ReadOnly = ["show", "ping", "ping6", "traceroute", "traceroute6"];
    private static readonly char[] Chaining = [';', '&', '|', '`', '$', '<', '>', '\\'];

    public static IReadOnlyList<string> ReadOnlyCommands => ReadOnly;

    /// <exception cref="ArgumentException">The command may not be run, and why.</exception>
    public static void Check(string command, bool allowChanges)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("A command is empty.");
        if (command.Any(c => c != '\t' && char.IsControl(c)))
            throw new ArgumentException($"\"{Shorten(command)}\" has a line break or other control character. Give one command per line.");
        if (allowChanges) return;

        var first = command.TrimStart().Split([' ', '\t'], 2)[0];
        if (!ReadOnly.Contains(first, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"\"{first}\" is not a read-only command. Only {string.Join(", ", ReadOnly)} commands run unless changes are allowed.");
        }
        if (command.IndexOfAny(Chaining) >= 0)
        {
            throw new ArgumentException(
                $"\"{Shorten(command)}\" chains or redirects. A read-only command runs on its own, with no ; & | ` $ < > or backslash.");
        }
    }

    private static string Shorten(string command) => command.Length <= 60 ? command : command[..57] + "...";
}

/// <summary>Where pinned host keys are kept.</summary>
public interface ISshHostKeyStore
{
    /// <summary>The fingerprint pinned for the host and port, or null.</summary>
    string? Find(string host, int port);

    void Pin(string host, int port, SshHostKeyInfo key);

    /// <summary>Forgets the key pinned for the host and port (it has to be confirmed again next time). Does nothing if none is.</summary>
    void Forget(string host, int port);
}

/// <summary>Pinned host keys in memory only (for a one-off check).</summary>
public sealed class MemorySshHostKeys : ISshHostKeyStore
{
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public string? Find(string host, int port) => _keys.GetValueOrDefault($"{host}:{port}");

    public void Pin(string host, int port, SshHostKeyInfo key) => _keys[$"{host}:{port}"] = key.Fingerprint;

    public void Forget(string host, int port) => _keys.Remove($"{host}:{port}");
}

/// <summary>A client's pinned host keys, kept encrypted with the rest of the client's data.</summary>
public sealed class ClientSshHostKeys(ClientWorkspace workspace, string clientId) : ISshHostKeyStore
{
    public string? Find(string host, int port) => workspace.FindSshHostKey(clientId, host, port)?.Fingerprint;

    public void Pin(string host, int port, SshHostKeyInfo key) =>
        workspace.PinSshHostKey(clientId, host, port, key.Algorithm, key.Fingerprint);

    public void Forget(string host, int port) => workspace.DeleteSshHostKey(clientId, host, port);
}

/// <summary>Connects to a host, checks its key against the pinned one, signs in, and runs commands. Tests replace it.</summary>
public interface ISshClient
{
    /// <summary>
    /// The host key is checked before anything is sent: with no <paramref name="pinnedFingerprint"/> or a different one,
    /// the connection stops and no password is sent.
    /// </summary>
    /// <exception cref="SshHostKeyException">The key is not pinned, or is not the pinned one.</exception>
    /// <exception cref="SshFailureException">The host could not be reached, or refused the account.</exception>
    Task<SshSessionResult> RunAsync(SshTarget target, SshAccount account, string? pinnedFingerprint,
        IReadOnlyList<string> commands, TimeSpan connectTimeout, TimeSpan commandTimeout, CancellationToken ct);
}

/// <summary>The SSH.NET implementation. Commands run one at a time, each in its own channel (no terminal).</summary>
public sealed class SshNetClient : ISshClient
{
    public Task<SshSessionResult> RunAsync(SshTarget target, SshAccount account, string? pinnedFingerprint,
        IReadOnlyList<string> commands, TimeSpan connectTimeout, TimeSpan commandTimeout, CancellationToken ct) =>
        Task.Run(() => Run(target, account, pinnedFingerprint, commands, connectTimeout, commandTimeout, ct), ct);

    private sealed class HostKeyState
    {
        public SshHostKeyInfo? Presented;
        public SshHostKeyException? Problem;
    }

    private static SshSessionResult Run(SshTarget target, SshAccount account, string? pinned,
        IReadOnlyList<string> commands, TimeSpan connectTimeout, TimeSpan commandTimeout, CancellationToken ct)
    {
        var connection = new PasswordConnectionInfo(target.Host, target.Port, account.User, account.Password) { Timeout = connectTimeout };
        using var client = new SshClient(connection);

        // The key arrives during the handshake, before the account is used. Refusing it here means no password is sent.
        var state = new HostKeyState();
        client.HostKeyReceived += (_, e) =>
        {
            var presented = new SshHostKeyInfo(e.HostKeyName, SshFingerprint.Of(e.HostKey));
            state.Presented = presented;
            if (pinned is null)
            {
                state.Problem = new SshHostKeyException(SshHostKeyProblem.NotPinned, target, presented, null);
                e.CanTrust = false;
            }
            else if (!string.Equals(pinned, presented.Fingerprint, StringComparison.Ordinal))
            {
                state.Problem = new SshHostKeyException(SshHostKeyProblem.Mismatch, target, presented, pinned);
                e.CanTrust = false;
            }
            else
            {
                e.CanTrust = true;
            }
        };

        try
        {
            client.Connect();
        }
        catch (Exception) when (state.Problem is not null)
        {
            throw state.Problem!;
        }
        catch (SshAuthenticationException)
        {
            throw new SshFailureException($"{target.Host} refused the user id or password for {account.User}.");
        }
        catch (SshOperationTimeoutException)
        {
            throw new SshFailureException($"{target.Host} did not answer within {connectTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is SshConnectionException or System.Net.Sockets.SocketException or SshException)
        {
            throw new SshFailureException($"Could not connect to {target.Host}:{target.Port}: {ex.Message}");
        }

        var results = new List<SshCommandResult>();
        foreach (var text in commands)
        {
            ct.ThrowIfCancellationRequested();
            var started = DateTime.UtcNow;
            try
            {
                using var command = client.CreateCommand(text);
                command.CommandTimeout = commandTimeout;
                var output = command.Execute();
                int? exit = command.ExitStatus;
                results.Add(new SshCommandResult(text, output, command.Error ?? "", exit, DateTime.UtcNow - started, null));
            }
            catch (SshOperationTimeoutException)
            {
                results.Add(new SshCommandResult(text, "", "", null, DateTime.UtcNow - started,
                    $"No result within {commandTimeout.TotalSeconds:0} seconds."));
            }
            catch (SshException ex)
            {
                results.Add(new SshCommandResult(text, "", "", null, DateTime.UtcNow - started, ex.Message));
            }
        }

        return new SshSessionResult(target, state.Presented, results, null);
    }
}

public sealed record SshRunOptions
{
    /// <summary>Allow commands other than read-only ones.</summary>
    public bool AllowChanges { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Hosts worked on at once. One at a time by default, so a host key question is asked once and clearly.</summary>
    public int MaxParallel { get; init; } = 1;
}

/// <summary>
/// Runs commands on hosts. Every host's key must be pinned first: one that is not is put to
/// <c>confirmNewKey</c> (which shows the fingerprint to the engineer) and pinned only if it says yes; one that has
/// changed is refused.
/// </summary>
public sealed class SshRunner(ISshClient client, ISshHostKeyStore keys)
{
    /// <param name="targets">The hosts.</param>
    /// <param name="account">The account to sign in with on each.</param>
    /// <param name="commands">The commands, run in this order on each host.</param>
    /// <param name="options">What is allowed and how long to wait.</param>
    /// <param name="confirmNewKey">Asked when a host has no pinned key: true pins the key it presented and carries on.</param>
    /// <param name="progress">Told as each host finishes.</param>
    /// <exception cref="ArgumentException">A command may not be run; nothing is connected to.</exception>
    public async Task<IReadOnlyList<SshSessionResult>> RunAsync(
        IReadOnlyList<SshTarget> targets, SshAccount account, IReadOnlyList<string> commands, SshRunOptions options,
        Func<SshHostKeyException, CancellationToken, Task<bool>>? confirmNewKey = null,
        IProgress<SshSessionResult>? progress = null, CancellationToken ct = default)
    {
        if (commands.Count == 0) throw new ArgumentException("There is no command to run.");
        foreach (var command in commands) SshCommandGuard.Check(command, options.AllowChanges);

        var results = new SshSessionResult[targets.Count];
        using var gate = new SemaphoreSlim(Math.Clamp(options.MaxParallel, 1, 16));
        await Task.WhenAll(targets.Select(async (target, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[index] = await RunOneAsync(target, account, commands, options, confirmNewKey, ct);
                progress?.Report(results[index]);
            }
            finally
            {
                gate.Release();
            }
        }));
        return results;
    }

    private async Task<SshSessionResult> RunOneAsync(SshTarget target, SshAccount account, IReadOnlyList<string> commands,
        SshRunOptions options, Func<SshHostKeyException, CancellationToken, Task<bool>>? confirmNewKey, CancellationToken ct)
    {
        var pinned = keys.Find(target.Host, target.Port);
        try
        {
            try
            {
                return await client.RunAsync(target, account, pinned, commands, options.ConnectTimeout, options.CommandTimeout, ct);
            }
            catch (SshHostKeyException ex) when (ex.Problem == SshHostKeyProblem.NotPinned && confirmNewKey is not null)
            {
                // First time for this host: the engineer decides whether the key it presented is the right one.
                if (!await confirmNewKey(ex, ct)) return new SshSessionResult(target, ex.Presented, [], ex.Message);

                keys.Pin(target.Host, target.Port, ex.Presented);
                return await client.RunAsync(target, account, ex.Presented.Fingerprint, commands, options.ConnectTimeout, options.CommandTimeout, ct);
            }
        }
        catch (SshHostKeyException ex)
        {
            return new SshSessionResult(target, ex.Presented, [], ex.Message);
        }
        catch (SshFailureException ex)
        {
            return new SshSessionResult(target, null, [], ex.Message);
        }
    }
}

/// <summary>Command output as plain text or JSON.</summary>
public static class SshReport
{
    public static string Text(IReadOnlyList<SshSessionResult> sessions)
    {
        var text = new StringBuilder();
        foreach (var session in sessions)
        {
            text.AppendLine($"=== {session.Target.Label}{(session.Target.Label == session.Target.Host ? "" : $" ({session.Target.Host})")} ===");
            if (session.HostKey is { } key) text.AppendLine($"host key {key.Fingerprint} ({key.Algorithm})");
            if (session.Failure is not null)
            {
                text.AppendLine($"!! {session.Failure}");
                text.AppendLine();
                continue;
            }

            foreach (var result in session.Commands)
            {
                text.AppendLine($"$ {result.Command}");
                if (result.Failure is not null) text.AppendLine($"!! {result.Failure}");
                if (result.Output.Length > 0) text.AppendLine(result.Output.TrimEnd('\r', '\n'));
                if (result.Error.Length > 0) text.AppendLine("[stderr] " + result.Error.TrimEnd('\r', '\n'));
                if (result.ExitStatus is { } exit and not 0) text.AppendLine($"[exit status {exit}]");
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    public static string Json(IReadOnlyList<SshSessionResult> sessions) => JsonSerializer.Serialize(
        sessions.Select(s => new
        {
            host = s.Target.Host, port = s.Target.Port, label = s.Target.Label,
            hostKey = s.HostKey is null ? null : new { algorithm = s.HostKey.Algorithm, fingerprint = s.HostKey.Fingerprint },
            failure = s.Failure,
            commands = s.Commands.Select(c => new
            {
                command = c.Command, output = c.Output, error = c.Error, exitStatus = c.ExitStatus,
                seconds = Math.Round(c.Elapsed.TotalSeconds, 2), failure = c.Failure,
            }).ToList(),
        }).ToList(),
        new JsonSerializerOptions { WriteIndented = true });
}
