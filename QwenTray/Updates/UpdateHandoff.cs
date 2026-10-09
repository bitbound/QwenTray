using System.Globalization;

namespace QwenTray.Updates;

/// <summary>
/// The command line a downloaded update is launched with so it can replace the process that started it.
/// </summary>
public sealed record UpdateHandoff(string OutdatedExecutablePath, int OutdatedProcessId)
{
    public const string TargetArgument = "--qwentray-update-target";

    public const string ProcessIdArgument = "--qwentray-update-pid";

    public static string[] BuildArguments(UpdateHandoff handoff) =>
        [TargetArgument, handoff.OutdatedExecutablePath, ProcessIdArgument, handoff.OutdatedProcessId.ToString(CultureInfo.InvariantCulture)];

    public static UpdateHandoff? TryParse(string[] args)
    {
        var target = GetValue(args, TargetArgument);
        var rawProcessId = GetValue(args, ProcessIdArgument);

        if (string.IsNullOrWhiteSpace(target)
            || !int.TryParse(rawProcessId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId))
        {
            return null;
        }

        return new UpdateHandoff(target, processId);
    }

    /// <summary>Removes the handoff arguments so a relaunched binary starts normally.</summary>
    public static string[] Strip(string[] args)
    {
        if (args.Length == 0)
        {
            return [];
        }

        var remaining = new List<string>(args.Length);

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            if (string.Equals(arg, TargetArgument, StringComparison.Ordinal)
                || string.Equals(arg, ProcessIdArgument, StringComparison.Ordinal))
            {
                // Skip the flag and its value.
                index++;
                continue;
            }

            remaining.Add(arg);
        }

        return [.. remaining];
    }

    private static string? GetValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
