using System.Diagnostics;
using System.Reflection;
using QwenTray.Views;

namespace QwenTray.ViewModels;

public sealed partial class AboutViewModel : ViewModelBase<AboutView>
{
    private const string RepositoryUrl = "https://github.com/bitbound/QwenTray";

    [ObservableProperty]
    private string _appVersion = string.Empty;

    public string ProjectUrl => RepositoryUrl;

    public string IssuesUrl => $"{RepositoryUrl}/issues";

    public IReadOnlyList<LibraryLink> Libraries { get; } =
    [
        new("Avalonia",
            "https://github.com/AvaloniaUI/Avalonia",
            "https://github.com/AvaloniaUI/Avalonia/blob/master/license.md"),
        new("Community Toolkit MVVM",
            "https://github.com/CommunityToolkit/dotnet",
            "https://github.com/CommunityToolkit/dotnet/blob/main/License.md"),
        new(".NET and Microsoft.Extensions",
            "https://github.com/dotnet/runtime",
            "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT"),
        new("Fluent UI System Icons",
            "https://github.com/microsoft/fluentui-system-icons",
            "https://github.com/microsoft/fluentui-system-icons/blob/main/LICENSE"),
    ];

    protected override Task OnInitializeAsync()
    {
        AppVersion = ResolveVersion();

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open {url}: {ex}");
        }
    }

    private static string ResolveVersion()
    {
        var assembly = typeof(AboutViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return informational ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }
}

public sealed record LibraryLink(string Name, string SourceUrl, string LicenseUrl);