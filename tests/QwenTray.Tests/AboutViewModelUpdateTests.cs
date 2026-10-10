using Microsoft.Extensions.Logging.Abstractions;
using QwenTray.Services;
using QwenTray.Updates;
using QwenTray.ViewModels;
using Xunit;

namespace QwenTray.Tests;

public sealed class AboutViewModelUpdateTests
{
    private static readonly UpdateInfo Available =
        new("v2026.10.10.0238", "QwenTray-x64.exe", "https://example/QwenTray-x64.exe", null);

    private static AboutViewModel Create(FakeUpdateService updates) =>
        new(updates, new SnackbarService(), NullLogger<AboutViewModel>.Instance);

    [Fact]
    public async Task WhenAnUpdateIsAvailableTheButtonAppliesItInsteadOfCheckingAgain()
    {
        var updates = new FakeUpdateService { AvailableUpdate = Available };
        var viewModel = Create(updates);
        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Equal("Download and install this version", viewModel.UpdateActionToolTip);

        await viewModel.CheckOrApplyUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, updates.ApplyCount);
        Assert.Equal(0, updates.CheckCount);
    }

    [Fact]
    public async Task WithNoUpdateAvailableTheButtonChecks()
    {
        var updates = new FakeUpdateService { CheckResult = UpdateCheckResult.UpToDate };
        var viewModel = Create(updates);
        await viewModel.InitializeAsync();

        Assert.Equal("Check GitHub Releases for a newer version", viewModel.UpdateActionToolTip);

        await viewModel.CheckOrApplyUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, updates.CheckCount);
        Assert.Equal(0, updates.ApplyCount);
    }

    [Fact]
    public async Task AFailedApplyReturnsToTheAvailableStateSoItCanBeRetried()
    {
        var updates = new FakeUpdateService { AvailableUpdate = Available, ApplyException = new InvalidOperationException("network") };
        var viewModel = Create(updates);
        await viewModel.InitializeAsync();

        await viewModel.CheckOrApplyUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, updates.ApplyCount);
        Assert.True(viewModel.IsUpdateAvailable);
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public UpdateInfo? AvailableUpdate { get; set; }

        public UpdateCheckResult CheckResult { get; set; } = UpdateCheckResult.UpToDate;

        public Exception? ApplyException { get; set; }

        public int CheckCount { get; private set; }

        public int ApplyCount { get; private set; }

        public event EventHandler<UpdateInfo>? UpdateAvailable;

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCount++;
            AvailableUpdate = CheckResult.Update;
            if (CheckResult.Update is { } update)
            {
                UpdateAvailable?.Invoke(this, update);
            }

            return Task.FromResult(CheckResult);
        }

        public Task ApplyAsync(CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            if (ApplyException is not null)
            {
                throw ApplyException;
            }

            return Task.CompletedTask;
        }
    }
}
