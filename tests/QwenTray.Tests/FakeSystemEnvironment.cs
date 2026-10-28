using Bitbound.SystemAbstractions;

namespace QwenTray.Tests;

/// <summary>
/// An environment that names a folder the fake file system can hold, so a step that writes into the user's
/// profile, such as <c>.wslconfig</c>, can be exercised without touching the real one.
/// </summary>
internal sealed class FakeSystemEnvironment : ISystemEnvironment
{
    public const string ProfileDirectory = "/profile";

    public int CurrentThreadId => 1;

    public bool Is64Bit => true;

    public bool IsDebug => false;

    public SystemPlatform Platform => SystemPlatform.Windows;

    public int ProcessId => 1;

    public int ProcessorCount => 8;

    public RuntimeId Runtime => RuntimeId.Unknown;

    public string SelfExtractDir => "/extract";

    public string StartupDirectory => "/app";

    public string StartupExePath => "/app/QwenTray.exe";

    public string GetCommonApplicationDataDirectory() => "/programdata";

    public string GetProfileDirectory() => ProfileDirectory;

    public bool IsLinux() => false;

    public bool IsMacOS() => false;

    public bool IsWindows() => true;
}
