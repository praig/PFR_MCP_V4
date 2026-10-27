using System.Security.Cryptography;
using System.Text;
using Pfr.Core.Storage;

namespace Pfr.Core.Tests.Clients;

public sealed class SecureStoreTests : IDisposable
{
    // Low iteration count keeps tests fast; production uses SecureStore.DefaultIterations.
    internal const int TestIterations = 1_000;
    private const string Password = "correct horse battery";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfr-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private SecureStore NewStore()
    {
        var store = new SecureStore(_dir);
        store.Initialize(Password, TestIterations);
        return store;
    }

    [Fact]
    public void Lifecycle_not_initialized_then_unlocked_then_locked()
    {
        var store = new SecureStore(_dir);
        Assert.Equal(StoreState.NotInitialized, store.State);

        store.Initialize(Password, TestIterations);
        Assert.Equal(StoreState.Unlocked, store.State);

        store.Lock();
        Assert.Equal(StoreState.Locked, store.State);
        Assert.Throws<InvalidOperationException>(() => store.Read("x"));
    }

    [Fact]
    public void Wrong_password_is_rejected_and_right_one_works()
    {
        NewStore().Write("a/b.pfr", "hello"u8);

        var reopened = new SecureStore(_dir);
        Assert.Equal(StoreState.Locked, reopened.State);
        Assert.False(reopened.Unlock("not the password"));
        Assert.Equal(StoreState.Locked, reopened.State);

        Assert.True(reopened.Unlock(Password));
        Assert.Equal("hello", Encoding.UTF8.GetString(reopened.Read("a/b.pfr")));
    }

    [Fact]
    public void Files_on_disk_are_encrypted_and_header_holds_no_secrets()
    {
        NewStore().Write("clients/x/client.pfr", Encoding.UTF8.GetBytes("Acme Corporation secret notes"));

        var raw = File.ReadAllBytes(Path.Combine(_dir, "clients", "x", "client.pfr"));
        Assert.Equal("PFR1"u8.ToArray(), raw[..4]);
        Assert.DoesNotContain("Acme", Encoding.UTF8.GetString(raw));

        var header = File.ReadAllText(Path.Combine(_dir, "store.json"));
        Assert.DoesNotContain(Password, header);
        Assert.Contains("PBKDF2-SHA256", header);
    }

    [Fact]
    public void Tampering_or_moving_a_file_is_detected()
    {
        var store = NewStore();
        store.Write("clients/a/client.pfr", "client A"u8);
        store.Write("clients/b/client.pfr", "client B"u8);

        // Flip one ciphertext byte.
        var path = Path.Combine(_dir, "clients", "a", "client.pfr");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(path, bytes);
        Assert.ThrowsAny<CryptographicException>(() => store.Read("clients/a/client.pfr"));

        // Copy B's file over A's: the path is bound in, so it doesn't decrypt as A.
        File.Copy(Path.Combine(_dir, "clients", "b", "client.pfr"), path, overwrite: true);
        Assert.ThrowsAny<CryptographicException>(() => store.Read("clients/a/client.pfr"));
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        var store = NewStore();
        store.Write("x.pfr", "same"u8);
        var first = File.ReadAllBytes(Path.Combine(_dir, "x.pfr"));
        store.Write("x.pfr", "same"u8);
        Assert.NotEqual(first, File.ReadAllBytes(Path.Combine(_dir, "x.pfr")));
    }

    [Fact]
    public void Rejects_short_passwords_reinitializing_and_escaping_paths()
    {
        var store = new SecureStore(_dir);
        Assert.Throws<ArgumentException>(() => store.Initialize("short", TestIterations));

        store.Initialize(Password, TestIterations);
        Assert.Throws<InvalidOperationException>(() => new SecureStore(_dir).Initialize(Password, TestIterations));
        Assert.Throws<ArgumentException>(() => store.Write("../outside.pfr", "x"u8));
    }

    [Fact]
    public void Data_location_defaults_next_to_the_app_and_can_be_overridden()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "PfrData"), DataLocation.Resolve());
        Assert.Equal(Path.GetFullPath(_dir), DataLocation.Resolve(_dir));
        Assert.Null(DataLocation.CheckWritable(_dir));
    }
}
