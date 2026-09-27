using Microsoft.Win32;

namespace QwenTray;

public sealed class RegistryAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QwenTray";

    public event EventHandler? Changed;

    public bool IsEnabled()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return string.Equals(key?.GetValue(ValueName) as string, Quote(executable), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The running executable path could not be determined.");
            key.SetValue(ValueName, Quote(executable));
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Quote(string path) => $"\"{path}\"";
}
