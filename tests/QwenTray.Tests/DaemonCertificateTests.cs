using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

/// <summary>
/// The daemon's https material is generated inside the distro by openssl and pinned by the tray for its
/// own health check. These pin the parts the tray itself decides: which names land on the leaf, which
/// order the in-distro script issues them in, what the save does when the leaf cannot be produced, and
/// what the pinned check accepts when it is handed real certificates.
/// </summary>
public sealed class DaemonCertificateTests
{
    private const string Distro = "QwenTray";

    private static IdentityProfile Profile() => new()
    {
        Id = "default",
        Name = "Default",
        LinuxUser = "qwen",
        WorkspaceDirectory = "/home/qwen/workspace",
    };

    [Fact]
    public void TheLoopbackNamesAreOnEveryLeafWhetherAskedForOrNot()
    {
        // The tray health check dials https on loopback with no certificate prompt to fall back on, so a
        // leaf without these reads as a dead daemon.
        var names = DaemonCertificateService.BuildSubjectAlternativeNames([]);

        Assert.Contains("127.0.0.1", names);
        Assert.Contains("localhost", names);
    }

    [Fact]
    public void TheOperatorsNamesKeepTheirOrderAndTheLoopbackNamesComeLast()
    {
        var names = DaemonCertificateService.BuildSubjectAlternativeNames(["qwen.example.lan", "192.168.0.107"]);

        Assert.Equal(["qwen.example.lan", "192.168.0.107", "127.0.0.1", "localhost"], names);
    }

    [Fact]
    public void RetypingANameInAnotherFormIsNotAChangeAndDoesNotReissueTheLeaf()
    {
        // The leaf is reissued whenever the wanted list changes, so a retyping that the certificate
        // already covers must produce the same list rather than churn a new leaf on every save.
        var names = DaemonCertificateService.BuildSubjectAlternativeNames(["Qwen.Example.Lan", "127.0.0.1", "localhost", "localhost"]);

        Assert.Equal(["qwen.example.lan", "127.0.0.1", "localhost"], names);
    }

    [Fact]
    public void AddressesBecomeIpEntriesAndNamesBecomeDnsEntries()
    {
        var formatted = DaemonCertificateService.FormatSubjectAlternativeNames(
            DaemonCertificateService.BuildSubjectAlternativeNames(["qwen.example.lan", "192.168.0.107"]));

        Assert.Equal("DNS:qwen.example.lan,IP:192.168.0.107,IP:127.0.0.1,DNS:localhost", formatted);
    }

    [Theory]
    [InlineData("qwen.example.lan", true)]
    [InlineData("localhost", true)]
    [InlineData("192.168.0.107", true)]
    [InlineData("::1", true)]
    [InlineData("bad name", false)]
    [InlineData("-leading-dash", false)]
    [InlineData("trailing-dot.", false)]
    [InlineData("", false)]
    public void OnlyPlainNamesAndAddressesMayBeDialled(string value, bool valid)
    {
        Assert.Equal(valid, DaemonCertificateService.IsValidSubjectAlternativeName(value));
    }

    [Fact]
    public void TheCommonNamePrefersANameOverAnAddress()
    {
        var names = DaemonCertificateService.BuildSubjectAlternativeNames(["192.168.0.107", "qwen.example.lan"]);

        Assert.Equal("qwen.example.lan", DaemonCertificateService.ResolveCommonName(names));
    }

    [Fact]
    public void TheScriptHoldsItsPromiseAboutModesAndRunsTheWholeThingUnderSetE()
    {
        var script = DaemonCertificateService.BuildEnsureCertificateScript(Profile(), DaemonCertificateService.BuildSubjectAlternativeNames(["192.168.0.107"]));

        Assert.StartsWith("set -e", script, StringComparison.Ordinal);
        Assert.Contains("umask 077", script, StringComparison.Ordinal);
        Assert.Contains("chmod 600 -- \"$d/daemon-ca.key\" \"$d/daemon-server.key\"", script, StringComparison.Ordinal);
        Assert.Contains("/home/qwen/.config/qwen/tls", script, StringComparison.Ordinal);
        Assert.Contains("-addext \"subjectAltName=$wanted\"", script, StringComparison.Ordinal);
        Assert.Contains("-copy_extensions copy", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkerIsWrittenOnlyAfterTheNewLeafLandedBecauseThatOrderIsTheHalfGenerationGuard()
    {
        // A run that dies between issuing the leaf and recording the names it was issued for has to look
        // out of date to the next run. The marker going last is what makes that true, and it is the whole
        // reason a half-completed generation cannot present itself as current.
        var script = DaemonCertificateService.BuildEnsureCertificateScript(Profile(), DaemonCertificateService.BuildSubjectAlternativeNames(["192.168.0.107"]));

        var marker = script.IndexOf("printf %s \"$wanted\" >", StringComparison.Ordinal);
        var moved = script.IndexOf("mv -f --", StringComparison.Ordinal);
        var generated = script.IndexOf("echo qwentray-tls-generated", StringComparison.Ordinal);
        Assert.True(moved > 0 && marker > moved, "the leaf must move into place before the marker records it");
        Assert.True(generated > marker, "the answer must come only after the marker is written");
    }

    [Fact]
    public void TheIssuedLeafIsVerifiedBeforeTheGoodOneIsReplaced()
    {
        var script = DaemonCertificateService.BuildEnsureCertificateScript(Profile(), DaemonCertificateService.BuildSubjectAlternativeNames(["192.168.0.107"]));

        var verified = script.IndexOf("openssl verify -CAfile \"$d/daemon-ca.pem\" \"$d/daemon-server.pem.new\"", StringComparison.Ordinal);
        var moved = script.IndexOf("mv -f --", StringComparison.Ordinal);
        Assert.True(verified > 0 && moved > verified, "the candidate leaf must verify before the move replaces the live one");
    }

    [Fact]
    public async Task TheMaterialIsGeneratedAsTheProfilesOwnUserSoTheKeyBelongsToThatUser()
    {
        var wsl = new FakeWslDistro { ScriptOutput = DaemonCertificateService.GeneratedMarker };
        var service = new DaemonCertificateService(wsl, new DaemonCertificateTrustService(wsl));

        var result = await service.EnsureAsync(Distro, Profile(), ["192.168.0.107"], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.WasGenerated);
        var issued = Assert.Single(wsl.UserScripts);
        Assert.Equal("qwen", issued.User);
    }

    [Fact]
    public async Task AnAlreadyCurrentLeafIsReportedRatherThanReissued()
    {
        var wsl = new FakeWslDistro { ScriptOutput = DaemonCertificateService.UnchangedMarker };
        var service = new DaemonCertificateService(wsl, new DaemonCertificateTrustService(wsl));

        var result = await service.EnsureAsync(Distro, Profile(), [], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.WasGenerated);
    }

    [Fact]
    public async Task ARefusedScriptFailsTheResultAndCarriesItsOwnExplanation()
    {
        // The distro answers with why openssl refused, and that text is what turns an inexplicable boot
        // refusal in the systemd unit into a save that says what went wrong.
        var wsl = new FakeWslDistro { ScriptExitCode = 1, ScriptOutput = "req: No such file or directory - openssl" };
        var service = new DaemonCertificateService(wsl, new DaemonCertificateTrustService(wsl));

        var result = await service.EnsureAsync(Distro, Profile(), ["192.168.0.107"], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("openssl", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsentDistroIsRefusedBeforeAnyScriptIsRun()
    {
        var wsl = new FakeWslDistro { Exists = false };
        var service = new DaemonCertificateService(wsl, new DaemonCertificateTrustService(wsl));

        var result = await service.EnsureAsync(Distro, Profile(), [], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.Failure, StringComparison.Ordinal);
        Assert.Empty(wsl.UserScripts);
    }

    [Fact]
    public async Task AnUnusableNameNeverReachesTheDistro()
    {
        var wsl = new FakeWslDistro();
        var service = new DaemonCertificateService(wsl, new DaemonCertificateTrustService(wsl));

        var result = await service.EnsureAsync(Distro, Profile(), ["two words"], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("two words", result.Failure, StringComparison.Ordinal);
        Assert.Empty(wsl.UserScripts);
    }

    [Fact]
    public void CopyingSettingsCarriesTheGeneratedCertificateMaterial()
    {
        // Import and reset write through CopyFrom, so a setting left out of it silently reverts.
        var target = new Settings();

        target.CopyFrom(new Settings { TlsAutoGenerate = true, TlsSubjectAltNames = ["192.168.0.107"] });

        Assert.True(target.TlsAutoGenerate);
        Assert.Equal(["192.168.0.107"], target.TlsSubjectAltNames);
    }

    [Fact]
    public void TheDirectServeBindAnswersTheNetworkWithATokenAndNeedsNoComplaint()
    {
        // The combination this whole feature exists for: Host 0.0.0.0, a token set, the forwarder off,
        // and the PC's LAN address on the leaf. Nothing about it is wrong, so nothing may warn about it,
        // or the operator learns to ignore the warnings that matter.
        var warnings = DaemonSupervisor.ValidateSettings(new Settings
        {
            Host = "0.0.0.0",
            Token = "a-token",
            LanProxy = false,
            TlsAutoGenerate = true,
            TlsCertPath = "/home/qwen/.config/qwen/tls/daemon-server.pem",
            TlsKeyPath = "/home/qwen/.config/qwen/tls/daemon-server.key",
            TlsSubjectAltNames = ["192.168.0.107"],
        }).ToList();

        Assert.Empty(warnings);
    }

    [Fact]
    public void ANetworkBindWithoutATokenIsRefusedBeforeTheDaemonFailsToBoot()
    {
        var warnings = DaemonSupervisor.ValidateSettings(new Settings { Host = "0.0.0.0" }).ToList();

        Assert.Contains(warnings, warning => warning.Contains("token", StringComparison.Ordinal));
    }

    [Fact]
    public void HttpsOnANetworkAddressWithNoNamesWarnsAboutTheMismatchComing()
    {
        var warnings = DaemonSupervisor.ValidateSettings(new Settings
        {
            Host = "0.0.0.0",
            Token = "a-token",
            TlsCertPath = "/home/qwen/.config/qwen/tls/daemon-server.pem",
            TlsKeyPath = "/home/qwen/.config/qwen/tls/daemon-server.key",
        }).ToList();

        Assert.Contains(warnings, warning => warning.Contains("name mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void AGeneratedLeafCarryingTheDialledAddressIsTrustedByNameAndRoot()
    {
        var (caCert, caKey) = CreateCertificateAuthority();
        var (leaf, _) = CreateLeaf(caCert, caKey, ["qwen.example.lan"], ["127.0.0.1"]);
        var wsl = new FakeWslDistro { ScriptOutput = ToPem(caCert) };
        var trust = new DaemonCertificateTrustService(wsl);

        Assert.True(trust.ValidateServerCertificate(Distro, Profile(), leaf, "127.0.0.1"));
    }

    [Fact]
    public void ALeafForAnotherAddressIsRejectedEvenWhenTheSameRootSignedIt()
    {
        // The chain build answers who signed the certificate; this answers who it was issued to. Without
        // it, one leaf under the pinned root would serve every name.
        var (caCert, caKey) = CreateCertificateAuthority();
        var (leaf, _) = CreateLeaf(caCert, caKey, [], ["10.0.0.5"]);
        var wsl = new FakeWslDistro { ScriptOutput = ToPem(caCert) };
        var trust = new DaemonCertificateTrustService(wsl);

        Assert.False(trust.ValidateServerCertificate(Distro, Profile(), leaf, "127.0.0.1"));
    }

    [Fact]
    public void ALeafThePinnedRootDidNotSignIsRejected()
    {
        var (foreignCa, foreignKey) = CreateCertificateAuthority();
        var (leaf, _) = CreateLeaf(foreignCa, foreignKey, [], ["127.0.0.1"]);
        var (ownCa, _) = CreateCertificateAuthority();
        var wsl = new FakeWslDistro { ScriptOutput = ToPem(ownCa) };
        var trust = new DaemonCertificateTrustService(wsl);

        Assert.False(trust.ValidateServerCertificate(Distro, Profile(), leaf, "127.0.0.1"));
    }

    [Fact]
    public void MaterialThatIsNotTheTraysOwnLeavesTheVerdictToTheSystemTrust()
    {
        // A hand-supplied cert path is the operator's business. The tray must not reject what Windows
        // accepts, so it answers nothing when there is no generated root to compare against.
        var (someCertificate, _) = CreateCertificateAuthority();
        var wsl = new FakeWslDistro { ScriptExitCode = 1 };
        var trust = new DaemonCertificateTrustService(wsl);

        Assert.Null(trust.ValidateServerCertificate(Distro, Profile(), someCertificate, "127.0.0.1"));
    }

    [WindowsFact]
    public void ARegenerationDropsThePinnedRootSoTheNewMaterialIsJudgedOnItsOwnMerits()
    {
        var (caCert, caKey) = CreateCertificateAuthority();
        var (leaf, _) = CreateLeaf(caCert, caKey, [], ["127.0.0.1"]);
        var wsl = new FakeWslDistro { ScriptOutput = ToPem(caCert) };
        var trust = new DaemonCertificateTrustService(wsl);

        Assert.True(trust.ValidateServerCertificate(Distro, Profile(), leaf, "127.0.0.1"));

        // The root is read once and kept, because a health poll every few seconds cannot pay for a script
        // call every handshake. A regeneration has to drop it, or the new daemon looks dead to the tray.
        Assert.True(trust.ValidateServerCertificate(Distro, Profile(), leaf, "127.0.0.1"));
        Assert.Equal(1, wsl.RunScriptCalls);

        wsl.ScriptExitCode = 1;
        trust.Invalidate(Distro, "qwen");

        Assert.Null(trust.ValidateServerCertificate(Distro, Profile(), leaf, "127.0.0.1"));
    }

    [Fact]
    public void SavingWithGenerationOnPutsTheGeneratedPathsIntoTheSettings()
    {
        var pages = new SettingsPages();
        pages.PrimeProfiles();
        pages.Server.TlsAutoGenerate = true;
        pages.Server.TlsSans = "192.168.0.107";
        pages.Wsl.ScriptOutput = DaemonCertificateService.GeneratedMarker;

        pages.Save.SaveCommand.Execute(null);

        Assert.Null(pages.Save.SaveError);
        Assert.Equal("/home/qwen/.config/qwen/tls/daemon-server.pem", pages.Settings.TlsCertPath);
        Assert.Equal("/home/qwen/.config/qwen/tls/daemon-server.key", pages.Settings.TlsKeyPath);
        Assert.Equal("192.168.0.107", Assert.Single(pages.Settings.TlsSubjectAltNames));
        Assert.True(pages.FileSystem.FileExists(SettingsStore.FilePath));
    }

    [Fact]
    public void AFailedGenerationFailsTheSaveAndWritesNothing()
    {
        // Requirement 6 in its own test: the half-completed case must be a save that says so, never a
        // systemd unit that quietly refuses to boot the daemon.
        var pages = new SettingsPages();
        pages.PrimeProfiles();
        pages.Server.TlsAutoGenerate = true;
        pages.Server.TlsSans = "192.168.0.107";
        pages.Wsl.ScriptExitCode = 1;
        pages.Wsl.ScriptOutput = "openssl req: unable to load key";

        pages.Save.SaveCommand.Execute(null);

        Assert.NotNull(pages.Save.SaveError);
        Assert.Contains("nothing was saved", pages.Save.SaveError!, StringComparison.Ordinal);
        Assert.Contains("unable to load key", pages.Save.SaveError!, StringComparison.Ordinal);
        Assert.False(pages.FileSystem.FileExists(SettingsStore.FilePath));
    }

    [Fact]
    public void AnUnusableNameIsRefusedBeforeTheDistroIsAskedForAnything()
    {
        var pages = new SettingsPages();
        pages.PrimeProfiles();
        pages.Server.TlsAutoGenerate = true;
        pages.Server.TlsSans = "not a name!";
        var callsBefore = pages.Wsl.RunScriptCalls;

        pages.Save.SaveCommand.Execute(null);

        Assert.Contains("not a name!", pages.Save.SaveError, StringComparison.Ordinal);
        Assert.Equal(callsBefore, pages.Wsl.RunScriptCalls);
    }

    private static (X509Certificate2 Certificate, RSA Key) CreateCertificateAuthority()
    {
        var key = RSA.Create(3072);
        var request = new CertificateRequest("O=QwenTray, CN=QwenTray daemon CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        return (certificate, key);
    }

    private static (X509Certificate2 Certificate, RSA Key) CreateLeaf(X509Certificate2 caCertificate, RSA caKey, string[] dnsNames, string[] addresses)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest("O=QwenTray, CN=daemon", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(BuildSubjectAlternativeNames(dnsNames, addresses));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        var certificate = request.Create(caCertificate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), NextSerial());
        return (certificate, key);
    }

    private static X509Extension BuildSubjectAlternativeNames(string[] dnsNames, string[] addresses)
    {
        var builder = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames) builder.AddDnsName(name);
        foreach (var address in addresses) builder.AddIpAddress(IPAddress.Parse(address));
        return builder.Build();
    }

    private static byte[] NextSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        return serial;
    }

    private static string ToPem(X509Certificate2 certificate)
        => "-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(certificate.RawData, Base64FormattingOptions.InsertLineBreaks) + "\n-----END CERTIFICATE-----\n";
}
