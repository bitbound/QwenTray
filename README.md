# QwenTray

QwenTray is a Windows tray app that supervises a single local Qwen Code daemon. The tray icon and
the app window both drive the same process, and Settings covers the daemon command line, daemon
lifecycle controls, and optional Local Control device pairing.

## Install

Download `QwenTray-x64.exe` from the [latest release](https://github.com/bitbound/QwenTray/releases/latest),
put it anywhere, and run it. No installer and no .NET install needed.

## Code Signing

Every publish (local or in CI) Authenticode-signs the binaries that go into the
single-file bundle and the final exe, using the self-signed `CN=Bitbound`
certificate from the current user's Windows certificate store
(`build/WindowsSign.Build.targets`, with the certificate lookup in
`build/Find-SigningCert.ps1` and the bundle signing in
`build/Sign-BundleFiles.ps1`). Binaries that already carry a signature are left
alone, so the Microsoft-signed runtime assemblies keep their own signatures.
Signing needs the [Sign CLI](https://github.com/dotnet/sign) global tool:

```
dotnet tool install --global sign --prerelease
```

Skip signing with `-p:EnableSigning=false`. The certificate subject, timestamp
server, and description can be overridden with the `SignCertSubject`,
`SignTimestampUrl`, `SignDescription`, and `SignDescriptionUrl` properties.

The release workflow signs too: it imports the PFX from two repository secrets
into the runner's certificate store before publishing:

- `SIGN_CERT_PFX_BASE64`: the `CN=Bitbound` PFX (with private key) as a
  base64 string:
  `[Convert]::ToBase64String([IO.File]::ReadAllBytes('bitbound.pfx')) | Set-Clipboard`
- `SIGN_CERT_PASSWORD`: the PFX password.

Because the certificate is self-signed, fresh machines don't trust it, so Explorer
and SmartScreen still flag the signature. The app carries the certificate's public
key (`QwenTray/Assets/bitbound.cer`) and Settings has a **Trust Bitbound publisher**
button that adds it to the current user's Trusted Root store. It needs no admin
rights, though Windows always asks for a Windows Hello/UAC-style confirmation
before trusting any certificate. After that, file properties show Bitbound as the
verified publisher (SmartScreen reputation is a separate, paid-CA-only thing).
Regenerate the .cer whenever the signing cert is rotated:

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My\18FC41D0A2E8190B6BEDCF70CEF1DAF18298BFA8
[IO.File]::WriteAllBytes('QwenTray\Assets\bitbound.cer', $cert.Export('Cert'))
```
