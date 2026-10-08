using System.Diagnostics;
using Bitbound.SystemAbstractions.Processes;

namespace QwenTray.Services;

public sealed record WslCommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Every wsl.exe interaction QwenTray performs, so callers can be exercised without a distro.
/// </summary>
public interface IWslDistro
{
    Task<bool> DistroExistsAsync(string distro, CancellationToken cancellationToken);
    Task<WslCommandResult> RunScriptAsync(string distro, string user, string script, CancellationToken cancellationToken);
    Task<WslCommandResult> RunRootScriptAsync(string distro, string script, CancellationToken cancellationToken);
    Task<WslCommandResult> InstallDistroAsync(string distro, CancellationToken cancellationToken);
    Task<WslCommandResult> TerminateAsync(string distro, CancellationToken cancellationToken);

    /// <summary>Starts the daemon as the given Linux user and returns the wsl.exe host process.</summary>
    IProcess StartDaemon(Settings settings, string distro, IdentityProfile profile);
}

/// <summary>
/// Runs commands inside the QwenTray distro through wsl.exe. The argument builders and the output
/// parsers are static so they can be exercised without a distro present.
/// </summary>
public sealed class WslDistro(IProcessManager processes) : IWslDistro
{
    public const string Executable = "wsl.exe";
    private const string UnixProgram = "/bin/bash";

    public async Task<WslCommandResult> ListDistrosAsync(CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(["--list", "--quiet"]), cancellationToken);

    public async Task<bool> DistroExistsAsync(string distro, CancellationToken cancellationToken)
    {
        var result = await ListDistrosAsync(cancellationToken);
        return result.Succeeded && ParseDistroNames(result.Output).Contains(distro, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Runs a shell script as <paramref name="user"/> inside the distro.</summary>
    public async Task<WslCommandResult> RunScriptAsync(string distro, string user, string script, CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(BuildScriptCommand(distro, user, script)), cancellationToken);

    /// <summary>
    /// Runs a shell script as root, which is what provisioning needs. Nothing here accepts a script
    /// that originates from user input.
    /// </summary>
    public async Task<WslCommandResult> RunRootScriptAsync(string distro, string script, CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(BuildScriptCommand(distro, "root", script)), cancellationToken);

    public async Task<WslCommandResult> InstallDistroAsync(string distro, CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(["--install", "Ubuntu-26.04", "--name", distro, "--no-launch", "--web-download"]), cancellationToken);

    public async Task<WslCommandResult> TerminateAsync(string distro, CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(["--terminate", distro]), cancellationToken);

    public IProcess StartDaemon(Settings settings, string distro, IdentityProfile profile)
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            Arguments = BuildArguments(DaemonSupervisor.BuildArguments(settings, distro, profile)),
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return processes.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {Executable}.");
    }

    internal static IReadOnlyList<string> BuildScriptCommand(string distro, string user, string script)
        => ["-d", distro, "-u", user, "--", UnixProgram, "-lc", script];

    internal static string BuildArguments(IReadOnlyList<string> arguments)
        => string.Join(' ', arguments.Select(QuoteArgument));

    internal static string[] ParseDistroNames(string output)
        => output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('\0', '\uFEFF'))
            .Where(line => line.Length > 0)
            .ToArray();

    /// <summary>Resolves an absolute Linux path from the output of a command such as <c>command -v</c>.</summary>
    internal static string? ParseAbsolutePath(string output)
        => output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('\0', '\uFEFF'))
            .FirstOrDefault(line => line.StartsWith('/'));

    /// <summary>Reads the first non-empty line, for example <c>1.2.3</c> from a version banner.</summary>
    internal static string? ParseVersion(string output)
        => output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('\0', '\uFEFF'))
            .FirstOrDefault(line => line.Length > 0);

    private async Task<WslCommandResult> ExecuteAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = processes.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {Executable}.");
        var output = process.StandardOutput is { } reader ? await reader.ReadToEndAsync(cancellationToken) : string.Empty;
        await process.WaitForExitAsync(cancellationToken);
        return new WslCommandResult(process.ExitCode, Decode(output));
    }

    // wsl.exe pads its output with NUL bytes, so strip whatever the reader left behind.
    private static string Decode(string output) => output.Replace("\0", string.Empty, StringComparison.Ordinal);

    private static string QuoteArgument(string value)
        => value.Length > 0 && value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
}
