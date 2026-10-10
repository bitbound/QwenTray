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

    /// <summary>Removes the distro and its entire filesystem. This cannot be undone.</summary>
    Task<WslCommandResult> UnregisterAsync(string distro, CancellationToken cancellationToken);

    /// <summary>Starts the daemon as the given Linux user and returns the wsl.exe host process.</summary>
    IProcess StartDaemon(Settings settings, string distro, IdentityProfile profile);
}

/// <summary>
/// A wsl.exe command line and the encoding wsl.exe writes its stdout in. The two travel together
/// because the encoding differs by invocation, and pairing them keeps a call site from picking the
/// wrong one.
/// </summary>
internal readonly record struct WslInvocation(string Arguments, Encoding Encoding);

/// <summary>
/// Runs commands inside the QwenTray distro through wsl.exe. The argument builders and the output
/// parsers are static so they can be exercised without a distro present.
/// </summary>
public sealed class WslDistro(IProcessManager processes) : IWslDistro
{
    public const string Executable = "wsl.exe";
    public const string SourceDistro = "Ubuntu-26.04";

    /// <summary>The environment variable <c>qwen serve</c> reads the bearer token from.</summary>
    public const string ServerTokenVariable = "QWEN_SERVER_TOKEN";
    private const string UnixProgram = "/bin/bash";

    // wsl.exe encodes its own messages as UTF-16LE but passes a child command's stdout through as
    // raw UTF-8. Measured on this box: `wsl --list --quiet` emits 55 00 62 00, while
    // `wsl -e /bin/bash -lc "echo ready"` emits 72 65 61 64 79 0A. Decoding a command's output as
    // UTF-16LE turns "ready" into three garbage code units, which silently breaks every caller that
    // reads it.
    internal static readonly Encoding WslOutputEncoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
    internal static readonly Encoding CommandOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<WslCommandResult> ListDistrosAsync(CancellationToken cancellationToken)
        => await ExecuteAsync(ListDistrosInvocation(), input: null, cancellationToken);

    public async Task<bool> DistroExistsAsync(string distro, CancellationToken cancellationToken)
    {
        var result = await ListDistrosAsync(cancellationToken);
        return result.Succeeded && ParseDistroNames(result.Output).Contains(distro, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Runs a shell script as <paramref name="user"/> inside the distro.</summary>
    public async Task<WslCommandResult> RunScriptAsync(string distro, string user, string script, CancellationToken cancellationToken)
        => await ExecuteAsync(ScriptInvocation(distro, user, script), input: null, cancellationToken);

    /// <summary>Runs a shell script with text on stdin, which keeps a secret off the command line.</summary>
    public async Task<WslCommandResult> RunScriptWithInputAsync(string distro, string user, string script, string input, CancellationToken cancellationToken)
        => await ExecuteAsync(ScriptInvocation(distro, user, script), input, cancellationToken);

    /// <summary>
    /// Runs a shell script as root, which is what provisioning needs. Nothing here accepts a script
    /// that originates from user input.
    /// </summary>
    public async Task<WslCommandResult> RunRootScriptAsync(string distro, string script, CancellationToken cancellationToken)
        => await ExecuteAsync(ScriptInvocation(distro, "root", script), input: null, cancellationToken);

    public async Task<WslCommandResult> InstallDistroAsync(string distro, CancellationToken cancellationToken)
        => await ExecuteAsync(InstallDistroInvocation(distro), input: null, cancellationToken);

    public async Task<WslCommandResult> TerminateAsync(string distro, CancellationToken cancellationToken)
        => await ExecuteAsync(TerminateInvocation(distro), input: null, cancellationToken);

    public async Task<WslCommandResult> UnregisterAsync(string distro, CancellationToken cancellationToken)
        => await ExecuteAsync(UnregisterInvocation(distro), input: null, cancellationToken);

    public IProcess StartDaemon(Settings settings, string distro, IdentityProfile profile)
    {
        var invocation = DaemonInvocation(settings, distro, profile);
        var startInfo = new ProcessStartInfo(Executable)
        {
            Arguments = invocation.Arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = invocation.Encoding,
        };

        ApplyServeToken(startInfo, settings.Token);

        return processes.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {Executable}.");
    }

    /// <summary>
    /// Hands the bearer token to the daemon through the environment instead of an argument, so it stays
    /// off the wsl.exe command line. WSLENV is what forwards a Windows variable into the distro.
    /// </summary>
    internal static void ApplyServeToken(ProcessStartInfo startInfo, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        startInfo.Environment[ServerTokenVariable] = token.Trim();
        var existing = startInfo.Environment.TryGetValue("WSLENV", out var wslenv) ? wslenv : null;
        startInfo.Environment["WSLENV"] = MergeWslenv(existing, ServerTokenVariable);
    }

    private static string MergeWslenv(string? existing, string name)
    {
        if (string.IsNullOrEmpty(existing))
        {
            return name;
        }

        return existing.Split(':').Contains(name, StringComparer.Ordinal) ? existing : existing + ":" + name;
    }

    /// <summary>A wsl.exe subcommand. wsl.exe writes its own output as UTF-16LE.</summary>
    internal static WslInvocation ListDistrosInvocation()
        => new(BuildArguments(["--list", "--quiet"]), WslOutputEncoding);

    internal static WslInvocation InstallDistroInvocation(string distro)
        => new(BuildArguments(["--install", SourceDistro, "--name", distro, "--no-launch", "--web-download"]), WslOutputEncoding);

    internal static WslInvocation TerminateInvocation(string distro)
        => new(BuildArguments(["--terminate", distro]), WslOutputEncoding);

    internal static WslInvocation UnregisterInvocation(string distro)
        => new(BuildArguments(["--unregister", distro]), WslOutputEncoding);

    /// <summary>A command run inside the distro. Its stdout is the child's, so it is UTF-8.</summary>
    internal static WslInvocation ScriptInvocation(string distro, string user, string script)
        => new(BuildArguments(BuildScriptCommand(distro, user, CaptureErrorOutput(script))), CommandOutputEncoding);

    /// <summary>
    /// Wraps a script so a failing command's stderr is appended to its output. The process abstraction
    /// only surfaces stdout, and most tools explain a failure on stderr, so without this a failed
    /// command reports an empty reason. Stderr is dropped when the command succeeds, so callers that
    /// parse the output are not thrown by a warning. The subshell keeps a script's own exit from
    /// skipping the capture.
    /// </summary>
    internal static string CaptureErrorOutput(string script)
        => "__qt_err=$(mktemp); "
            + "( " + script + " ) 2>\"$__qt_err\"; __qt_status=$?; "
            + "[ \"$__qt_status\" -eq 0 ] || cat \"$__qt_err\"; "
            + "rm -f \"$__qt_err\"; exit \"$__qt_status\"";

    internal static WslInvocation DaemonInvocation(Settings settings, string distro, IdentityProfile profile)
        => new(BuildArguments(BuildDaemonCommand(distro, profile.LinuxUser, DaemonSupervisor.BuildServeArguments(settings, profile))), CommandOutputEncoding);

    /// <summary>
    /// Builds a command that runs <paramref name="script"/> through bash. The <c>-e</c> flag passes
    /// the arguments verbatim; <c>--</c> re-joins them and runs them through the default shell, which
    /// expands <c>$</c> and backticks inside the values.
    /// </summary>
    internal static IReadOnlyList<string> BuildScriptCommand(string distro, string user, string script)
        => ["-d", distro, "-u", user, "-e", UnixProgram, "-lc", script];

    /// <summary>
    /// The daemon is launched through bash with its stderr folded into stdout, because the log-path
    /// banner it prints on startup goes to stderr and the process abstraction only surfaces stdout.
    /// Every argument is single-quoted, so nothing the settings page holds can be read as shell syntax.
    /// </summary>
    internal static IReadOnlyList<string> BuildDaemonCommand(string distro, string user, IReadOnlyList<string> serveArguments)
        => ["-d", distro, "-u", user, "-e", UnixProgram, "-lc", BuildDaemonScript(serveArguments)];

    internal static string BuildDaemonScript(IReadOnlyList<string> serveArguments)
        => $"exec {string.Join(' ', serveArguments.Select(QuotePosix))} 2>&1";

    internal static string QuotePosix(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

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

    private async Task<WslCommandResult> ExecuteAsync(WslInvocation invocation, string? input, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            Arguments = invocation.Arguments,
            RedirectStandardOutput = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = invocation.Encoding,
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
