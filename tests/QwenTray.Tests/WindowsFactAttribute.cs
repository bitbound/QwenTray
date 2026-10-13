using System.Runtime.InteropServices;
using Xunit;

namespace QwenTray.Tests;

/// <summary>
/// A test that can only pass on Windows, because it depends on DPAPI through <c>ProtectedData</c>. That
/// API throws <see cref="System.PlatformNotSupportedException"/> everywhere else, so running it off
/// Windows measures the platform guard rather than the code under test.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "DPAPI is Windows-only.";
        }
    }
}
