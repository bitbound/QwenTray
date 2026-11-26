using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace QwenTray.Services;

/// <summary>What one run of the in-distro certificate step produced.</summary>
public sealed record DaemonCertificateResult(bool Succeeded, bool WasGenerated, string? Failure)
{
    internal static DaemonCertificateResult Unchanged() => new(true, false, null);
    internal static DaemonCertificateResult Generated() => new(true, true, null);
    internal static DaemonCertificateResult Failed(string failure) => new(false, false, failure);
}

/// <summary>
/// Provisions the daemon's TLS material inside the distro so the daemon can serve https with no reverse
/// proxy in front of it. One local CA is generated per profile under that profile's home and a leaf is
/// issued under it, so a multi-profile distro gives each identity its own material rather than sharing a
/// key. The leaf carries every name the operator dials plus the two loopback names the tray's own health
/// check needs, and it is reissued whenever that list changes.
/// </summary>
public sealed class DaemonCertificateService(IWslDistro wsl, DaemonCertificateTrustService trust, ILogger<DaemonCertificateService>? logger = null)
{
    private readonly ILogger<DaemonCertificateService> _logger = logger ?? NullLogger<DaemonCertificateService>.Instance;

    public const string CertificateRelativeDirectory = ".config/qwen/tls";
    public const string CaCertificateFileName = "daemon-ca.pem";
    public const string CaKeyFileName = "daemon-ca.key";
    public const string ServerCertificateFileName = "daemon-server.pem";
    public const string ServerKeyFileName = "daemon-server.key";
    private const string SansMarkerFileName = "daemon-server.sans";

    internal const string GeneratedMarker = "qwentray-tls-generated";
    internal const string UnchangedMarker = "qwentray-tls-unchanged";

    // A leaf's common name is built from an accepted subject alternative name, so the accepted charset
    // here is what keeps anything interpolated after -subj free of shell and openssl metacharacters.
    private static readonly Regex DnsNamePattern = new(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.Compiled);

    public static string CertificateDirectory(IdentityProfile profile) => $"{profile.HomeDirectory}/{CertificateRelativeDirectory}";

    public static string CaCertificatePath(IdentityProfile profile) => $"{CertificateDirectory(profile)}/{CaCertificateFileName}";

    public static string ServerCertificatePath(IdentityProfile profile) => $"{CertificateDirectory(profile)}/{ServerCertificateFileName}";

    public static string ServerKeyPath(IdentityProfile profile) => $"{CertificateDirectory(profile)}/{ServerKeyFileName}";

    public static bool IsValidSubjectAlternativeName(string? value)
        => value is not null && value.Length > 0
        && (DnsNamePattern.IsMatch(value) || System.Net.IPAddress.TryParse(value, out _));

    /// <summary>
    /// The names as they go on the leaf: the operator's in the order typed, then the two loopback names
    /// the health check dials. DNS names are lowercased and addresses are normalized so retyping one in a
    /// different form does not read as a change and reissue the leaf for nothing.
    /// </summary>
    public static IReadOnlyList<string> BuildSubjectAlternativeNames(IEnumerable<string> requested)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in requested)
        {
            var value = raw.Trim();
            if (value.Length == 0) continue;
            value = System.Net.IPAddress.TryParse(value, out var address) ? address.ToString() : value.ToLowerInvariant();
            if (seen.Add(value)) names.Add(value);
        }

        if (seen.Add("127.0.0.1")) names.Add("127.0.0.1");
        if (seen.Add("localhost")) names.Add("localhost");
        return names;
    }

    /// <summary>The names in openssl's subjectAltName syntax, which is also the form the marker file keeps.</summary>
    internal static string FormatSubjectAlternativeNames(IReadOnlyList<string> names)
        => string.Join(',', names.Select(name => System.Net.IPAddress.TryParse(name, out _) ? $"IP:{name}" : $"DNS:{name}"));

    /// <summary>The first DNS name, since an address makes a poor label for a human to read.</summary>
    internal static string ResolveCommonName(IReadOnlyList<string> names)
        => names.FirstOrDefault(name => !System.Net.IPAddress.TryParse(name, out _)) ?? names[0];

    /// <summary>
    /// One script that brings the leaf up to date with the wanted names. It does nothing when the
    /// existing leaf verifies under the existing CA, matches its key, and already carries the wanted
    /// list, so a save that changed nothing reissues nothing. The marker file naming the issued list is
    /// written only after the new leaf has verified, which is what stops a half-completed generation
    /// from ever presenting itself as current: the next run sees the mismatch and reissues. Everything
    /// runs as the profile's own user, so the key lands owned by that user, and the umask is the second
    /// half of the 0600 promise while the explicit chmods are the first.
    /// </summary>
    internal static string BuildEnsureCertificateScript(IdentityProfile profile, IReadOnlyList<string> names)
    {
        var directory = WslDistro.QuotePosix(CertificateDirectory(profile));
        var wanted = WslDistro.QuotePosix(FormatSubjectAlternativeNames(names));
        var commonName = WslDistro.QuotePosix(ResolveCommonName(names));
        return string.Join(" && ", [
            "set -e",
            "umask 077",
            $"d={directory}",
            $"wanted={wanted}",
            $"common={commonName}",
            "command -v openssl >/dev/null",
            "install -d -m 0700 -- \"$d\"",
            // Current means: both halves of the CA, both halves of the leaf, the marker agreeing with the
            // wanted list, the leaf verifying and not expiring imminently, and the leaf and key agreeing.
            // The leaf's -pubkey and the key's -pubout both print the same PUBLIC KEY pem, so they compare
            // directly. Feeding one back through `openssl pkey` would need -pubin for a public key, and
            // without it OpenSSL 3.5 reads stdin as a private key and refuses, which reads as not current.
            $"if [ -s \"$d/{CaCertificateFileName}\" ] && [ -s \"$d/{CaKeyFileName}\" ] && [ -s \"$d/{ServerCertificateFileName}\" ] && [ -s \"$d/{ServerKeyFileName}\" ] && [ -s \"$d/{SansMarkerFileName}\" ] && [ \"$(cat \"$d/{SansMarkerFileName}\")\" = \"$wanted\" ] && openssl x509 -in \"$d/{ServerCertificateFileName}\" -noout -checkend 86400 >/dev/null && openssl verify -CAfile \"$d/{CaCertificateFileName}\" \"$d/{ServerCertificateFileName}\" >/dev/null && [ \"$(openssl x509 -in \"$d/{ServerCertificateFileName}\" -noout -pubkey)\" = \"$(openssl pkey -in \"$d/{ServerKeyFileName}\" -pubout)\" ]; then echo {UnchangedMarker}; exit 0; fi",
            // The CA is generated once. An existing CA is kept even when the leaf is reissued, because
            // every device that already trusts this root keeps trusting it across a name change.
            $"if [ ! -s \"$d/{CaCertificateFileName}\" ] || [ ! -s \"$d/{CaKeyFileName}\" ]; then openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes -keyout \"$d/{CaKeyFileName}\" -out \"$d/{CaCertificateFileName}\" -subj \"/O=QwenTray/CN=QwenTray daemon CA\" -addext \"basicConstraints=critical,CA:TRUE\" -addext \"keyUsage=critical,keyCertSign,cRLSign\" >/dev/null 2>&1; fi",
            $"openssl req -new -newkey rsa:2048 -sha256 -nodes -keyout \"$d/{ServerKeyFileName}\" -out \"$d/{ServerCertificateFileName}.csr\" -subj \"/O=QwenTray/CN=$common\" -addext \"subjectAltName=$wanted\" -addext \"basicConstraints=critical,CA:FALSE\" -addext \"keyUsage=critical,digitalSignature,keyEncipherment\" -addext \"extendedKeyUsage=serverAuth\" >/dev/null 2>&1",
            // The leaf lands under a temporary name and is moved only after it verifies, so a generation
            // that dies halfway leaves the previous leaf in place rather than a half-written one.
            $"openssl x509 -req -in \"$d/{ServerCertificateFileName}.csr\" -CA \"$d/{CaCertificateFileName}\" -CAkey \"$d/{CaKeyFileName}\" -CAcreateserial -CAserial \"$d/{CaKeyFileName}.srl\" -sha256 -days 825 -copy_extensions copy -out \"$d/{ServerCertificateFileName}.new\" >/dev/null 2>&1",
            $"openssl x509 -in \"$d/{ServerCertificateFileName}.new\" -noout -checkend 86400 >/dev/null",
            $"openssl verify -CAfile \"$d/{CaCertificateFileName}\" \"$d/{ServerCertificateFileName}.new\" >/dev/null",
            $"mv -f -- \"$d/{ServerCertificateFileName}.new\" \"$d/{ServerCertificateFileName}\"",
            $"chmod 600 -- \"$d/{CaKeyFileName}\" \"$d/{ServerKeyFileName}\"",
            $"chmod 644 -- \"$d/{CaCertificateFileName}\" \"$d/{ServerCertificateFileName}\"",
            $"rm -f -- \"$d/{ServerCertificateFileName}.csr\" \"$d/{CaKeyFileName}.srl\"",
            // Last, and only on a verified leaf. A run that dies before this line leaves the marker
            // disagreeing, and the next run reissues instead of trusting a half-written certificate.
            $"printf %s \"$wanted\" > \"$d/{SansMarkerFileName}\"",
            $"chmod 600 -- \"$d/{SansMarkerFileName}\"",
            $"echo {GeneratedMarker}",
        ]);
    }

    /// <summary>
    /// Brings the profile's certificate material up to date, generating the CA the first time and the
    /// leaf whenever the name list differs from the one already issued. A failure keeps its output so
    /// the caller can say why, which matters because the alternative is a systemd unit that refuses to
    /// boot the daemon for a reason nobody can see.
    /// </summary>
    public async Task<DaemonCertificateResult> EnsureAsync(string distro, IdentityProfile profile, IEnumerable<string> requestedNames, CancellationToken cancellationToken)
    {
        if (!IdentityProfile.IsValidLinuxUser(profile.LinuxUser) || !IdentityProfile.IsSafeAbsolutePath(profile.HomeDirectory))
        {
            return DaemonCertificateResult.Failed("The profile's Linux user or home directory holds characters that are not allowed.");
        }

        var names = BuildSubjectAlternativeNames(requestedNames);
        foreach (var name in names)
        {
            if (!IsValidSubjectAlternativeName(name))
            {
                return DaemonCertificateResult.Failed($"\"{name}\" is neither a DNS name nor an IP address.");
            }
        }

        if (!await wsl.DistroExistsAsync(distro, cancellationToken))
        {
            return DaemonCertificateResult.Failed($"The \"{distro}\" distro does not exist, so there is nowhere to generate the certificate.");
        }

        var result = await wsl.RunScriptAsync(distro, profile.LinuxUser, BuildEnsureCertificateScript(profile, names), cancellationToken);
        if (!result.Succeeded)
        {
            _logger.LogError("The daemon certificate step failed in {Distro} for {User}. {Output}", distro, profile.LinuxUser, result.Output);
            return DaemonCertificateResult.Failed(result.Output);
        }

        // Whatever the answer was, the material may have changed, and a pinned root that outlives the
        // certificate it was read for would reject a healthy daemon.
        trust.Invalidate(distro, profile.LinuxUser);

        var generated = result.Output.Contains(GeneratedMarker, StringComparison.Ordinal);
        _logger.LogInformation("Daemon certificate material for {User} in {Distro} is {State}.", profile.LinuxUser, distro, generated ? "newly issued" : "already current");
        return generated ? DaemonCertificateResult.Generated() : DaemonCertificateResult.Unchanged();
    }
}
