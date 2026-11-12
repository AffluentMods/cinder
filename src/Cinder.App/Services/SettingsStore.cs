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

    /// <summary>SQLite hash-set database (NSRL import). Shared by the Hash sets tool and the Filesystem walk.</summary>
    public string? HashSetDatabase { get; init; }

    /// <summary>
    /// Hash every file during filesystem enumeration and look it up in <see cref="HashSetDatabase"/>,
    /// adding SHA-1 and a Known / Notable / Unknown verdict per row. Off by default — it reads
    /// every file on the volume.
    /// </summary>
    public bool HashFilesOnEnumerate { get; init; }

    /// <summary>Files larger than this are skipped by the enumeration hasher.</summary>
    public int HashSizeLimitMb { get; init; } = 64;

    /// <summary>
    /// RFC 3161 Time-Stamp Authority the Custody tool sends attestation signatures to after
    /// signing (https). Empty disables timestamping. Only a SHA-256 of the signature leaves
    /// the machine.
    /// </summary>
    public string? TimestampAuthorityUrl { get; init; }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    /// <summary>Settings keys whose values are encrypted at rest before being written.</summary>
    private static readonly string[] SecretAiKeys = [ApiKeyName, "ApiKey", "api_key"];

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

    /// <summary>The spelling every reader in the app uses for the AI provider's API key.</summary>
    public const string ApiKeyName = "apiKey";

    /// <summary>
    /// The settings on disk, or defaults when there are none — or when the file cannot be read.
    /// Callers that are about to write should use <see cref="TryLoad"/> (or just call
    /// <see cref="Save"/>, which refuses to replace a file it cannot read).
    /// </summary>
    public CinderSettings Load() => TryLoad(out var settings, out _) ? settings : new CinderSettings();

    /// <summary>
    /// Reads the settings file. Returns true with defaults when there is no file (or an empty
    /// one) — nothing has been saved yet. Returns false, with the reason, when a file exists
    /// but could not be read or parsed: in that case <paramref name="settings"/> is defaults
    /// that say nothing about what the examiner had configured, and must not be written back.
    /// </summary>
    public bool TryLoad(out CinderSettings settings, out string? error)
    {
        settings = new CinderSettings();
        error = null;
        try
        {
            if (!File.Exists(_path))
            {
                return true;
            }
            var text = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;   // a zero-length file holds no settings to lose
            }
            var loaded = JsonSerializer.Deserialize<CinderSettings>(text) ?? new CinderSettings();
            // Decrypt any AI-provider secrets on load. We mutate the dictionary in place so the
            // rest of the app never sees the ciphertext form.
            DecryptSecrets(loaded.AiProvider);
            NormalizeApiKey(loaded.AiProvider);
            settings = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Writes the settings, atomically. Throws <see cref="SettingsUnreadableException"/> —
    /// without touching the file — when a settings file exists but cannot be read: saving then
    /// would replace whatever the examiner had configured (API key, hash-set database,
    /// timestamp authority) with defaults plus one change, and they would never be told.
    /// </summary>
    public void Save(CinderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryLoad(out _, out var error))
        {
            throw new SettingsUnreadableException(_path, error ?? "unknown error");
        }

        // Round-trip through a copy so we don't mutate the caller's dictionary when we encrypt.
        var safe = settings with
        {
            AiProvider = new Dictionary<string, string>(settings.AiProvider),
        };
        NormalizeApiKey(safe.AiProvider);
        EncryptSecrets(safe.AiProvider);
        // The file holds the AI provider API key, so on Unix it is created owner-only. On
        // Windows it already sits under %LOCALAPPDATA%, which is user-scoped by ACL.
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(safe, Json), ownerOnly: true);
    }

    /// <summary>
    /// Earlier builds saved the key as "api_key" while the AI tool read "apiKey", so a saved key
    /// never loaded. Whatever spelling a file uses, the app sees (and writes) exactly one.
    /// </summary>
    private static void NormalizeApiKey(IDictionary<string, string> ai)
    {
        string? value = null;
        foreach (var key in SecretAiKeys)
        {
            if (ai.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
            {
                value ??= v;
            }
        }
        foreach (var key in SecretAiKeys)
        {
            if (key != ApiKeyName)
            {
                ai.Remove(key);
            }
        }
        if (value is not null)
        {
            ai[ApiKeyName] = value;
        }
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

/// <summary>
/// Thrown by <see cref="SettingsStore.Save"/> when a settings file exists but could not be read.
/// The file has not been changed. An <see cref="IOException"/>, so callers that already report
/// I/O failures report this one too.
/// </summary>
public sealed class SettingsUnreadableException(string path, string reason)
    : IOException($"The settings file {path} exists but could not be read ({reason}). " +
                  "It has been left as it is; fix or remove it, then save again.")
{
    public string SettingsPath { get; } = path;
}
