using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PresidioDemo.Feeds;

/// <summary>Encrypted single-listener prototype store. Replace with a native database repository for deployment.</summary>
public sealed class DemoTokenVault
{
    private sealed record Entry(string Tenant, string Pnr, string SourcePassengerId, string Token);
    private readonly string _path;
    private readonly byte[] _key;
    private readonly Dictionary<string, Entry> _entries;

    public DemoTokenVault(string docsRoot)
    {
        string directory = Path.Combine(docsRoot, ".demo-token-vault");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "mappings.enc");
        string keyPath = Path.Combine(directory, "key.bin");
        if (!File.Exists(keyPath))
        {
            using var keyFile = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            keyFile.Write(RandomNumberGenerator.GetBytes(32));
            keyFile.Flush(true);
        }
        _key = File.ReadAllBytes(keyPath);
        if (_key.Length != 32) throw new InvalidDataException("Invalid prototype vault key.");
        _entries = File.Exists(_path) ? Load() : new(StringComparer.Ordinal);
    }

    public string Resolve(string tenant, string pnr, string sourcePassengerId)
    {
        byte[] scope = JsonSerializer.SerializeToUtf8Bytes(new[] { tenant, pnr, sourcePassengerId });
        string lookup = Convert.ToHexString(HMACSHA256.HashData(_key, scope));
        if (_entries.TryGetValue(lookup, out var entry)) return entry.Token;
        string token = "PAX-TOKEN-" + Guid.NewGuid().ToString("N");
        _entries.Add(lookup, new(tenant, pnr, sourcePassengerId, token));
        try { Save(); } // Commit mapping before the protected output can be published.
        catch { _entries.Remove(lookup); throw; }
        return token;
    }

    private Dictionary<string, Entry> Load()
    {
        byte[] packed = File.ReadAllBytes(_path);
        if (packed.Length < 32 || !packed.AsSpan(0, 4).SequenceEqual("PV01"u8))
            throw new InvalidDataException("Invalid prototype vault file.");
        byte[] plain = new byte[packed.Length - 32];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(packed.AsSpan(4, 12), packed.AsSpan(32), packed.AsSpan(16, 16), plain);
        return JsonSerializer.Deserialize<Dictionary<string, Entry>>(plain)
            ?? throw new InvalidDataException("Invalid prototype vault mappings.");
    }

    private void Save()
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_entries);
        byte[] packed = new byte[32 + plain.Length];
        "PV01"u8.CopyTo(packed);
        RandomNumberGenerator.Fill(packed.AsSpan(4, 12));
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(packed.AsSpan(4, 12), plain, packed.AsSpan(32), packed.AsSpan(16, 16));
        string pending = _path + ".pending";
        using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            output.Write(packed);
            output.Flush(true);
        }
        File.Move(pending, _path, overwrite: true);
    }
}