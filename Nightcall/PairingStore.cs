using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Nightcall;

public sealed record PairedPhone(string Id, string Name, string Secret);
public sealed class PairingStore
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Certificate { get; set; } = "";
    public List<PairedPhone> Phones { get; set; } = new();
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nightcall", "pairing.dat");
    public static PairingStore Load(string? path = null)
    {
        path ??= FilePath;
        string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DictationOverlay", "pairing.dat");
        if (path == FilePath && !File.Exists(path) && File.Exists(legacy))
        {
            var migrated = Load(legacy); migrated.Save(path); return migrated;
        }
        if (File.Exists(path)) return JsonSerializer.Deserialize<PairingStore>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser))
            ?? throw new InvalidDataException("Cannot read saved phone pairings.");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Nightcall", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        var store = new PairingStore { Certificate = Convert.ToBase64String(cert.Export(X509ContentType.Pfx)) };
        store.Save(path); return store;
    }
    // Windows Schannel cannot serve TLS with an ephemeral imported private key.
    public X509Certificate2 OpenCertificate() => X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(Certificate), null, X509KeyStorageFlags.DefaultKeySet);
    public void Save(string? path = null)
    {
        path ??= FilePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this), null, DataProtectionScope.CurrentUser);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
