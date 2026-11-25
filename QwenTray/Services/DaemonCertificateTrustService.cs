using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace QwenTray.Services;

/// <summary>
/// Makes the tray's own https calls accept a leaf the generated CA signed. The root is deliberately not
/// installed into any Windows store, so the only client that trusts it implicitly is the health check
/// that would otherwise read a healthy daemon as dead. Every other client, including the browser the
/// Web Shell opens in, has to be handed the root by the operator. A certificate the operator supplied
/// by hand, or one a real CA issued, is accepted through the system trust before this service is ever
/// asked, so those keep working unchanged.
/// </summary>
public sealed class DaemonCertificateTrustService(IWslDistro wsl, ILogger<DaemonCertificateTrustService>? logger = null)
{
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    // GeneralName is CHOICE, and the two alternatives a daemon is dialed by are tagged [6] for a DNS
    // name and [7] for an address.
    private static readonly Asn1Tag DnsNameTag = new(TagClass.ContextSpecific, 6, isConstructed: false);
    private static readonly Asn1Tag IPAddressTag = new(TagClass.ContextSpecific, 7, isConstructed: false);

    private readonly ILogger<DaemonCertificateTrustService> _logger = logger ?? NullLogger<DaemonCertificateTrustService>.Instance;
    private readonly ConcurrentDictionary<string, X509Certificate2?> _roots = new(StringComparer.Ordinal);

    /// <summary>
    /// Drops the pinned root for one profile, which is what a regeneration has to do: a root held past
    /// the certificate it was read for would reject the very daemon the tray is supervising.
    /// </summary>
    public void Invalidate(string distro, string linuxUser) => _roots.TryRemove(Key(distro, linuxUser), out _);

    /// <summary>
    /// Whether the daemon's leaf is accepted, or null when the daemon is not serving material generated
    /// under a readable root, which leaves the caller to the system trust. The chain is built to the
    /// pinned root alone rather than to anything Windows already trusts, and the name being dialed has to
    /// be on the leaf, so a certificate for some other host is rejected even when the same root signed it.
    /// </summary>
    public bool? ValidateServerCertificate(string distro, IdentityProfile profile, X509Certificate2 serverCertificate, string targetHost)
    {
        var root = ReadRoot(distro, profile);
        if (root is null)
        {
            return null;
        }

        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            chain.ChainPolicy.CustomTrustStore.Add(root);
            // The daemon may hand over the leaf without the issuer, so the builder has to be able to find
            // the root on its own. CustomRootTrust says where a chain may end; ExtraStore says what it may
            // walk through, and those are separate questions.
            chain.ChainPolicy.ExtraStore.Add(root);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));

            if (!chain.Build(serverCertificate))
            {
                _logger.LogWarning("The daemon presented a certificate that does not chain to the generated root for {User}: {Status}", profile.LinuxUser, string.Join(", ", chain.ChainStatus));
                return false;
            }

            if (!HasSubjectAlternativeName(serverCertificate, targetHost))
            {
                _logger.LogWarning("The daemon presented a certificate issued by the generated root for {User} that does not cover {Host}.", profile.LinuxUser, targetHost);
                return false;
            }

            return true;
        }
        catch (CryptographicException exception)
        {
            _logger.LogWarning(exception, "Chaining the daemon's certificate to the generated root for {User} failed.", profile.LinuxUser);
            return false;
        }
    }

    /// <summary>
    /// Whether the leaf names the address being dialed. The chain build says a root was trusted; it says
    /// nothing about who the certificate was issued to, and the tray dials an address rather than a name,
    /// so the comparison has to be made here against the subject alternative names.
    /// </summary>
    internal static bool HasSubjectAlternativeName(X509Certificate2 certificate, string host)
    {
        var extension = certificate.Extensions[SubjectAlternativeNameOid];
        if (extension is null || host.Length == 0)
        {
            return false;
        }

        try
        {
            var names = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (names.HasData)
            {
                var tag = names.PeekTag();
                if (tag.HasSameClassAndValue(DnsNameTag))
                {
                    if (string.Equals(Encoding.ASCII.GetString(names.ReadOctetString(DnsNameTag)), host, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (tag.HasSameClassAndValue(IPAddressTag))
                {
                    var address = names.ReadOctetString(IPAddressTag);
                    if (address.Length is 4 or 16
                        && (address.Length == 4 ? new IPAddress(address) : new IPAddress(address, 0)).ToString().Equals(host, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else
                {
                    // Anything else a leaf may name (email, URI, directory name) is not how the tray dials
                    // a daemon, so it is stepped over rather than read.
                    names.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException)
        {
            // A leaf whose alternative-name extension cannot be read names nothing the tray can dial, and
            // reading it is a check rather than a step that may proceed without an answer.
            return false;
        }

        return false;
    }

    /// <summary>
    /// The pinned root, read from the distro and kept for the life of the tray or until a regeneration
    /// drops it. A validation callback is synchronous, so this is the one place a script call is waited
    /// on rather than awaited, and the wait is bounded by the script's own bound. It runs during a TLS
    /// handshake, which means a daemon is already answering, so the call is not the one that boots a
    /// distro the user left stopped.
    /// </summary>
    private X509Certificate2? ReadRoot(string distro, IdentityProfile profile)
    {
        if (!IdentityProfile.IsValidLinuxUser(profile.LinuxUser))
        {
            return null;
        }

        var key = Key(distro, profile.LinuxUser);
        if (_roots.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var script = $"cat -- {WslDistro.QuotePosix(DaemonCertificateService.CaCertificatePath(profile))}";
        var result = wsl.RunScriptAsync(distro, profile.LinuxUser, script, CancellationToken.None).GetAwaiter().GetResult();

        X509Certificate2? root = null;
        if (result.Succeeded)
        {
            try
            {
                root = X509Certificate2.CreateFromPem(result.Output);
            }
            catch (CryptographicException exception)
            {
                _logger.LogWarning(exception, "The root at {Path} in {Distro} is not a readable certificate.", DaemonCertificateService.CaCertificatePath(profile), distro);
            }
        }

        _roots[key] = root;
        return root;
    }

    private static string Key(string distro, string linuxUser) => $"{distro}\n{linuxUser}";
}
