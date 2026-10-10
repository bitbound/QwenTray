using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace QwenTray.Tests;

public sealed class SettingsCipherTests
{
    [Fact]
    public void TheCipherRoundTripsAndHidesThePlaintext()
    {
        const string json = """{"token":"super-secret-token"}""";

        var encrypted = SettingsCipher.Protect(json);

        Assert.NotEqual(json, Encoding.UTF8.GetString(encrypted));
        Assert.Equal(json, SettingsCipher.Unprotect(encrypted));
    }

    [Fact]
    public void PlaintextIsRejectedSoLegacyFilesCanBeDetected()
    {
        // Load treats this exception as the signal that the file predates encryption.
        Assert.Throws<CryptographicException>(
            () => SettingsCipher.Unprotect(Encoding.UTF8.GetBytes("""{"token":"x"}""")));
    }
}
