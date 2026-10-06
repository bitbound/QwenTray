namespace QwenTray.Services;

/// <summary>
/// Lets the user trust the app's self-signed code-signing publisher certificate so Windows
/// validates signatures on shipped binaries instead of reporting an unknown publisher.
/// </summary>
public interface ICertificateTrustService
{
    bool IsPublisherTrusted();

    void TrustPublisher();
}
