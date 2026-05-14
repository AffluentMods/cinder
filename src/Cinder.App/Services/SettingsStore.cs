using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cinder.App.Services;

public sealed record CinderSettings
{
    public string Theme { get; init; } = "Dark";              // "Dark" | "Light" | "System"
    public string Density { get; init; } = "Comfortable";     // "Comfortable" | "Compact"
    public bool VimModeInHex { get; init; } = false;
    public bool RespectReduceMotion { get; init; } = true;
    public bool CheckForUpdates { get; init; } = true;
    public string? PythonExecutable { get; init; }
    public string? ParsersDirectory { get; init; }
    public Dictionary<string, string> AiProvider { get; init; } = new();   // id, model, endpoint, …
    public Dictionary<string, string> CloudClientIds { get; init; } = new(); // provider → OAuth client id
    public List<string> EnabledPlugins { get; init; } = new();
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    /// <summary>Settings keys whose values are encrypted at rest before being written.</summary>
    private static readonly string[] SecretAiKeys = ["apiKey", "ApiKey", "api_key"];

    /// <summary>Marker prefix on values that have been DPAPI-encrypted by Cinder.</summary>
    public const string EncryptedPrefix = "enc::";

    private readonly string _path;
    public SettingsStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cinder", "settings.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
    }

    public CinderSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new CinderSettings();
        }
        try
        {
            var settings = JsonSerializer.Deserialize<CinderSettings>(File.ReadAllText(_path)) ?? new CinderSettings();
            // Decrypt any AI-provider secrets on load. We mutate the dictionary in place so the
            // rest of the app never sees the ciphertext form.
            DecryptSecrets(settings.AiProvider);
            return settings;
        }
        catch
        {
            return new CinderSettings();
        }
    }

    public void Save(CinderSettings settings)
    {
        // Round-trip through a copy so we don't mutate the caller's dictionary when we encrypt.
        var safe = settings with
        {
            AiProvider = new Dictionary<string, string>(settings.AiProvider),
        };
        EncryptSecrets(safe.AiProvider);
        File.WriteAllText(_path, JsonSerializer.Serialize(safe, Json));
    }

    public string Path => _path;

    // ============================================================ DPAPI helpers ========

    private static void EncryptSecrets(IDictionary<string, string> dict)
    {
        foreach (var key in SecretAiKeys)
        {
            if (dict.TryGetValue(key, out var plain) && !string.IsNullOrEmpty(plain) &&
                !plain.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                dict[key] = EncryptedPrefix + EncryptString(plain);
            }
        }
    }

    private static void DecryptSecrets(IDictionary<string, string> dict)
    {
        foreach (var key in SecretAiKeys)
        {
            if (dict.TryGetValue(key, out var stored) && !string.IsNullOrEmpty(stored) &&
                stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                try
                {
                    dict[key] = DecryptString(stored[EncryptedPrefix.Length..]);
                }
                catch
                {
                    // Decryption failed (different user, different machine, or corrupt blob).
                    // Surface as empty — the user will notice and re-enter.
                    dict[key] = "";
                }
            }
        }
    }

    /// <summary>
    /// Encrypt a string for at-rest storage. Uses Windows DPAPI when available — keys can only
    /// be decrypted by the same user on the same machine. On Linux / macOS we fall back to an
    /// AES-GCM scheme with the key derived from the user's home path + machine ID — that's
    /// obfuscation, not real protection, and we say so in SECURITY.md.
    /// </summary>
    private static string EncryptString(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
        {
            var enc = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }
        return Convert.ToBase64String(LinuxEncrypt(bytes));
    }

    private static string DecryptString(string cipher)
    {
        var bytes = Convert.FromBase64String(cipher);
        if (OperatingSystem.IsWindows())
        {
            var dec = ProtectedData.Unprotect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        return Encoding.UTF8.GetString(LinuxDecrypt(bytes));
    }

    private static byte[] DeriveLinuxKey()
    {
        // Cross-platform fallback: derive a 256-bit key from a stable per-user-per-machine
        // identifier. NOT real protection — anyone with read access to the user's home dir can
        // decrypt. Documented in SECURITY.md.
        var seed = (Environment.MachineName + "::" + Environment.UserName + "::cinder.v1");
        return SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    private static byte[] LinuxEncrypt(byte[] plain)
    {
        var key = DeriveLinuxKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, ct, tag);
        }
        var blob = new byte[nonce.Length + tag.Length + ct.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, blob, nonce.Length, tag.Length);
        Buffer.BlockCopy(ct, 0, blob, nonce.Length + tag.Length, ct.Length);
        CryptographicOperations.ZeroMemory(key);
        return blob;
    }

    private static byte[] LinuxDecrypt(byte[] blob)
    {
        if (blob.Length < 12 + 16) throw new CryptographicException("Ciphertext too short.");
        var key = DeriveLinuxKey();
        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(12, 16);
        var ct = blob.AsSpan(28);
        var plain = new byte[ct.Length];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Decrypt(nonce, ct, tag, plain);
        }
        CryptographicOperations.ZeroMemory(key);
        return plain;
    }
}
