using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Notifications.Worker.Tests;

/// <summary>A relay that greets, answers each command with its next scripted reply, then falls silent.</summary>
/// <remarks>
/// For the faults no real relay stages on demand: a stall at a chosen command, or a reply Mailpit's Chaos API has no
/// trigger for. Every connection replays the whole script, so a retry meets the same relay (§12.7).
/// </remarks>
internal sealed class ScriptedRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string[] _replies;
    private readonly Task _accepting;
    private int _connections;

    /// <summary>One reply per command, in order; the lines of a multi-line reply are joined by CRLF.</summary>
    public ScriptedRelay(params string[] replies)
    {
        _replies = replies;
        _listener.Start();
        _accepting = AcceptAsync(_stop.Token);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref _connections);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        List<Task> conversations = [];

        try
        {
            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                Interlocked.Increment(ref _connections);
                conversations.Add(ConverseAsync(client, _replies, ct));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        await Task.WhenAll(conversations);
    }

    private static async Task ConverseAsync(TcpClient client, string[] replies, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                await WriteAsync(stream, "220 relay.test ESMTP", ct);

                foreach (string reply in replies)
                {
                    await reader.ReadLineAsync(ct);
                    await WriteAsync(stream, reply, ct);
                }

                // Holds the connection open, unanswered, until the test is over.
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
            }
        }
    }

    private static ValueTask WriteAsync(NetworkStream stream, string reply, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\r\n"), ct);
}
