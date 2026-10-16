using System.Diagnostics;
using System.Text;
using Bitbound.SystemAbstractions.Processes;

namespace QwenTray.Services;

public sealed record WslCommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// Set when the command was abandoned rather than answered. A distro whose Linux side is wedged
    /// accepts the process and then answers nothing at all, so this is the one failure a retry cannot
    /// get past and the one worth naming apart from a plain error.
    /// </summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// Every wsl.exe interaction QwenTray performs, so callers can be exercised without a distro.
/// </summary>
public interface IWslDistro
{
    Task<bool> DistroExistsAsync(string distro, CancellationToken cancellationToken);
    Task<WslCommandResult> RunScriptAsync(string distro, string user, string script, CancellationToken cancellationToken, TimeSpan? timeout = null);
    Task<WslCommandResult> RunScriptWithInputAsync(string distro, string user, string script, string input, CancellationToken cancellationToken, TimeSpan? timeout = null);
    Task<WslCommandResult> RunRootScriptAsync(string distro, string script, CancellationToken cancellationToken, TimeSpan? timeout = null);
    Task<WslCommandResult> InstallDistroAsync(string distro, CancellationToken cancellationToken);
    Task<WslCommandResult> TerminateAsync(string distro, CancellationToken cancellationToken);

    /// <summary>Removes the distro and its entire filesystem. This cannot be undone.</summary>
    Task<WslCommandResult> UnregisterAsync(string distro, CancellationToken cancellationToken);

    /// <summary>Writes the distro's whole filesystem to a tar file. Terminate the distro first.</summary>
    Task<WslCommandResult> ExportAsync(string distro, string tarPath, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a tar written by <see cref="ExportAsync"/> as <paramref name="distro"/>. Nothing is
    /// imported unless the name is free, and a location that already exists has to be empty.
    /// </summary>
    Task<WslCommandResult> ImportAsync(string distro, string targetDirectory, string tarPath, CancellationToken cancellationToken);
}

/// <summary>
/// A wsl.exe command line, the encoding wsl.exe writes its stdout in, and how long it may take. These
/// travel together because the longest honest wait differs by invocation, and pairing them keeps a
/// call site from picking the wrong one.
/// </summary>
internal readonly record struct WslInvocation(string Arguments, Encoding Encoding, TimeSpan Timeout);

/// <summary>
/// Runs commands inside the QwenTray distro through wsl.exe. The argument builders and the output
/// parsers are static so they can be exercised without a distro present.
/// </summary>
public sealed class WslDistro : IWslDistro
{
    private readonly IProcessManager _processes;
    private readonly string _executable;

    public WslDistro(IProcessManager processes) : this(processes, Executable)
    {
    }

    /// <summary>
    /// The executable is a parameter so that a wait which never ends can be exercised against a real
    /// slow process without a distro present.
    /// </summary>
    internal WslDistro(IProcessManager processes, string executable)
    {
        _processes = processes;
        _executable = executable;
    }

    public const string Executable = "wsl.exe";
    public const string SourceDistro = "Ubuntu-26.04";

    /// <summary>The environment variable <c>qwen serve</c> reads the bearer token from.</summary>
    public const string ServerTokenVariable = "QWEN_SERVER_TOKEN";
    private const string UnixProgram = "/bin/bash";

    /// <summary>
    /// How long an ordinary wsl.exe call may run. A wedged distro accepts the process and then never
    /// answers, so an unbounded wait hangs the caller forever, which on the daemon page is a Start
    /// button that never re-enables. Measured on this box: a healthy call answers in well under a
    /// second, and the slowest of these, a daemon restart, in a couple of seconds.
    /// </summary>
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long an operation that moves a whole filesystem or installs packages may run. The session
    /// bound would abort these mid-flight, which would leave a half-imported distro or a half-installed
    /// package set behind.
    /// </summary>
    internal static readonly TimeSpan LongCommandTimeout = TimeSpan.FromMinutes(30);

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

    /// <summary>
    /// Runs a shell script as <paramref name="user"/> inside the distro. <paramref name="timeout"/> is
    /// only for a step that legitimately takes longer than <see cref="CommandTimeout"/>, such as an
    /// install that reaches the network.
    /// </summary>
    public async Task<WslCommandResult> RunScriptAsync(string distro, string user, string script, CancellationToken cancellationToken, TimeSpan? timeout = null)
        => await ExecuteAsync(ScriptInvocation(distro, user, script, timeout), input: null, cancellationToken);

    /// <summary>Runs a shell script with text on stdin, which keeps a secret off the command line.</summary>
    public async Task<WslCommandResult> RunScriptWithInputAsync(string distro, string user, string script, string input, CancellationToken cancellationToken, TimeSpan? timeout = null)
        => await ExecuteAsync(ScriptInvocation(distro, user, script, timeout), input, cancellationToken);

    /// <summary>
    /// Runs a shell script as root, which is what provisioning needs. Nothing here accepts a script
    /// that originates from user input.
    /// </summary>
    public async Task<WslCommandResult> RunRootScriptAsync(string distro, string script, CancellationToken cancellationToken, TimeSpan? timeout = null)
        => await ExecuteAsync(ScriptInvocation(distro, "root", script, timeout), input: null, cancellationToken);

    public async Task<WslCommandResult> InstallDistroAsync(string distro, CancellationToken cancellationToken)
        => await ExecuteAsync(InstallDistroInvocation(distro), input: null, cancellationToken);

    public async Task<WslCommandResult> TerminateAsync(string distro, CancellationToken cancellationToken)
        => await ExecuteAsync(TerminateInvocation(distro), input: null, cancellationToken);

    public async Task<WslCommandResult> UnregisterAsync(string distro, CancellationToken cancellationToken)
        => await ExecuteAsync(UnregisterInvocation(distro), input: null, cancellationToken);

    public async Task<WslCommandResult> ExportAsync(string distro, string tarPath, CancellationToken cancellationToken)
        => await ExecuteAsync(ExportInvocation(distro, tarPath), input: null, cancellationToken);

    public async Task<WslCommandResult> ImportAsync(string distro, string targetDirectory, string tarPath, CancellationToken cancellationToken)
        => await ExecuteAsync(ImportInvocation(distro, targetDirectory, tarPath), input: null, cancellationToken);

    /// <summary>A wsl.exe subcommand. wsl.exe writes its own output as UTF-16LE.</summary>
    internal static WslInvocation ListDistrosInvocation()
        => new(BuildArguments(["--list", "--quiet"]), WslOutputEncoding, CommandTimeout);

    internal static WslInvocation InstallDistroInvocation(string distro)
        => new(BuildArguments(["--install", SourceDistro, "--name", distro, "--no-launch", "--web-download"]), WslOutputEncoding, LongCommandTimeout);

    internal static WslInvocation TerminateInvocation(string distro)
        => new(BuildArguments(["--terminate", distro]), WslOutputEncoding, CommandTimeout);

    internal static WslInvocation UnregisterInvocation(string distro)
        => new(BuildArguments(["--unregister", distro]), WslOutputEncoding, CommandTimeout);

    internal static WslInvocation ExportInvocation(string distro, string tarPath)
        => new(BuildArguments(["--export", distro, tarPath]), WslOutputEncoding, LongCommandTimeout);

    // The version is named rather than left to the default, because a host whose default is WSL 1 would
    // otherwise register a filesystem image it cannot boot the way QwenTray needs it.
    internal static WslInvocation ImportInvocation(string distro, string targetDirectory, string tarPath)
        => new(BuildArguments(["--import", distro, targetDirectory, tarPath, "--version", "2"]), WslOutputEncoding, LongCommandTimeout);

    /// <summary>A command run inside the distro. Its stdout is the child's, so it is UTF-8.</summary>
    internal static WslInvocation ScriptInvocation(string distro, string user, string script, TimeSpan? timeout = null)
        => new(BuildArguments(BuildScriptCommand(distro, user, script)), CommandOutputEncoding, timeout ?? CommandTimeout);

    /// <summary>
    /// Builds a command that runs <paramref name="script"/> through bash. The <c>-e</c> flag passes
    /// the arguments verbatim; <c>--</c> re-joins them and runs them through the default shell, which
    /// expands <c>$</c> and backticks inside the values.
    /// </summary>
    internal static IReadOnlyList<string> BuildScriptCommand(string distro, string user, string script)
        => ["-d", distro, "-u", user, "-e", UnixProgram, "-lc", script];

    /// <summary>
    /// The daemon's command line as a shell script. Every argument is single-quoted, so nothing the
    /// settings page holds can be read as shell syntax, and stderr is folded in so one stream carries
    /// everything the daemon writes.
    /// </summary>
    internal static string BuildDaemonScript(IReadOnlyList<string> serveArguments)
        => $"exec {string.Join(' ', serveArguments.Select(QuotePosix))} 2>&1";

    internal static string QuotePosix(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Builds a script that writes <paramref name="content"/> to a path inside the distro with an explicit
    /// octal mode. Base64 keeps newlines, quotes, and pipes off the command line, and <c>-D</c> creates the
    /// parent directories.
    /// </summary>
    internal static string BuildFileWriteScript(string path, string content, string mode = "0644")
    {
        var quoted = QuotePosix(path);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        return $"install -m {QuotePosix(mode)} -D /dev/null -- {quoted}"
             + $" && printf %s {QuotePosix(encoded)} | base64 -d > {quoted}";
    }

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

    /// <summary>
    /// Runs one wsl.exe invocation and enforces its bound. Visible to tests so the bound can be proven
    /// against a real process that outlives it, which a fake cannot show.
    /// </summary>
    internal async Task<WslCommandResult> ExecuteAsync(WslInvocation invocation, string? input, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_executable)
        {
            Arguments = invocation.Arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = invocation.Encoding,
            StandardErrorEncoding = invocation.Encoding,
        };

        var process = _processes.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {_executable}.");

        // A distro whose Linux side is wedged takes the process and then answers nothing at all, so
        // every wait here is bounded. Applying the bound in one place is what keeps a call site from
        // forgetting it and leaving a Start that never returns.
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(invocation.Timeout);
        var token = bound.Token;

        // Both pipes are drained at once. Reading one to its end while the other fills its buffer
        // deadlocks, and a command that fails can put plenty on stderr before it exits.
        var outputTask = process.StandardOutput is { } outputReader ? outputReader.ReadToEndAsync(token) : Task.FromResult(string.Empty);
        var errorTask = process.StandardError is { } errorReader ? errorReader.ReadToEndAsync(token) : Task.FromResult(string.Empty);

        try
        {
            if (input is not null && process.StandardInput is { } writer)
            {
                await writer.WriteAsync(input.AsMemory(), token);
                await writer.FlushAsync(token);
                writer.Close();
            }

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Killing tears the hung wsl.exe down rather than leaving it where the next call queues
            // behind it, and the caller gets a failure it can report instead of a wait that never ends.
            process.KillAndDispose();
            return new WslCommandResult(-1, $"wsl.exe did not answer within {invocation.Timeout.TotalSeconds:0} seconds, which a wedged Linux environment explains.") { TimedOut = true };
        }

        return new WslCommandResult(process.ExitCode, CombineCommandOutput(process.ExitCode, outputTask.Result, errorTask.Result).Trim('\0', '\uFEFF'));
    }

    /// <summary>
    /// Combines a command's two streams. Success keeps stdout alone, so callers that parse the output
    /// are not thrown by a warning. Failure appends stderr, because that is where most tools explain
    /// themselves. wsl.exe writes its own errors as UTF-16LE, which leaves a NUL between every
    /// character once the stream has been read as the child's UTF-8, so NULs are dropped.
    /// </summary>
    internal static string CombineCommandOutput(int exitCode, string output, string error)
    {
        if (exitCode == 0) return output;

        var stderr = error.Replace("\0", string.Empty).Trim();
        if (stderr.Length == 0) return output;
        return string.IsNullOrWhiteSpace(output) ? stderr : output.TrimEnd() + Environment.NewLine + stderr;
    }

    private static string QuoteArgument(string value)
        => value.Length > 0 && value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
}
