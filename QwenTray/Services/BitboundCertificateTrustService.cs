using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace QwenTray.Services;

/// <summary>
/// Ships the public half of the Bitbound code-signing certificate as an embedded resource and
/// installs it into the per-user Trusted Root store. The per-user store needs no elevation,
/// which fits a self-installed single-file app.
/// </summary>
public sealed class BitboundCertificateTrustService(ILogger<BitboundCertificateTrustService> logger) : ICertificateTrustService
{
    private const string CertificateResourceName = "QwenTray.Assets.bitbound.cer";

    private static readonly Lazy<X509Certificate2> PublisherCertificateLazy = new(() =>
    {
        using var stream = typeof(BitboundCertificateTrustService).Assembly
            .GetManifestResourceStream(CertificateResourceName)
            ?? throw new InvalidOperationException($"Embedded certificate '{CertificateResourceName}' is missing.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        // The exported resource holds only the public key, so no key-set flags apply here.
        return X509CertificateLoader.LoadCertificate(buffer.ToArray());
    });

    internal static X509Certificate2 PublisherCertificate => PublisherCertificateLazy.Value;

    public bool IsPublisherTrusted()
    {
        return ContainsInRootStore(StoreLocation.CurrentUser) || ContainsInRootStore(StoreLocation.LocalMachine);
    }

    public void TrustPublisher()
    {
        // One store means one Windows consent prompt, and Root alone makes Authenticode report
        // Valid (the cert is its own chain); TrustedPublisher would only add a second prompt.
        AddToUserStore(StoreName.Root);

        logger.LogInformation(
            "Trusted the Bitbound publisher certificate ({Thumbprint}) for the current user.",
            PublisherCertificate.Thumbprint);
    }

    private static bool ContainsInRootStore(StoreLocation location)
    {
        using var store = new X509Store(StoreName.Root, location);
        store.Open(OpenFlags.ReadOnly);

        return store.Certificates
            .Find(X509FindType.FindByThumbprint, PublisherCertificate.Thumbprint, validOnly: false)
            .Count > 0;
    }

    private static void AddToUserStore(StoreName storeName)
    {
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Find(X509FindType.FindByThumbprint, PublisherCertificate.Thumbprint, validOnly: false);

        if (existing.Count == 0)
        {
            store.Add(PublisherCertificate);
        }
    }
}
