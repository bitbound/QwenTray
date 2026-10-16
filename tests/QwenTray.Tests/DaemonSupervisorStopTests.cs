using System.Net;
using System.Net.Sockets;
using System.Text;
using QwenTray.Services;
using Xunit;

namespace QwenTray.Tests;

/// <summary>Answers every request with a bare 200 so a test can adopt a daemon it never spawned.</summary>
internal sealed class FakeHealthEndpoint : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();

    public FakeHealthEndpoint()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = RespondAsync(client);
        }
    }

    private static async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                _ = await stream.ReadAsync(new byte[1024]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            }
            catch (Exception)
            {
                // A probe the supervisor cancels drops the connection mid-exchange.
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();
        _shutdown.Dispose();
    }
}

// A stop is not instantaneous: it is a systemd call into the distro, and the health poll keeps ticking
// across that window. The poll used to read the cancelled probe as a health failure, publishing
// Starting while the daemon was leaving.
public sealed class DaemonSupervisorStopTests
{
    private static (DaemonSupervisor Supervisor, FakeWslDistro Wsl) CreateRunning(FakeHealthEndpoint endpoint)
    {
        // An active unit behind a port that answers is what the start path adopts, which is how a test
        // gets a Running supervisor without a daemon.
        var wsl = new FakeWslDistro { ScriptOutput = "active\nenabled\n" };
        return (new DaemonSupervisor(new Settings { Port = endpoint.Port, AutoRestart = true }, wsl, new SystemdUserService(wsl)), wsl);
    }

    [Fact]
    public async Task AStopInFlightIsPublishedAsStoppingAndNeverAsStarting()
    {
        using var endpoint = new FakeHealthEndpoint();
        var (supervisor, wsl) = CreateRunning(endpoint);
        await supervisor.StartAsync();
        Assert.Equal(DaemonState.Running, supervisor.State);

        var published = new List<DaemonState>();
        supervisor.StateChanged += (_, state) => published.Add(state);

        var gate = new TaskCompletionSource();
        wsl.ScriptGate = gate;
        var stop = supervisor.StopAsync();

        await supervisor.PollAsync();
        Assert.Equal(DaemonState.Stopping, supervisor.State);

        gate.SetResult();
        await stop;

        Assert.Equal(DaemonState.Stopped, supervisor.State);
        Assert.Equal([DaemonState.Stopping, DaemonState.Stopped], published);
    }

    [Fact]
    public async Task AStartIsIgnoredWhileTheStopIsStillRunning()
    {
        using var endpoint = new FakeHealthEndpoint();
        var (supervisor, wsl) = CreateRunning(endpoint);
        await supervisor.StartAsync();

        var gate = new TaskCompletionSource();
        wsl.ScriptGate = gate;
        var stop = supervisor.StopAsync();

        await supervisor.StartAsync();

        Assert.Equal(DaemonState.Stopping, supervisor.State);
        Assert.DoesNotContain(wsl.Scripts, script => script.Contains("systemctl --user restart", StringComparison.Ordinal));

        gate.SetResult();
        await stop;
        Assert.Equal(DaemonState.Stopped, supervisor.State);
    }

    [Fact]
    public async Task AFailedStopStillSettlesTheState()
    {
        // A missing wsl.exe fails the unit stop the way the start path's distro calls fail. The state has
        // to settle anyway, since nothing else clears Stopping and every command is disabled while it lasts.
        var wsl = new FakeWslDistro { ScriptFailure = new InvalidOperationException("wsl.exe is gone.") };
        var supervisor = new DaemonSupervisor(new Settings(), wsl, new SystemdUserService(wsl));

        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StopAsync());

        Assert.Equal(DaemonState.Stopped, supervisor.State);
        Assert.True(DaemonCommandAvailability.From(supervisor.State).CanStart);
    }
}
