using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QwenTray;
using Xunit;

namespace QwenTray.Tests;

public sealed class FileLoggerRegistrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task ContainerResolvesILoggerFactoryWithoutCyclicLoggerProvider()
    {
        // The resolution is the thing under test, so it runs off-thread and the token bounds it
        // in case a regression turns it back into unbounded recursion.
        var outcome = await Task.Run(() =>
        {
            using var provider = Program.BuildServices();

            var factory = provider.GetRequiredService<ILoggerFactory>();
            var logger = factory.CreateLogger("QwenTray.Tests");
            logger.LogInformation("FileLoggerRegistrationTests resolved the logging pipeline.");

            return new
            {
                Logger = logger,
                HasFileProvider = provider.GetServices<ILoggerProvider>().Any(p => p is FileLoggerProvider),
            };
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Logger);
        Assert.True(outcome.HasFileProvider);
    }

    [Fact(Timeout = 15_000)]
    public async Task LoggingAValueWithNoJsonRepresentationDoesNotThrow()
    {
        var outcome = await Task.Run(() =>
        {
            using var provider = Program.BuildServices();
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("QwenTray.Tests.Serialize");

            return Record.Exception(() => logger.LogInformation("Value={Value}", typeof(string)));
        }, TestContext.Current.CancellationToken);

        Assert.Null(outcome);
    }

    [Fact]
    public void AFailedWriteDoesNotEscapeTheLogger()
    {
        var provider = new FileLoggerProvider(() => throw new IOException("Simulated write failure."), "qwentray-test.log");
        var logger = provider.CreateLogger("QwenTray.Tests");

        Assert.Null(Record.Exception(() => logger.LogInformation("Should not escape.")));
    }
}
