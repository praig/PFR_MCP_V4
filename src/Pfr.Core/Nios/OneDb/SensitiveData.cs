namespace Pfr.Core.Nios.OneDb;

/// <summary>
/// Decides which onedb.xml properties must never be read into memory or stored.
/// A grid backup contains private keys, passwords, TSIG keys, tokens and license
/// strings. The reader drops any property this class flags, before it reaches
/// the rest of the application.
/// </summary>
public static class SensitiveData
{
    // Substrings that mark a property name as secret.
    private static readonly string[] SecretNameParts =
    [
        "password", "passwd", "secret", "token", "hash", "salt",
        "private_key", "private_value", "passphrase", "license_string", "key_blob",
        "certificate", "cert_data", "community",
    ];

    // Exact names that are secret but don't match the patterns above.
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "tsig_key", "value_key", "auth_key",
    };

    // Encrypted values in onedb.xml look like "{0}_{aes}_...".
    private const string EncryptedValuePrefix = "{0}_{aes}_";

    public static bool IsSensitive(string name, string value)
    {
        if (value.StartsWith(EncryptedValuePrefix, StringComparison.Ordinal))
        {
            return true;
        }

        if (SecretNames.Contains(name))
        {
            return true;
        }

        // e.g. ddns_update_key, proxy_auth_key. (tsig_key_name / tsig_key_alg are not secret.)
        if (name.EndsWith("_key", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var part in SecretNameParts)
        {
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
