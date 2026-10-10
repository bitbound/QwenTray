using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using QwenTray.Services;
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
    Applying,
    Failed,
}

public sealed partial class AboutViewModel : ViewModelBase<AboutView>
{
    private const string RepositoryUrl = "https://github.com/bitbound/QwenTray";

    private readonly IUpdateService _updates;
    private readonly ISnackbarService _snackbars;
    private readonly ILogger<AboutViewModel> _logger;

    [ObservableProperty]
    private string _appVersion = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateBusy))]
    [NotifyPropertyChangedFor(nameof(IsUpToDate))]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(HasUpdateCheckFailed))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusIconKey))]
    [NotifyPropertyChangedFor(nameof(UpdateActionToolTip))]
    private UpdateIndicatorState _updateState = UpdateIndicatorState.NotChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    private string? _availableVersion;

    public AboutViewModel(IUpdateService updates, ISnackbarService snackbars, ILogger<AboutViewModel> logger)
    {
        _updates = updates;
        _snackbars = snackbars;
        _logger = logger;
        _updates.UpdateAvailable += OnUpdateAvailable;
    }

    public string ProjectUrl => RepositoryUrl;

    public string IssuesUrl => $"{RepositoryUrl}/issues";

    /// <summary>Whether a check or an install is in flight, which disables the button.</summary>
    public bool IsUpdateBusy => UpdateState is UpdateIndicatorState.Checking or UpdateIndicatorState.Applying;

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
        UpdateIndicatorState.Applying => "Downloading...",
        UpdateIndicatorState.Failed => "Couldn't check. Click to retry",
        _ => "Check for updates",
    };

    public string UpdateStatusIconKey => UpdateState switch
    {
        UpdateIndicatorState.UpToDate => "checkmark_circle_regular",
        UpdateIndicatorState.UpdateAvailable or UpdateIndicatorState.Applying => "arrow_download_regular",
        UpdateIndicatorState.Failed => "warning_regular",
        _ => "arrow_sync_regular",
    };

    /// <summary>What pressing the button does from the current state, mirrored in its tooltip.</summary>
    public string UpdateActionToolTip => IsUpdateAvailable
        ? "Download and install this version"
        : "Check GitHub Releases for a newer version";

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

    /// <summary>
    /// The single button beside the version. It installs the update it already knows about, and
    /// otherwise checks for one.
    /// </summary>
    [RelayCommand]
    private async Task CheckOrApplyUpdateAsync()
    {
        if (UpdateState == UpdateIndicatorState.UpdateAvailable && _updates.AvailableUpdate is not null)
        {
            await ApplyUpdateAsync();
            return;
        }

        await CheckForUpdatesAsync();
    }

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
            _logger.LogWarning(ex, "Could not check for a QwenTray update.");
            UpdateState = UpdateIndicatorState.Failed;
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updates.AvailableUpdate is not { } update)
        {
            await CheckForUpdatesAsync();
            return;
        }

        UpdateState = UpdateIndicatorState.Applying;
        _snackbars.Show($"Downloading QwenTray {update.Version}…");

        try
        {
            // A successful handoff replaces this process, so control never returns here.
            await _updates.ApplyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not install the QwenTray update.");
            _snackbars.Show($"Could not install the update: {ex.Message}", SnackbarKind.Error);
            // Back to the available state so the button offers the install again.
            UpdateState = UpdateIndicatorState.UpdateAvailable;
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