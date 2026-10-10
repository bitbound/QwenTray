using Microsoft.Extensions.Logging;

namespace QwenTray.Services;

/// <summary>
/// One developer tool the Settings page installs with a single button. The script is idempotent, so
/// pressing the button again installs the newest version over whatever is there.
/// </summary>
public sealed record DevTool(string Id, string Name, string Summary, string Script);

/// <summary>
/// Installs the common developer toolchains into the QwenTray distro as root, shared by every identity
/// profile instead of living in the per-profile homes.
/// </summary>
public sealed class DevToolInstaller(IWslDistro wsl, ILogger<DevToolInstaller> logger)
{
    public static IReadOnlyList<DevTool> Catalog { get; } =
    [
        new("dotnet", ".NET SDK 10", "The current LTS .NET SDK and its runtimes.", InstallDotNet),
        new("node", "Node.js and npm", "The current Node.js LTS line, with npm.", InstallNode),
        new("azure-cli", "Azure CLI", "The az command line for working with Azure.", InstallAzureCli),
        new("aspire", "Aspire CLI", "The aspire command line.", InstallAspire),
        new("cli-utils", "Build tools and CLI utilities", "build-essential, jq, ripgrep, and fd.", InstallCliUtils),
        new("python", "Python tooling", "python3 with venv and pip, plus the uv package manager.", InstallPython),
        new("go", "Go", "The latest Go toolchain.", InstallGo),
        new("rust", "Rust", "rustup with the stable toolchain.", InstallRust),
    ];

    public async Task<WslCommandResult> InstallAsync(string distro, DevTool tool, CancellationToken cancellationToken)
    {
        logger.LogInformation("Installing developer tool {Tool} into distro {Distro}.", tool.Id, distro);
        return await wsl.RunRootScriptAsync(distro, tool.Script, cancellationToken);
    }

    // The .NET SDK is not on the Microsoft feed for this Ubuntu release; the distro's own feed carries
    // the LTS, and apt upgrades it in place on a second run.
    private const string InstallDotNet =
        "apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y dotnet-sdk-10.0";

    // NodeSource's LTS alias writes a distro-agnostic apt source and its nodejs package bundles npm,
    // so npm is not installed separately.
    private const string InstallNode =
        "apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl gnupg "
        + "&& curl -fsSL https://deb.nodesource.com/setup_lts.x | bash - "
        + "&& DEBIAN_FRONTEND=noninteractive apt-get install -y nodejs";

    // The suite name comes from lsb_release, which Microsoft publishes for this Ubuntu release. gpg
    // needs --yes or it refuses to overwrite the key on a second run.
    private const string InstallAzureCli =
        "apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl gnupg lsb-release "
        + "&& install -m 0755 -d /etc/apt/keyrings "
        + "&& curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor --yes -o /etc/apt/keyrings/microsoft.gpg "
        + "&& chmod a+r /etc/apt/keyrings/microsoft.gpg "
        + "&& echo \"deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/repos/azure-cli/ $(lsb_release -cs) main\" > /etc/apt/sources.list.d/azure-cli.list "
        + "&& apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y azure-cli";

    // The script is self-contained, so Aspire does not wait on the .NET SDK button. It lands in
    // /usr/local/bin, which is on the default path for every profile, and rerunning replaces it.
    private const string InstallAspire =
        "curl -fsSL https://aspire.dev/install.sh | bash -s -- --install-path /usr/local/bin --skip-path";

    // Ubuntu ships the fd binary as fdfind, so the name people type is linked to it.
    private const string InstallCliUtils =
        "apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y build-essential jq ripgrep fd-find "
        + "&& ln -sf \"$(command -v fdfind)\" /usr/local/bin/fd";

    // uv's installer honors UV_INSTALL_DIR and UV_NO_MODIFY_PATH, so it lands on the shared path
    // rather than in root's home.
    private const string InstallPython =
        "apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y python3 python3-venv python3-pip "
        + "&& curl -LsSf https://astral.sh/uv/install.sh | UV_INSTALL_DIR=/usr/local/bin UV_NO_MODIFY_PATH=1 sh";

    // The tarball name is read from go.dev rather than pinned, and the old tree is removed first so a
    // reinstall cannot leave two versions side by side.
    private const string InstallGo =
        "goarch=$(dpkg --print-architecture) "
        + "&& gover=$(curl -fsSL 'https://go.dev/VERSION?m=text' | head -n1) "
        + "&& curl -fsSL \"https://go.dev/dl/${gover}.linux-${goarch}.tar.gz\" -o /tmp/go.tar.gz "
        + "&& rm -rf /usr/local/go && tar -C /usr/local -xzf /tmp/go.tar.gz && rm -f /tmp/go.tar.gz "
        + "&& ln -sf /usr/local/go/bin/go /usr/local/bin/go && ln -sf /usr/local/go/bin/gofmt /usr/local/bin/gofmt";

    // A shared rustup needs one home for every profile and the variables that point at it, which
    // /etc/profile.d supplies to a login shell.
    private const string InstallRust =
        "export RUSTUP_HOME=/usr/local/rustup CARGO_HOME=/usr/local/cargo "
        + "&& curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --no-modify-path --profile default "
        + "&& chmod -R a+w /usr/local/rustup /usr/local/cargo "
        + "&& printf 'export RUSTUP_HOME=/usr/local/rustup\\nexport CARGO_HOME=/usr/local/cargo\\nexport PATH=/usr/local/cargo/bin:$PATH\\n' > /etc/profile.d/rust.sh";
}
