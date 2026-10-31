using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Tsuki.App.Play;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K23: the text WebSocket server with a real client (System.Net.WebSockets.ClientWebSocket): the handshake, one text frame per broadcast to every client, the
/// latest text first for a client that connects late, a clean close, the loopback-only binding, and the output that starts and stops it with the switch.
/// </summary>
public sealed class TextWebSocketTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<ClientWebSocket> ConnectAsync(int port)
    {
        var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(Wait);
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), cts.Token);
        return client;
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket client)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(Wait);
        using var bytes = new MemoryStream();                                                  // a message may arrive in pieces that split a UTF-8 sequence
        while (true)
        {
            WebSocketReceiveResult result = await client.ReceiveAsync(buffer, cts.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            bytes.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "the condition did not come true in time");
    }

    [Fact]
    public async Task AClientGetsEachBroadcastAsOneTextFrameAndALateClientTheLatestTextFirst()
    {
        var log = new List<string>();
        using TextWebSocketServer server = TextWebSocketServer.Start(0, log.Add);
        Assert.InRange(server.Port, 1, ushort.MaxValue);
        Assert.Equal(IPAddress.Loopback, TextWebSocketServer.Address);

        server.Broadcast("before anyone");                                                 // nobody connected yet: kept as the latest
        using ClientWebSocket first = await ConnectAsync(server.Port);
        Assert.Equal(WebSocketState.Open, first.State);
        Assert.Equal("before anyone", await ReceiveTextAsync(first));                       // the late client gets the current line at once
        await WaitForAsync(() => server.ClientCount == 1);

        server.Broadcast("月姫 -A piece of blue glass moon-");
        Assert.Equal("月姫 -A piece of blue glass moon-", await ReceiveTextAsync(first));

        using ClientWebSocket second = await ConnectAsync(server.Port);
        Assert.Equal("月姫 -A piece of blue glass moon-", await ReceiveTextAsync(second));  // the latest text, once
        await WaitForAsync(() => server.ClientCount == 2);
        string longText = new string('あ', 70_000);                                         // a 64-bit length frame (over 65535 bytes of UTF-8)
        server.Broadcast(longText);
        Assert.Equal(longText, await ReceiveTextAsync(first));
        Assert.Equal(longText, await ReceiveTextAsync(second));
        string medium = new string('x', 300);                                                // a 16-bit length frame
        server.Broadcast(medium);
        Assert.Equal(medium, await ReceiveTextAsync(first));
        Assert.Equal(medium, await ReceiveTextAsync(second));
        Assert.Equal(2, server.Served);

        // a client that closes is answered and dropped; the other keeps receiving
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketState.Closed, first.State);
        await WaitForAsync(() => server.ClientCount == 1);
        server.Broadcast("still here");
        Assert.Equal("still here", await ReceiveTextAsync(second));
        Assert.Contains(log, l => l.StartsWith("websocket: client connected", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("websocket: client left", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheServerListensOnTheLoopbackOnlyAndRefusesWhatIsNotAWebSocketHandshake()
    {
        using TextWebSocketServer server = TextWebSocketServer.Start(0);
        // the socket is bound to 127.0.0.1, not to every address: a connection to another local address of this machine is refused
        using (var local = new TcpClient())
        {
            await local.ConnectAsync(IPAddress.Loopback, server.Port, TestContext.Current.CancellationToken);
            Assert.True(local.Connected);
        }

        IPAddress? other = Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (other is not null)
        {
            using var outside = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(async () => await outside.ConnectAsync(other, server.Port, TestContext.Current.CancellationToken));
        }

        // a plain HTTP request is answered with 400 and closed
        using (var plain = new TcpClient())
        {
            await plain.ConnectAsync(IPAddress.Loopback, server.Port, TestContext.Current.CancellationToken);
            NetworkStream stream = plain.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"), TestContext.Current.CancellationToken);
            var buffer = new byte[256];
            int n = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
            Assert.StartsWith("HTTP/1.1 400", Encoding.ASCII.GetString(buffer, 0, n), StringComparison.Ordinal);
        }

        Assert.Equal(0, server.ClientCount);
        Assert.Equal(0, server.Served);
    }

    [Fact]
    public void FramesAreUnmaskedWithTheFinBitAndTheRightLengthForm()
    {
        byte[] small = TextWebSocketServer.Frame(0x1, "hi"u8);
        Assert.Equal([0x81, 0x02, (byte)'h', (byte)'i'], small);
        byte[] medium = TextWebSocketServer.Frame(0x1, new byte[300]);
        Assert.Equal(0x81, medium[0]);
        Assert.Equal(126, medium[1]);
        Assert.Equal((0x01, 0x2C), (medium[2], medium[3]));
        Assert.Equal(304, medium.Length);
        byte[] large = TextWebSocketServer.Frame(0x1, new byte[70_000]);
        Assert.Equal(127, large[1]);
        Assert.Equal(70_000, (large[7] << 16) | (large[8] << 8) | large[9]);                 // 0x011170, big-endian in bytes 2..9
        Assert.Equal(10 + 70_000, large.Length);
        Assert.Equal([0x88, 0x00], TextWebSocketServer.Frame(0x8, []));
    }

    [Fact]
    public async Task TheOutputStartsTheServerWhenTheSwitchGoesOnBroadcastsAndStopsItWhenItGoesOff()
    {
        var log = new List<string>();
        bool on = false;
        int listening = -1;
        var output = new TextWebSocketOutput(() => on, () => 0, log.Add);
        output.Listening += p => listening = p;
        Assert.Equal("websocket", output.Name);
        output.Tick();
        Assert.Null(output.Server);
        Assert.Equal("off", output.State);
        Assert.False(output.Send(TextKind.Message, "nobody listens"));                       // off: nothing to send to

        on = true;
        output.Tick();
        TextWebSocketServer server = Assert.IsType<TextWebSocketServer>(output.Server);
        Assert.Equal(server.Port, listening);
        Assert.Contains($"websocket: listening on ws://127.0.0.1:{server.Port}", log);
        Assert.StartsWith($"port {server.Port} (", output.State, StringComparison.Ordinal);
        Assert.True(output.Send(TextKind.Message, "first"));
        Assert.Equal(1, output.Sent);
        using (ClientWebSocket client = await ConnectAsync(server.Port))
        {
            Assert.Equal("first", await ReceiveTextAsync(client));
            Assert.True(output.Send(TextKind.Choice, "1. a\n2. b"));
            Assert.Equal("1. a\n2. b", await ReceiveTextAsync(client));
            await WaitForAsync(() =>
            {
                output.Tick();                                                                  // the server's own lines reach the log on the game thread, at the next step
                return log.Any(l => l.StartsWith("websocket: client connected (1 connected)", StringComparison.Ordinal));
            });
            Assert.Contains("websocket: choice, 9 chars to 1 clients", log);
        }

        on = false;
        output.Tick();
        Assert.Null(output.Server);
        Assert.Contains("websocket: stopped", log);
        Assert.Equal("off", output.State);
        await Assert.ThrowsAnyAsync<WebSocketException>(async () => await ConnectAsync(server.Port));

        // a port that is taken: the failure is reported once and not retried until the switch is toggled
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        int busy = ((IPEndPoint)taken.LocalEndpoint).Port;
        (int Port, string Error)? failed = null;
        var blocked = new TextWebSocketOutput(() => true, () => busy, log.Add);
        blocked.Failed += (p, e) => failed = (p, e);
        blocked.Tick();
        Assert.Null(blocked.Server);
        Assert.Equal(busy, failed?.Port);
        Assert.Equal($"port {busy} failed", blocked.State);
        Assert.Contains(log, l => l.StartsWith($"websocket: port {busy} could not be bound", StringComparison.Ordinal));
        failed = null;
        blocked.Tick();
        Assert.Null(failed);
        blocked.Dispose();
        output.Dispose();
    }
}
