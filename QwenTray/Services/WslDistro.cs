using System.Diagnostics;
using System.Text;
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
    Task<WslCommandResult> RunScriptWithInputAsync(string distro, string user, string script, string input, CancellationToken cancellationToken);
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
    public const string SourceDistro = "Ubuntu-26.04";
    private const string UnixProgram = "/bin/bash";

    public async Task<WslCommandResult> ListDistrosAsync(CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(["--list", "--quiet"]), input: null, cancellationToken);

    public async Task<bool> DistroExistsAsync(string distro, CancellationToken cancellationToken)
    {
        var result = await ListDistrosAsync(cancellationToken);
        return result.Succeeded && ParseDistroNames(result.Output).Contains(distro, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Runs a shell script as <paramref name="user"/> inside the distro.</summary>
    public async Task<WslCommandResult> RunScriptAsync(string distro, string user, string script, CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(BuildScriptCommand(distro, user, script)), input: null, cancellationToken);

    /// <summary>Runs a shell script with text on stdin, which keeps a secret off the command line.</summary>
    public async Task<WslCommandResult> RunScriptWithInputAsync(string distro, string user, string script, string input, CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(BuildScriptCommand(distro, user, script)), input, cancellationToken);

    /// <summary>
    /// Runs a shell script as root, which is what provisioning needs. Nothing here accepts a script
    /// that originates from user input.
    /// </summary>
    public async Task<WslCommandResult> RunRootScriptAsync(string distro, string script, CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(BuildScriptCommand(distro, "root", script)), input: null, cancellationToken);

    public async Task<WslCommandResult> InstallDistroAsync(string distro, CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(["--install", SourceDistro, "--name", distro, "--no-launch", "--web-download"]), input: null, cancellationToken);

    public async Task<WslCommandResult> TerminateAsync(string distro, CancellationToken cancellationToken)
        => await ExecuteAsync(BuildArguments(["--terminate", distro]), input: null, cancellationToken);

    public IProcess StartDaemon(Settings settings, string distro, IdentityProfile profile)
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            Arguments = BuildArguments(DaemonSupervisor.BuildArguments(settings, distro, profile)),
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = OutputEncoding,
        };

        return processes.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {Executable}.");
    }

    // wsl.exe writes UTF-16LE. The decoding has to match, or any non-ASCII output becomes mojibake.
    internal static Encoding OutputEncoding { get; } = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

    /// <summary>
    /// Builds a command that runs <paramref name="script"/> through bash. The <c>-e</c> flag passes
    /// the arguments verbatim; <c>--</c> re-joins them and runs them through the default shell, which
    /// expands <c>$</c> and backticks inside the values.
    /// </summary>
    internal static IReadOnlyList<string> BuildScriptCommand(string distro, string user, string script)
        => ["-d", distro, "-u", user, "-e", UnixProgram, "-lc", script];

    internal static string BuildArguments(IReadOnlyList<string> arguments)
        => string.Join(' ', arguments.Select(QuoteArgument));

    internal static string[] ParseDistroNames(string output)
        => output
            .Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('\uFEFF'))
            .Where(line => line.Length > 0)
            .ToArray();

    /// <summary>Resolves an absolute Linux path from the output of a command such as <c>command -v</c>.</summary>
    internal static string? ParseAbsolutePath(string output)
        => output
            .Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('\uFEFF'))
            .FirstOrDefault(line => line.StartsWith('/'));

    /// <summary>Reads the first non-empty line, for example <c>1.2.3</c> from a version banner.</summary>
    internal static string? ParseVersion(string output)
        => output
            .Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('\uFEFF'))
            .FirstOrDefault(line => line.Length > 0);

    private async Task<WslCommandResult> ExecuteAsync(string arguments, string? input, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = OutputEncoding,
        };

        var process = processes.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {Executable}.");
        if (input is not null && process.StandardInput is { } writer)
        {
            await writer.WriteAsync(input.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            writer.Close();
        }

        var output = process.StandardOutput is { } reader ? await reader.ReadToEndAsync(cancellationToken) : string.Empty;
        await process.WaitForExitAsync(cancellationToken);
        return new WslCommandResult(process.ExitCode, output.Trim('\0', '\uFEFF'));
    }

    private static string QuoteArgument(string value)
        => value.Length > 0 && value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
}
