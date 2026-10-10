using QwenTray.Services;

namespace QwenTray.ViewModels;

/// <summary>One row in the Developer tools card: a tool, what it is for, and its Install button.</summary>
public sealed class DevToolRowViewModel
{
    public DevToolRowViewModel(DevTool tool, Func<DevTool, Task> install, Func<bool> canInstall)
    {
        Tool = tool;
        InstallCommand = new AsyncRelayCommand(() => install(tool), canInstall);
    }

    public DevTool Tool { get; }

    public string Name => Tool.Name;

    public string Summary => Tool.Summary;

    public IAsyncRelayCommand InstallCommand { get; }
}
