# QwenTray

QwenTray is a Windows tray app that supervises a single local Qwen Code daemon. The daemon does not
run on Windows. QwenTray creates a WSL2 Ubuntu distro, owns it, and runs the daemon inside it as a
Linux user. The tray icon and the app window drive the same process, and Settings covers the Linux
environment, identity profiles, credentials, configuration sync, the daemon, and optional Local
Control device pairing.

## Requirements

Nothing to install by hand. You need Windows with WSL available and internet access during setup.
The **Set up distro** button in Settings does the rest: it creates the distro, adds each profile's
Linux user, and installs qwen, the gh CLI, and the base packages inside it.

## Install

Download `QwenTray-x64.exe` from the [latest release](https://github.com/bitbound/QwenTray/releases/latest),
put it anywhere, and run it. No installer and no .NET install needed.

## First Run

The tray comes up but does not start a daemon until the Linux environment exists. Open Settings,
find Linux environment, and press **Set up distro**. That does the following:

1. Adds `networkingMode=mirrored` to `%USERPROFILE%\.wslconfig`, leaving any keys you already set
   alone. Mirrored networking is what lets the health check reach `127.0.0.1` inside the distro.
2. Creates the distro (named `QwenTray` by default) from Ubuntu 26.04.
3. Writes `/etc/wsl.conf` with systemd on, drive automount off, and Windows interop off, then stops
   the distro so those settings take effect. `wsl.conf` is read at boot, so the isolation is not in
   force until that stop.
4. Installs git, gh, sudo, openssh-client, curl, and ca-certificates.
5. Creates each profile's Linux user, installs qwen under that user's home, and creates its workspace
   directory.

It takes a few minutes on the first run. Afterwards, sign in to Windows again or run `wsl --shutdown`
so the networking change applies. That stops every WSL distro, not just this one.

**Check status** reports whether the distro exists and whether setup finished. **Open in Windows
Terminal** opens the distro as the active profile's Linux user.

## Identity Profiles

A profile is a Linux user in the distro. That user's home holds everything that makes it a distinct
identity: the ssh key, the GPG signing key, the gh credentials, the git config, and the Qwen Code
config. Select a different profile and press Save, and the daemon restarts as that user, so the
whole identity swaps. Adding a profile only saves the definition. Press **Set up distro** again to
create the new Linux user and install qwen into it.

The Qwen executable path on the Daemon page is a Linux path inside the distro, not a Windows one.
Setup fills it in, and each profile has its own.

Settings also manages the credentials of the selected profile:

- **SSH public key**: an ed25519 key at `~/.ssh/id_ed25519`, generated without a passphrase.
  **Copy public key** puts it on the clipboard so it can be added to GitHub.
- **GPG public key**: an ed25519 signing key with no passphrase, held in that profile's own keyring,
  so commits can be signed unattended. The armored public key exports the same way.
- **GitHub token**: stored with `gh auth login --with-token`, so the token travels over stdin and
  never lands on a command line. **Sign out** runs `gh auth logout`.

## Isolation

The daemon runs as an ordinary Linux user in the distro, with no Windows interop and no mounted
drives. Inside it:

- Windows executables cannot be launched.
- Windows drives, C: included, are not visible.
- The daemon reaches the network the way any Linux program does.

Anything the agent needs has to exist in the distro. A Windows path will not work, and a tool that
shells out to a Windows binary fails. Profiles are separated by home directory only, so they still
share CPU, memory, and network with each other.

## Configuration Sync

**Push to distro** copies entries from `%USERPROFILE%\.qwen` on Windows into the active profile's
`~/.qwen`. **Pull from distro** copies them back. The default entries are `QWEN.md`, `agents`,
`rules`, `memories`, `skills`, and `workflows`. Edit the list to change what moves.

Two entries never sync, whatever the list says. `settings.json` holds live API keys and
per-machine settings, and `projects` is keyed by absolute path. Sync moves files over the
`\\wsl.localhost` share, so the distro has to be running.

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
