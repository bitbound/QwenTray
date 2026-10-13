using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace QwenTray.Tests;

/// <summary>A test that runs only on Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "This test runs only on Windows.";
        }
    }
}
