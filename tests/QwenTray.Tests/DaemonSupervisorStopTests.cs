using System.Net;
using System.Net.Sockets;
using System.Text;
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

// A stop is not instantaneous: killing wsl.exe only ends the client, and the distro terminate that
// follows is what takes the time. The health poll keeps ticking across that window, and it used to
// read the cancelled probe as a health failure, publishing Starting while the daemon was leaving.
public sealed class DaemonSupervisorStopTests
{
    private static (DaemonSupervisor Supervisor, FakeWslDistro Wsl) CreateRunning(FakeHealthEndpoint endpoint)
    {
        var wsl = new FakeWslDistro();
        return (new DaemonSupervisor(new Settings { Port = endpoint.Port, AutoRestart = true }, wsl), wsl);
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
        wsl.TerminateGate = gate;
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
        wsl.TerminateGate = gate;
        var stop = supervisor.StopAsync();

        await supervisor.StartAsync();

        Assert.Equal(DaemonState.Stopping, supervisor.State);
        Assert.Equal(0, wsl.StartDaemonCalls);

        gate.SetResult();
        await stop;
        Assert.Equal(DaemonState.Stopped, supervisor.State);
    }

    [Fact]
    public async Task AFailedTerminateStillSettlesTheState()
    {
        using var endpoint = new FakeHealthEndpoint();
        var wsl = new FakeWslDistro { TerminateFailure = new InvalidOperationException("wsl.exe is gone.") };
        var supervisor = new DaemonSupervisor(new Settings { Port = endpoint.Port }, wsl);
        await supervisor.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StopAsync());

        Assert.Equal(DaemonState.Stopped, supervisor.State);
        Assert.True(DaemonCommandAvailability.From(supervisor.State).CanStart);
    }
}
