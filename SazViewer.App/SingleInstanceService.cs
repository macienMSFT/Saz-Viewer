using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SazViewer.App;

internal enum InstanceRole
{
    /// <summary>This process owns the per-user mutex and listens for forwarded paths.</summary>
    Primary,

    /// <summary>The paths were handed to the running instance; this process should exit.</summary>
    Forwarded,

    /// <summary>The running instance refused the request; this process should exit.</summary>
    ForwardRejected,

    /// <summary>Coordination was impossible (for example a squatted name); run as an independent window.</summary>
    Standalone
}

/// <summary>Paths received from another launch: those that passed validation, and how many were dropped.</summary>
internal sealed record ForwardedPaths(IReadOnlyList<string> Accepted, int Ignored);

/// <summary>
/// Per-user single-instance coordination: a named mutex in the session's <c>Local\</c> namespace decides
/// the primary instance, and a named pipe whose ACL admits only the current user (network logons denied)
/// carries <see cref="SingleInstanceProtocol"/> requests to it. Names include the user SID, session ID,
/// and a hash of the data directory so isolated test runs never talk to a real instance.
/// </summary>
internal sealed class SingleInstanceService : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumAttempts = 4;

    private readonly string name;
    private readonly SecurityIdentifier user;
    private Mutex? mutex;
    private bool ownsMutex;
    private CancellationTokenSource? serverCancellation;
    private Task? serverTask;

    public SingleInstanceService(string name, SecurityIdentifier user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
        this.user = user;
    }

    /// <summary>Raised on a worker thread for each accepted request.</summary>
    public event EventHandler<ForwardedPaths>? PathsReceived;

    public string Name => name;

    public static SingleInstanceService ForCurrentUser(string dataDirectory)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current user has no SID.");
        return new SingleInstanceService(BuildName(user.Value, CurrentSessionId(), dataDirectory), user);
    }

    public static string BuildName(string userSid, int sessionId, string dataDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant()));
        return $"SazViewer.App-{userSid}-{sessionId}-{Convert.ToHexString(hash, 0, 8)}";
    }

    /// <summary>
    /// Becomes the primary instance, or forwards <paramref name="paths"/> to it. Must be called on the
    /// thread that later calls <see cref="Dispose"/>, because mutex ownership is thread-affine.
    /// Retries cover a primary that is still starting (pipe not yet listening) or is exiting (mutex about
    /// to be released).
    /// </summary>
    public InstanceRole Start(IReadOnlyList<string> paths)
    {
        var request = SingleInstanceProtocol.EncodeRequest(paths);
        try
        {
            mutex = new Mutex(initiallyOwned: false, @"Local\" + name, out _);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            return InstanceRole.Standalone;
        }

        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            if (TryAcquire(TimeSpan.Zero))
            {
                StartServer();
                return InstanceRole.Primary;
            }
            switch (TrySend(request))
            {
                case SendResult.Accepted:
                    return InstanceRole.Forwarded;
                case SendResult.Rejected:
                    return InstanceRole.ForwardRejected;
            }
            if (TryAcquire(TimeSpan.FromMilliseconds(500)))
            {
                StartServer();
                return InstanceRole.Primary;
            }
        }
        return InstanceRole.Standalone;
    }

    public void Dispose()
    {
        serverCancellation?.Cancel();
        try
        {
            serverTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        serverCancellation?.Dispose();
        if (ownsMutex)
        {
            ownsMutex = false;
            try
            {
                mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }
        mutex?.Dispose();
        mutex = null;
    }

    private bool TryAcquire(TimeSpan timeout)
    {
        try
        {
            ownsMutex = mutex!.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // A previous primary crashed; ownership transfers to this thread.
            ownsMutex = true;
        }
        return ownsMutex;
    }

    private enum SendResult
    {
        Failed,
        Accepted,
        Rejected
    }

    private SendResult TrySend(byte[] request) =>
        Task.Run(async () =>
        {
            try
            {
                // CurrentUserOnly makes the client verify that the pipe is owned by this user.
                await using var client = new NamedPipeClientStream(
                    ".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var timeout = new CancellationTokenSource(ConnectTimeout + ExchangeTimeout);
                await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, timeout.Token);
                if (NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverProcessId))
                {
                    // Lets the running instance bring its window to the foreground.
                    NativeMethods.AllowSetForegroundWindow(serverProcessId);
                }
                await client.WriteAsync(request, timeout.Token);
                await client.FlushAsync(timeout.Token);
                var response = new byte[1];
                await client.ReadExactlyAsync(response, timeout.Token);
                return response[0] == SingleInstanceProtocol.Accepted ? SendResult.Accepted : SendResult.Rejected;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException
                or UnauthorizedAccessException or EndOfStreamException)
            {
                return SendResult.Failed;
            }
        }).GetAwaiter().GetResult();

    private void StartServer()
    {
        serverCancellation = new CancellationTokenSource();
        var token = serverCancellation.Token;
        serverTask = Task.Run(() => ServeAsync(token), token);
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    name,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
                    // Non-zero buffers let the one-byte reply go out even while an early-rejected client is still writing.
                    inBufferSize: 4096,
                    outBufferSize: 256,
                    CreatePipeSecurity());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The name is held by someone else (or a previous handle is still closing); retry later.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            await using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(ExchangeTimeout);
                    var paths = await SingleInstanceProtocol.ReadRequestAsync(server, timeout.Token);
                    var response = paths is null ? SingleInstanceProtocol.Rejected : SingleInstanceProtocol.Accepted;
                    await server.WriteAsync(new[] { response }, timeout.Token);
                    if (paths is not null)
                    {
                        var accepted = paths
                            .Where(ForwardedPathValidator.IsAcceptable)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        PathsReceived?.Invoke(this, new ForwardedPaths(accepted, paths.Count - accepted.Length));
                    }
                    await WaitForClientHangUpAsync(server, timeout.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // A broken or slow client affects only its own request.
                }
            }
        }
    }

    /// <summary>
    /// Closing the server end discards unread data, so the reply is kept alive until the client has read
    /// it and disconnected (bounded by the exchange timeout). Bytes beyond a rejected request are discarded.
    /// </summary>
    private static async Task WaitForClientHangUpAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        var sink = new byte[256];
        try
        {
            while (await server.ReadAsync(sink, cancellationToken) > 0)
            {
            }
        }
        catch (IOException)
        {
        }
    }

    private PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        return security;
    }

    private static int CurrentSessionId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.SessionId;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(uint processId);
    }
}
