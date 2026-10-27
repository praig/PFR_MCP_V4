using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pfr.Core.Storage;

public enum StoreState
{
    /// <summary>No store in this folder yet; a master password must be created.</summary>
    NotInitialized,
    /// <summary>Store exists; the master password is needed to read it.</summary>
    Locked,
    Unlocked,
}

/// <summary>
/// An encrypted folder of files, unlocked with a master password.
///
/// - The key is derived from the password with PBKDF2-SHA256 (600,000 iterations,
///   random 32-byte salt) and only ever held in memory.
/// - Each file is encrypted with AES-256-GCM using a fresh 12-byte nonce. Its path
///   relative to the store is bound in as associated data, so files can't be
///   swapped between clients without detection.
/// - store.json holds only the salt, KDF settings and an encrypted check value
///   used to verify the password. It contains no secrets.
/// - A forgotten master password cannot be recovered.
/// </summary>
public sealed class SecureStore : IDisposable
{
    private const string HeaderFile = "store.json";
    private const int FormatVersion = 1;
    private const int SaltBytes = 32;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly byte[] Magic = "PFR1"u8.ToArray();
    private static readonly byte[] CheckPlaintext = "PFR-STORE-KEY-CHECK"u8.ToArray();
    private const string CheckPath = "#key-check";

    /// <summary>OWASP 2023 guidance for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Minimum master password length.</summary>
    public const int MinimumPasswordLength = 10;

    private byte[]? _key;

    public SecureStore(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>The store's folder.</summary>
    public string Root { get; }

    public StoreState State =>
        _key is not null ? StoreState.Unlocked
        : File.Exists(Path.Combine(Root, HeaderFile)) ? StoreState.Locked
        : StoreState.NotInitialized;

    /// <summary>Creates a new store protected by <paramref name="password"/>.</summary>
    public void Initialize(string password, int iterations = DefaultIterations)
    {
        if (State != StoreState.NotInitialized)
            throw new InvalidOperationException("A store already exists in this folder.");
        if (password.Length < MinimumPasswordLength)
            throw new ArgumentException($"The master password must be at least {MinimumPasswordLength} characters.");

        Directory.CreateDirectory(Root);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = DeriveKey(password, salt, iterations);

        var header = new StoreHeader
        {
            Format = FormatVersion,
            Kdf = "PBKDF2-SHA256",
            Iterations = iterations,
            Salt = Convert.ToBase64String(salt),
            KeyCheck = Convert.ToBase64String(Seal(key, CheckPlaintext, CheckPath)),
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        WriteAtomic(Path.Combine(Root, HeaderFile),
            JsonSerializer.SerializeToUtf8Bytes(header, new JsonSerializerOptions { WriteIndented = true }));
        _key = key;
    }

    /// <summary>Unlocks the store. Returns false if the password is wrong.</summary>
    public bool Unlock(string password)
    {
        var header = ReadHeader();
        var key = DeriveKey(password, Convert.FromBase64String(header.Salt), header.Iterations);
        try
        {
            var check = Open(key, Convert.FromBase64String(header.KeyCheck), CheckPath);
            if (!check.AsSpan().SequenceEqual(CheckPlaintext)) throw new CryptographicException();
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            return false;
        }

        Lock();
        _key = key;
        return true;
    }

    /// <summary>Forgets the key. Data stays on disk, encrypted.</summary>
    public void Lock()
    {
        if (_key is null) return;
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    public void Dispose() => Lock();

    public bool Exists(string relativePath) => File.Exists(FullPath(relativePath));

    public void Write(string relativePath, ReadOnlySpan<byte> plaintext)
    {
        var path = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomic(path, Seal(RequireKey(), plaintext, Normalize(relativePath)));
    }

    public byte[] Read(string relativePath) =>
        Open(RequireKey(), File.ReadAllBytes(FullPath(relativePath)), Normalize(relativePath));

    public void WriteJson<T>(string relativePath, T value) =>
        Write(relativePath, JsonSerializer.SerializeToUtf8Bytes(value, StoreJson.Options));

    public T ReadJson<T>(string relativePath) =>
        JsonSerializer.Deserialize<T>(Read(relativePath), StoreJson.Options)
        ?? throw new InvalidDataException($"{relativePath} is empty.");

    public void Delete(string relativePath)
    {
        var path = FullPath(relativePath);
        if (File.Exists(path)) File.Delete(path);
    }

    public void DeleteFolder(string relativePath)
    {
        var path = FullPath(relativePath);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>Immediate sub-folder names under a folder in the store.</summary>
    public IEnumerable<string> Folders(string relativePath)
    {
        var path = FullPath(relativePath);
        return Directory.Exists(path)
            ? Directory.EnumerateDirectories(path).Select(Path.GetFileName).OfType<string>()
            : [];
    }

    private byte[] RequireKey() => _key ?? throw new InvalidOperationException("The store is locked.");

    private string FullPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && full != Root)
            throw new ArgumentException("Path escapes the store.", nameof(relativePath));
        return full;
    }

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/');

    private StoreHeader ReadHeader()
    {
        var path = Path.Combine(Root, HeaderFile);
        if (!File.Exists(path)) throw new InvalidOperationException("No store has been created in this folder.");
        var header = JsonSerializer.Deserialize<StoreHeader>(File.ReadAllBytes(path))
                     ?? throw new InvalidDataException("store.json is empty.");
        if (header.Format != FormatVersion)
            throw new InvalidDataException($"Unsupported store format {header.Format}.");
        return header;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    /// <summary>Layout: "PFR1" | nonce (12) | tag (16) | ciphertext.</summary>
    private static byte[] Seal(byte[] key, ReadOnlySpan<byte> plaintext, string associated)
    {
        var output = new byte[Magic.Length + NonceBytes + TagBytes + plaintext.Length];
        Magic.CopyTo(output, 0);
        var nonce = output.AsSpan(Magic.Length, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        var tag = output.AsSpan(Magic.Length + NonceBytes, TagBytes);
        var cipher = output.AsSpan(Magic.Length + NonceBytes + TagBytes);

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(associated));
        return output;
    }

    private static byte[] Open(byte[] key, byte[] data, string associated)
    {
        if (data.Length < Magic.Length + NonceBytes + TagBytes || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Not a PFR encrypted file.");

        var nonce = data.AsSpan(Magic.Length, NonceBytes);
        var tag = data.AsSpan(Magic.Length + NonceBytes, TagBytes);
        var cipher = data.AsSpan(Magic.Length + NonceBytes + TagBytes);
        var plaintext = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plaintext, Encoding.UTF8.GetBytes(associated));
        return plaintext;
    }

    /// <summary>Write to a temp file then replace, so a crash never leaves a half-written file.</summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private sealed class StoreHeader
    {
        public int Format { get; set; }
        public string Kdf { get; set; } = "";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string KeyCheck { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
    }
}
