using System.IO.Pipes;
using System.Security.Principal;

namespace SazViewer.App.Tests;

/// <summary>Real named-pipe hand-off between two <see cref="SingleInstanceService"/> instances in this process.</summary>
public sealed class SingleInstanceServiceTests
{
    private static readonly SecurityIdentifier User = WindowsIdentity.GetCurrent().User!;

    [Fact]
    public void NameIsScopedToUserSessionAndDataDirectory()
    {
        var a = SingleInstanceService.BuildName("S-1-5-21-1", 1, @"C:\data");
        Assert.StartsWith("SazViewer.App-S-1-5-21-1-1-", a, StringComparison.Ordinal);
        Assert.Equal(a, SingleInstanceService.BuildName("S-1-5-21-1", 1, @"c:\DATA"));
        Assert.NotEqual(a, SingleInstanceService.BuildName("S-1-5-21-2", 1, @"C:\data"));
        Assert.NotEqual(a, SingleInstanceService.BuildName("S-1-5-21-1", 2, @"C:\data"));
        Assert.NotEqual(a, SingleInstanceService.BuildName("S-1-5-21-1", 1, @"C:\other"));
    }

    [Fact]
    public async Task SecondInstanceForwardsValidatedPathsToPrimary()
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WritePlain(temp.File("a.saz"));
        var name = UniqueName(temp);
        using var primary = await PrimaryHost.StartAsync(name);

        var role = await RunSecondaryAsync(name, [capture, capture.ToUpperInvariant(), temp.File("missing.saz"), temp.File("notes.txt")]);

        Assert.Equal(InstanceRole.Forwarded, role);
        var received = await primary.Received.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([capture], received.Accepted);
        Assert.Equal(3, received.Ignored);
    }

    [Fact]
    public async Task SecondLaunchWithoutPathsStillForwards()
    {
        using var temp = new TempDirectory();
        var name = UniqueName(temp);
        using var primary = await PrimaryHost.StartAsync(name);

        Assert.Equal(InstanceRole.Forwarded, await RunSecondaryAsync(name, []));
        Assert.Empty((await primary.Received.WaitAsync(TimeSpan.FromSeconds(10))).Accepted);
    }

    [Fact]
    public async Task PipeIsOwnedByAndRestrictedToCurrentUserAndDeniesNetwork()
    {
        using var temp = new TempDirectory();
        var name = UniqueName(temp);
        using var primary = await PrimaryHost.StartAsync(name);

        PipeSecurity security;
        await using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(5000);
            security = client.GetAccessControl();
        }

        Assert.Equal(User, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var allowed = rules.Where(rule => rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow).ToList();
        Assert.NotEmpty(allowed);
        Assert.All(allowed, rule => Assert.Equal(User, rule.IdentityReference));
        Assert.Contains(rules, rule => rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny
            && rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)));
    }

    [Fact]
    public async Task MalformedRequestIsRejectedAndServerKeepsServing()
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WritePlain(temp.File("a.saz"));
        var name = UniqueName(temp);
        using var primary = await PrimaryHost.StartAsync(name);

        // A request carrying a password property is refused as a whole.
        var bad = "{\"paths\":[\"" + capture.Replace(@"\", @"\\", StringComparison.Ordinal) + "\"],\"password\":\"x\"}";
        var payload = System.Text.Encoding.UTF8.GetBytes(bad);
        var message = new byte[SingleInstanceProtocol.HeaderLength + payload.Length];
        SingleInstanceProtocol.EncodeRequest([])[..5].CopyTo(message, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(5), payload.Length);
        payload.CopyTo(message, SingleInstanceProtocol.HeaderLength);
        Assert.Equal(SingleInstanceProtocol.Rejected, await SendRawAsync(name, message));

        // Garbage header.
        Assert.Equal(SingleInstanceProtocol.Rejected, await SendRawAsync(name, "GET / HTTP/1.1\r\n\r\n"u8.ToArray()));

        // A client that connects and hangs up without a request.
        await using (var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await silent.ConnectAsync(5000);
        }

        Assert.False(primary.Received.IsCompleted);
        Assert.Equal(InstanceRole.Forwarded, await RunSecondaryAsync(name, [capture]));
        Assert.Equal([capture], (await primary.Received.WaitAsync(TimeSpan.FromSeconds(10))).Accepted);
    }

    [Fact]
    public async Task LaunchAfterPrimaryExitsBecomesPrimary()
    {
        using var temp = new TempDirectory();
        var name = UniqueName(temp);
        (await PrimaryHost.StartAsync(name)).Dispose();

        Assert.Equal(InstanceRole.Primary, await RunSecondaryAsync(name, []));
    }

    [Fact]
    public async Task PrimaryThatExitsWithoutListeningLetsTheNextLaunchTakeOver()
    {
        using var temp = new TempDirectory();
        var name = UniqueName(temp);
        // A primary that owns the mutex but never opens its pipe (still starting, or exiting) and then releases it.
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, @"Local\" + name);
            Thread.Sleep(700);
            mutex.ReleaseMutex();
        });
        owner.Start();
        await Task.Delay(100);

        Assert.Equal(InstanceRole.Primary, await RunSecondaryAsync(name, []));
        owner.Join();
    }

    [Fact]
    public async Task CrashedPrimaryAbandonedMutexIsTakenOver()
    {
        using var temp = new TempDirectory();
        var name = UniqueName(temp);
        var owner = new Thread(() => new Mutex(initiallyOwned: true, @"Local\" + name).WaitOne());
        owner.Start();
        owner.Join();

        Assert.Equal(InstanceRole.Primary, await RunSecondaryAsync(name, []));
    }

    private static string UniqueName(TempDirectory temp) =>
        SingleInstanceService.BuildName(User.Value, Environment.ProcessId, temp.Path);

    /// <summary>Starts and disposes a service on one dedicated thread, as mutex ownership requires.</summary>
    private static Task<InstanceRole> RunSecondaryAsync(string name, IReadOnlyList<string> paths)
    {
        var result = new TaskCompletionSource<InstanceRole>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            using var service = new SingleInstanceService(name, User);
            result.SetResult(service.Start(paths));
        }).Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>A primary instance living on its own thread until disposed, like the app's UI thread.</summary>
    private sealed class PrimaryHost : IDisposable
    {
        private readonly TaskCompletionSource<ForwardedPaths> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim stop = new();
        private Thread? thread;

        public Task<ForwardedPaths> Received => received.Task;

        public static async Task<PrimaryHost> StartAsync(string name)
        {
            var host = new PrimaryHost();
            var started = new TaskCompletionSource<InstanceRole>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.thread = new Thread(() =>
            {
                using var service = new SingleInstanceService(name, User);
                service.PathsReceived += (_, paths) => host.received.TrySetResult(paths);
                started.SetResult(service.Start([]));
                host.stop.Wait();
            });
            host.thread.Start();
            Assert.Equal(InstanceRole.Primary, await started.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            return host;
        }

        public void Dispose()
        {
            stop.Set();
            thread?.Join();
            stop.Dispose();
        }
    }

    private static async Task<byte> SendRawAsync(string name, byte[] message)
    {
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        await client.WriteAsync(message);
        await client.FlushAsync();
        var response = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ReadExactlyAsync(response, timeout.Token);
        return response[0];
    }
}
