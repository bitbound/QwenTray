using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using QwenTray.Updates;
using QwenTray.Views;

namespace QwenTray.ViewModels;

/// <summary>
/// Backs the update indicator shown beside the version on the About page.
/// </summary>
public enum UpdateIndicatorState
{
    NotChecked,
    Checking,
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed partial class AboutViewModel : ViewModelBase<AboutView>
{
    private const string RepositoryUrl = "https://github.com/bitbound/QwenTray";

    private readonly IUpdateService _updates;

    [ObservableProperty]
    private string _appVersion = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckingForUpdates))]
    [NotifyPropertyChangedFor(nameof(IsUpToDate))]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(HasUpdateCheckFailed))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusIconKey))]
    private UpdateIndicatorState _updateState = UpdateIndicatorState.NotChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    private string? _availableVersion;

    public AboutViewModel(IUpdateService updates)
    {
        _updates = updates;
        _updates.UpdateAvailable += OnUpdateAvailable;
    }

    public string ProjectUrl => RepositoryUrl;

    public string IssuesUrl => $"{RepositoryUrl}/issues";

    public bool IsCheckingForUpdates => UpdateState == UpdateIndicatorState.Checking;

    public bool IsUpToDate => UpdateState == UpdateIndicatorState.UpToDate;

    public bool IsUpdateAvailable => UpdateState == UpdateIndicatorState.UpdateAvailable;

    public bool HasUpdateCheckFailed => UpdateState == UpdateIndicatorState.Failed;

    public string UpdateStatusText => UpdateState switch
    {
        UpdateIndicatorState.Checking => "Checking...",
        UpdateIndicatorState.UpToDate => "Up to date",
        UpdateIndicatorState.UpdateAvailable when AvailableVersion is { Length: > 0 } version =>
            $"Update available ({version})",
        UpdateIndicatorState.UpdateAvailable => "Update available",
        UpdateIndicatorState.Failed => "Couldn't check. Click to retry",
        _ => "Check for updates",
    };

    public string UpdateStatusIconKey => UpdateState switch
    {
        UpdateIndicatorState.UpToDate => "checkmark_circle_regular",
        UpdateIndicatorState.UpdateAvailable => "arrow_download_regular",
        UpdateIndicatorState.Failed => "warning_regular",
        _ => "arrow_sync_regular",
    };

    public IReadOnlyList<LibraryLink> Libraries { get; } =
    [
        new("Avalonia",
            "https://github.com/AvaloniaUI/Avalonia",
            "https://github.com/AvaloniaUI/Avalonia/blob/master/license.md"),
        new("Community Toolkit MVVM",
            "https://github.com/CommunityToolkit/dotnet",
            "https://github.com/CommunityToolkit/dotnet/blob/main/License.md"),
        new("QRCoder",
            "https://github.com/Shane32/QRCoder",
            "https://github.com/Shane32/QRCoder/blob/master/LICENSE.txt"),
        new(".NET and Microsoft.Extensions",
            "https://github.com/dotnet/runtime",
            "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT"),
    ];

    protected override Task OnInitializeAsync()
    {
        AppVersion = ResolveVersion();

        // The startup check may already have found a release, so reflect it instead of checking again.
        if (_updates.AvailableUpdate is { } update)
        {
            ShowUpdate(update);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateState = UpdateIndicatorState.Checking;

        try
        {
            var result = await _updates.CheckAsync();

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Update is { } update:
                    ShowUpdate(update);
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateState = UpdateIndicatorState.UpToDate;
                    break;
                default:
                    UpdateState = UpdateIndicatorState.Failed;
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex}");
            UpdateState = UpdateIndicatorState.Failed;
        }
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

    private void OnUpdateAvailable(object? sender, UpdateInfo info)
    {
        Dispatcher.UIThread.Post(() => ShowUpdate(info));
    }

    private void ShowUpdate(UpdateInfo update)
    {
        AvailableVersion = update.Version;
        UpdateState = UpdateIndicatorState.UpdateAvailable;
    }

    private static string ResolveVersion()
    {
        var assembly = typeof(AboutViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return informational ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }
}

public sealed record LibraryLink(string Name, string SourceUrl, string LicenseUrl);