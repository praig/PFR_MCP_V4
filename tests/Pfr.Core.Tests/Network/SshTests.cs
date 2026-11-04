using System.Text;
using System.Text.Json;
using Pfr.Core.Clients;
using Pfr.Core.Network;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Clients;

namespace Pfr.Core.Tests.Network;

public class SshCommandGuardTests
{
    [Theory]
    [InlineData("show version")]
    [InlineData("  show status  ")]
    [InlineData("SHOW version")]
    [InlineData("show grid\tdetail")]
    [InlineData("ping 10.0.0.1")]
    [InlineData("ping6 2001:db8::1")]
    [InlineData("traceroute 10.0.0.1")]
    public void Read_only_commands_are_allowed(string command)
    {
        SshCommandGuard.Check(command, allowChanges: false);
    }

    [Theory]
    [InlineData("set debug on", "set")]
    [InlineData("reset all", "reset")]
    [InlineData("ls -l", "ls")]
    [InlineData("showfoo", "showfoo")]
    public void Anything_else_needs_changes_to_be_allowed(string command, string firstWord)
    {
        var ex = Assert.Throws<ArgumentException>(() => SshCommandGuard.Check(command, allowChanges: false));

        Assert.Contains($"\"{firstWord}\" is not a read-only command", ex.Message);
        SshCommandGuard.Check(command, allowChanges: true);
    }

    [Theory]
    [InlineData("show version; reset all")]
    [InlineData("show version && reset all")]
    [InlineData("show version | tee out")]
    [InlineData("show $(whoami)")]
    [InlineData("show `id`")]
    [InlineData("show version > out")]
    [InlineData("show version < in")]
    [InlineData("show version \\")]
    public void A_read_only_command_cannot_have_another_chained_on_or_be_redirected(string command)
    {
        var ex = Assert.Throws<ArgumentException>(() => SshCommandGuard.Check(command, allowChanges: false));

        Assert.Contains("chains or redirects", ex.Message);
    }

    [Theory]
    [InlineData("show version\nreset all")]
    [InlineData("show version\r")]
    [InlineData("show\u0000")]
    public void A_command_with_a_line_break_or_control_character_is_never_run(string command)
    {
        Assert.Throws<ArgumentException>(() => SshCommandGuard.Check(command, allowChanges: false));
        Assert.Throws<ArgumentException>(() => SshCommandGuard.Check(command, allowChanges: true)); // not even when changes are allowed
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_command_is_refused(string command)
    {
        Assert.Throws<ArgumentException>(() => SshCommandGuard.Check(command, allowChanges: true));
    }
}

public class SshFingerprintTests
{
    private static readonly string Body = new('A', 43);

    [Fact]
    public void A_fingerprint_is_written_the_way_openssh_shows_it()
    {
        var fingerprint = SshFingerprint.Of([1, 2, 3]);

        Assert.StartsWith("SHA256:", fingerprint);
        Assert.Equal(7 + 43, fingerprint.Length);
        Assert.DoesNotContain('=', fingerprint);
        Assert.Equal(fingerprint, SshFingerprint.Of([1, 2, 3]));
        Assert.NotEqual(fingerprint, SshFingerprint.Of([1, 2, 4]));
    }

    [Theory]
    [InlineData("SHA256:", "")]
    [InlineData("sha256:", "")]
    [InlineData("", "")]
    [InlineData("  SHA256:", "=  ")]
    public void A_fingerprint_is_accepted_with_or_without_its_prefix_and_padding(string before, string after)
    {
        Assert.Equal("SHA256:" + Body, SshFingerprint.Normalize(before + Body + after));
    }

    [Fact]
    public void The_letter_case_of_a_fingerprint_matters()
    {
        var mixed = "SHA256:" + "aBcD" + new string('A', 39);

        Assert.Equal(mixed, SshFingerprint.Normalize(mixed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SHA256:")]
    [InlineData("SHA256:AAAA")]
    [InlineData("MD5:00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff")]
    public void Anything_else_is_not_a_fingerprint(string? text)
    {
        Assert.Null(SshFingerprint.Normalize(text));
    }

    [Fact]
    public void A_fingerprint_of_the_wrong_length_or_with_characters_that_are_not_base64_is_not_one()
    {
        Assert.Null(SshFingerprint.Normalize("SHA256:" + Body + "A"));        // 44 characters
        Assert.Null(SshFingerprint.Normalize("SHA256:" + Body[..^1]));       // 42
        Assert.Null(SshFingerprint.Normalize("SHA256:" + Body[..^1] + "-")); // 43, but not base64
        Assert.Equal(43, Body.Length);
    }

    [Fact]
    public void A_fingerprint_made_here_is_accepted_back()
    {
        var fingerprint = SshFingerprint.Of(Encoding.UTF8.GetBytes("a host key"));

        Assert.Equal(fingerprint, SshFingerprint.Normalize(fingerprint));
    }
}

public class SshRunnerTests
{
    private static readonly SshAccount Account = new("admin", "s3cret!");
    private static readonly SshTarget Gm = new("gm", "gm.lab.test");

    private static Task<bool> No(SshHostKeyException ex, CancellationToken ct) => Task.FromResult(false);
    private static Task<bool> Yes(SshHostKeyException ex, CancellationToken ct) => Task.FromResult(true);

    [Fact]
    public async Task A_host_with_no_pinned_key_is_run_after_the_engineer_confirms_the_key_it_presented()
    {
        var keys = new MemorySshHostKeys();
        var client = new FakeSshClient(FakeSshClient.Honest);
        var asked = new List<SshHostKeyException>();

        var results = await new SshRunner(client, keys).RunAsync([Gm], Account, ["show version"], new SshRunOptions(),
            (ex, _) =>
            {
                asked.Add(ex);
                return Task.FromResult(true);
            });

        var session = Assert.Single(results);
        Assert.True(session.Succeeded);
        Assert.Equal("NIOS 9.0.8", session.Commands[0].Output);

        // The engineer was shown the key the host presented, and it is now the pinned one.
        var expected = FakeSshClient.KeyOf(Gm);
        Assert.Equal(expected, Assert.Single(asked).Presented);
        Assert.Equal(expected.Fingerprint, keys.Find("gm.lab.test", 22));
        Assert.Equal([null, expected.Fingerprint], client.Calls.Select(c => c.Pinned).ToArray()); // first without a pin, then with it
    }

    [Fact]
    public async Task A_key_the_engineer_does_not_trust_is_not_pinned_and_nothing_goes_further()
    {
        var keys = new MemorySshHostKeys();
        var client = new FakeSshClient(FakeSshClient.Honest);

        var session = Assert.Single(await new SshRunner(client, keys).RunAsync([Gm], Account, ["show version"], new SshRunOptions(), No));

        Assert.False(session.Succeeded);
        Assert.Contains("has not been pinned yet", session.Failure);
        Assert.Contains(FakeSshClient.KeyOf(Gm).Fingerprint, session.Failure);
        Assert.Empty(session.Commands);
        Assert.Null(keys.Find("gm.lab.test", 22));
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task With_nobody_to_ask_a_new_key_is_refused()
    {
        var client = new FakeSshClient(FakeSshClient.Honest);

        var session = Assert.Single(await new SshRunner(client, new MemorySshHostKeys()).RunAsync([Gm], Account, ["show version"], new SshRunOptions()));

        Assert.Contains("has not been pinned yet", session.Failure);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task A_pinned_key_that_matches_runs_without_asking_anything()
    {
        var keys = new MemorySshHostKeys();
        keys.Pin("gm.lab.test", 22, FakeSshClient.KeyOf(Gm));
        var client = new FakeSshClient(FakeSshClient.Honest);

        var session = Assert.Single(await new SshRunner(client, keys).RunAsync([Gm], Account, ["show version"], new SshRunOptions(),
            (_, _) => throw new InvalidOperationException("a pinned key needs no confirmation")));

        Assert.True(session.Succeeded);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task A_host_that_presents_a_different_key_is_refused_and_never_offered_for_pinning()
    {
        var keys = new MemorySshHostKeys();
        var old = "SHA256:" + new string('B', 43);
        keys.Pin("gm.lab.test", 22, new SshHostKeyInfo("ssh-ed25519", old));
        var client = new FakeSshClient(FakeSshClient.Honest);

        var session = Assert.Single(await new SshRunner(client, keys).RunAsync([Gm], Account, ["show version"], new SshRunOptions(),
            (_, _) => throw new InvalidOperationException("a changed key must not be offered for pinning")));

        Assert.False(session.Succeeded);
        Assert.Contains("different host key", session.Failure);
        Assert.Contains(old, session.Failure);
        Assert.DoesNotContain("s3cret!", session.Failure);
        Assert.Equal(old, keys.Find("gm.lab.test", 22)); // the pinned key is untouched
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task A_host_that_cannot_be_reached_or_refuses_the_account_is_reported_with_the_others_still_run()
    {
        var keys = new MemorySshHostKeys();
        var ns1 = new SshTarget("ns1", "ns1.lab.test");
        var ns2 = new SshTarget("ns2", "ns2.lab.test");
        foreach (var target in new[] { Gm, ns1, ns2 }) keys.Pin(target.Host, 22, FakeSshClient.KeyOf(target));
        var client = new FakeSshClient((target, pinned) => target.Host == "ns1.lab.test"
            ? throw new SshFailureException("ns1.lab.test refused the user id or password for admin.")
            : FakeSshClient.Honest(target, pinned));

        var progress = new List<string>();
        var results = await new SshRunner(client, keys).RunAsync([Gm, ns1, ns2], Account, ["show version"], new SshRunOptions(),
            progress: new SyncProgress<SshSessionResult>(s => progress.Add(s.Target.Label)));

        Assert.Equal("gm,ns1,ns2", string.Join(",", results.Select(r => r.Target.Label)));
        Assert.True(results[0].Succeeded);
        Assert.Equal("ns1.lab.test refused the user id or password for admin.", results[1].Failure);
        Assert.Null(results[1].HostKey);
        Assert.True(results[2].Succeeded);
        Assert.Equal(["gm", "ns1", "ns2"], progress.Order().ToArray());
    }

    [Fact]
    public async Task A_command_that_may_not_be_run_stops_everything_before_any_connection()
    {
        var client = new FakeSshClient(FakeSshClient.Honest);
        var runner = new SshRunner(client, new MemorySshHostKeys());

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync([Gm], Account, ["show version", "reset all"], new SshRunOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync([Gm], Account, [], new SshRunOptions()));
        Assert.Empty(client.Calls);

        // Allowed on purpose, it goes ahead.
        await runner.RunAsync([Gm], Account, ["set debug off"], new SshRunOptions { AllowChanges = true });
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task Every_host_gets_the_commands_in_order()
    {
        var keys = new MemorySshHostKeys();
        var targets = new[] { Gm, new SshTarget("ns1", "ns1.lab.test", 2222) };
        foreach (var target in targets) keys.Pin(target.Host, target.Port, FakeSshClient.KeyOf(target));
        var client = new FakeSshClient(FakeSshClient.Honest);

        await new SshRunner(client, keys).RunAsync(targets, Account, ["show version", "show status"], new SshRunOptions { MaxParallel = 2 });

        Assert.All(client.Calls, c => Assert.Equal(["show version", "show status"], c.Commands.ToArray()));
        Assert.Equal([22, 2222], client.Calls.Select(c => c.Target.Port).Order().ToArray());
    }

    [Fact]
    public void An_account_never_shows_its_password()
    {
        Assert.Equal("admin", Account.ToString());
        Assert.DoesNotContain("s3cret!", Account.ToString());
    }
}

public class SshReportTests
{
    private static readonly SshTarget Gm = new("gm", "gm.lab.test");

    private static SshSessionResult Session() => new(
        Gm, new SshHostKeyInfo("ssh-ed25519", "SHA256:" + new string('A', 43)),
        [
            new SshCommandResult("show version", "NIOS 9.0.8\r\nBuild 12345\r\n", "", 0, TimeSpan.FromMilliseconds(40), null),
            new SshCommandResult("show nothing", "", "unknown command\n", 2, TimeSpan.FromMilliseconds(10), null),
            new SshCommandResult("show slow", "", "", null, TimeSpan.FromSeconds(60), "No result within 60 seconds."),
        ],
        null);

    [Fact]
    public void The_text_shows_each_command_with_its_output_errors_and_exit_status()
    {
        var text = SshReport.Text([Session()]).Replace("\r\n", "\n");

        Assert.Contains("=== gm (gm.lab.test) ===", text);
        Assert.Contains("host key SHA256:" + new string('A', 43) + " (ssh-ed25519)", text);
        Assert.Contains("$ show version\nNIOS 9.0.8\nBuild 12345\n", text);
        Assert.Contains("$ show nothing\n[stderr] unknown command\n[exit status 2]", text);
        Assert.Contains("$ show slow\n!! No result within 60 seconds.", text);
        Assert.DoesNotContain("[exit status 0]", text);
    }

    [Fact]
    public void A_host_that_was_never_reached_shows_why()
    {
        var failed = new SshSessionResult(new SshTarget("ns1", "10.0.0.2"), null, [], "Could not connect to 10.0.0.2:22: timed out");

        var text = SshReport.Text([failed]).Replace("\r\n", "\n");

        Assert.Contains("=== ns1 (10.0.0.2) ===", text);
        Assert.Contains("!! Could not connect to 10.0.0.2:22: timed out", text);
        Assert.False(failed.Succeeded);
    }

    [Fact]
    public void A_target_named_by_its_address_is_not_written_twice()
    {
        var session = new SshSessionResult(new SshTarget("10.0.0.2", "10.0.0.2"), null, [], null);

        Assert.Contains("=== 10.0.0.2 ===", SshReport.Text([session]));
    }

    [Fact]
    public void The_json_has_the_same_facts()
    {
        using var document = JsonDocument.Parse(SshReport.Json([Session()]));
        var host = document.RootElement[0];

        Assert.Equal("gm.lab.test", host.GetProperty("host").GetString());
        Assert.Equal(22, host.GetProperty("port").GetInt32());
        Assert.Equal("ssh-ed25519", host.GetProperty("hostKey").GetProperty("algorithm").GetString());
        var commands = host.GetProperty("commands");
        Assert.Equal(3, commands.GetArrayLength());
        Assert.Equal("NIOS 9.0.8\r\nBuild 12345\r\n", commands[0].GetProperty("output").GetString());
        Assert.Equal(2, commands[1].GetProperty("exitStatus").GetInt32());
        Assert.Equal("unknown command\n", commands[1].GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, commands[2].GetProperty("exitStatus").ValueKind);
        Assert.Equal("No result within 60 seconds.", commands[2].GetProperty("failure").GetString());
    }
}

/// <summary>Pinned host keys kept with a client, encrypted like the rest of its data.</summary>
public sealed class SshHostKeyStoreTests : IDisposable
{
    private static readonly string One = "SHA256:" + new string('A', 43);
    private static readonly string Two = "SHA256:" + new string('B', 43);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfr-ssh-{Guid.NewGuid():N}");
    private readonly ClientWorkspace _ws;

    public SshHostKeyStoreTests()
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

    [Fact]
    public void A_key_is_pinned_found_replaced_and_forgotten()
    {
        var client = _ws.CreateClient("Acme");
        Assert.Empty(_ws.GetSshHostKeys(client.Id));
        Assert.Null(_ws.FindSshHostKey(client.Id, "gm.lab.test", 22));

        _ws.PinSshHostKey(client.Id, "gm.lab.test", 22, "ssh-ed25519", One);
        Assert.Equal(One, _ws.FindSshHostKey(client.Id, "GM.lab.test", 22)!.Fingerprint); // host names are not case sensitive

        // Pinning again replaces the key for that host and port.
        _ws.PinSshHostKey(client.Id, "GM.lab.test", 22, "ssh-rsa", Two);
        var only = Assert.Single(_ws.GetSshHostKeys(client.Id));
        Assert.Equal(Two, only.Fingerprint);
        Assert.Equal("ssh-rsa", only.Algorithm);

        // A different port is a different host key.
        _ws.PinSshHostKey(client.Id, "gm.lab.test", 2222, "ssh-ed25519", One);
        Assert.Equal([22, 2222], _ws.GetSshHostKeys(client.Id).Select(k => k.Port).ToArray());

        _ws.DeleteSshHostKey(client.Id, "gm.lab.test", 22);
        Assert.Equal(2222, Assert.Single(_ws.GetSshHostKeys(client.Id)).Port);
        _ws.DeleteSshHostKey(client.Id, "no.such.host", 22); // forgetting what is not there is harmless
    }

    [Fact]
    public void Keys_are_kept_per_client()
    {
        var acme = _ws.CreateClient("Acme");
        var other = _ws.CreateClient("Other");

        _ws.PinSshHostKey(acme.Id, "gm.lab.test", 22, "ssh-ed25519", One);

        Assert.Single(_ws.GetSshHostKeys(acme.Id));
        Assert.Empty(_ws.GetSshHostKeys(other.Id));
    }

    [Fact]
    public void Nothing_about_the_keys_is_readable_on_disk_and_deleting_the_client_deletes_them()
    {
        var client = _ws.CreateClient("Acme");
        _ws.PinSshHostKey(client.Id, "gm.lab.test", 22, "ssh-ed25519", One);

        var files = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories);
        Assert.All(files, f => Assert.DoesNotContain("gm.lab.test", File.ReadAllText(f)));
        Assert.All(files, f => Assert.DoesNotContain(One, File.ReadAllText(f)));

        _ws.DeleteClient(client.Id);
        Assert.Null(_ws.FindSshHostKey(client.Id, "gm.lab.test", 22));
    }

    [Fact]
    public void The_runner_reads_and_writes_the_keys_through_an_adapter()
    {
        var client = _ws.CreateClient("Acme");
        var keys = new ClientSshHostKeys(_ws, client.Id);
        Assert.Null(keys.Find("gm.lab.test", 22));

        keys.Pin("gm.lab.test", 22, new SshHostKeyInfo("ssh-ed25519", One));

        Assert.Equal(One, keys.Find("gm.lab.test", 22));
        Assert.Equal(One, _ws.FindSshHostKey(client.Id, "gm.lab.test", 22)!.Fingerprint);

        keys.Forget("gm.lab.test", 22);
        Assert.Null(keys.Find("gm.lab.test", 22));
        keys.Forget("gm.lab.test", 22); // already gone: harmless
    }

    [Fact]
    public void Memory_keys_can_be_forgotten_too()
    {
        var keys = new MemorySshHostKeys();
        keys.Pin("gm.lab.test", 22, new SshHostKeyInfo("ssh-ed25519", One));
        keys.Pin("gm.lab.test", 2222, new SshHostKeyInfo("ssh-ed25519", Two));

        keys.Forget("GM.lab.test", 22);

        Assert.Null(keys.Find("gm.lab.test", 22));
        Assert.Equal(Two, keys.Find("gm.lab.test", 2222));
    }

    [Fact]
    public void A_key_needs_a_host_and_a_client_that_exists()
    {
        var client = _ws.CreateClient("Acme");

        Assert.Throws<ArgumentException>(() => _ws.PinSshHostKey(client.Id, "  ", 22, "ssh-ed25519", One));
        Assert.Throws<ArgumentException>(() => _ws.PinSshHostKey("no-such-client", "gm.lab.test", 22, "ssh-ed25519", One));
    }
}
