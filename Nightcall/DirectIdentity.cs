using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Pkcs;
using System.Text;

namespace Nightcall;

public static class DirectIdentity
{
    public sealed class Keys : IDisposable
    {
        public X509Certificate2 Certificate { get; }
        public RSA PrivateKey { get; }
        public Keys(X509Certificate2 certificate, RSA privateKey) { Certificate = certificate; PrivateKey = privateKey; }
        public void Dispose() { PrivateKey.Dispose(); Certificate.Dispose(); }
    }
    public static Keys Open(PairingStore store)
    {
        // Parse the existing PKCS#12 container in memory. In particular, do not
        // ask Windows to import a private key into any persistent key store.
        byte[] pfx = Convert.FromBase64String(store.Certificate);
        RSA? key = null; X509Certificate2? certificate = null;
        try
        {
            var info = Pkcs12Info.Decode(pfx, out int consumed);
            if (consumed != pfx.Length || info.IntegrityMode != Pkcs12IntegrityMode.Password || !(info.VerifyMac((string?)null) || info.VerifyMac(string.Empty))) throw new CryptographicException("PC identity integrity check failed.");
            foreach (var safe in info.AuthenticatedSafe)
            {
                if (safe.ConfidentialityMode == Pkcs12ConfidentialityMode.Password) safe.Decrypt((string?)null);
                else if (safe.ConfidentialityMode != Pkcs12ConfidentialityMode.None) throw new CryptographicException("Unsupported identity protection.");
                foreach (var bag in safe.GetBags())
                {
                    if (bag is Pkcs12CertBag cert && cert.IsX509Certificate) { certificate?.Dispose(); certificate = cert.GetCertificate(); }
                    else if (bag is Pkcs12ShroudedKeyBag encrypted)
                    {
                        if (key != null) throw new CryptographicException("PC identity contains multiple private keys.");
                        key = RSA.Create(); key.ImportEncryptedPkcs8PrivateKey(ReadOnlySpan<char>.Empty, encrypted.EncryptedPkcs8PrivateKey.Span, out _);
                    }
                    else if (bag is Pkcs12KeyBag plain)
                    {
                        if (key != null) throw new CryptographicException("PC identity contains multiple private keys.");
                        key = RSA.Create(); key.ImportPkcs8PrivateKey(plain.Pkcs8PrivateKey.Span, out _);
                    }
                }
            }
            if (certificate == null || key == null) throw new CryptographicException("PC identity is incomplete.");
            using var publicKey = certificate.GetRSAPublicKey();
            if (publicKey == null || !CryptographicOperations.FixedTimeEquals(publicKey.ExportSubjectPublicKeyInfo(), key.ExportSubjectPublicKeyInfo())) throw new CryptographicException("PC identity key does not match its certificate.");
            return new Keys(certificate, key);
        }
        catch { key?.Dispose(); certificate?.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    public static byte[] Input(string session, string sdp) => Encoding.UTF8.GetBytes("nightcall-direct-v1\n" + session + "\n" + sdp);
    public static (string Certificate, string Signature) Sign(PairingStore store, string session, string sdp)
    {
        // Signing only: no Schannel import and no persistent Windows key-container write.
        using var identity = Open(store);
        return (Convert.ToBase64String(identity.Certificate.RawData), Convert.ToBase64String(identity.PrivateKey.SignData(Input(session, sdp), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }
    public static bool Verify(string pin, string session, string sdp, string publicCertificate, string signature)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(publicCertificate));
            string actual = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
            using var key = certificate.GetRSAPublicKey();
            return RelayHost.Same(pin, actual) && key != null && key.VerifyData(Input(session, sdp), Convert.FromBase64String(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return false; }
    }
}
