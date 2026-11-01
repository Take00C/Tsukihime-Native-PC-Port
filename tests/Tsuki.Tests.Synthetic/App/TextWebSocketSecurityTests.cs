using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Tsuki.App.Play;
using Tsuki.App.Services;

namespace Tsuki.Tests.Synthetic.App;

public sealed class TextWebSocketSecurityTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static string Request(int port, string? origin = null, string? host = null, string extra = "")
    {
        string request = $"GET / HTTP/1.1\r\nHost: {host ?? $"127.0.0.1:{port}"}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: AAECAwQFBgcICQoLDA0ODw==\r\n";
        if (origin is not null)
        {
            request += $"Origin: {origin}\r\n";
        }

        return request + extra + "\r\n";
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken stop)
    {
        var buffer = new byte[1];
        var text = new StringBuilder();
        while (text.Length < 1024)
        {
            await stream.ReadExactlyAsync(buffer, stop);
            text.Append((char)buffer[0]);
            if (text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                return text.ToString();
            }
        }

        throw new InvalidDataException("the handshake response is too long");
    }

    private static async Task<string> UpgradeAsync(int port, string? origin = null, string? host = null, string extra = "")
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(Wait);
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Request(port, origin, host, extra)), timeout.Token);
        return await ReadHeadersAsync(client.GetStream(), timeout.Token);
    }

    private static async Task<TcpClient> OpenRawAsync(int port)
    {
        var client = new TcpClient { ReceiveBufferSize = 1024 };
        try
        {
            using var timeout = new CancellationTokenSource(Wait);
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Request(port)), timeout.Token);
            Assert.StartsWith("HTTP/1.1 101", await ReadHeadersAsync(client.GetStream(), timeout.Token), StringComparison.Ordinal);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task ClosedAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(Wait);
        try
        {
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], timeout.Token));
        }
        catch (IOException)
        {
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Theory]
    [InlineData("https://untrusted.example")]
    [InlineData("http://localhost:8000")]
    [InlineData("null")]
    [InlineData("")]
    public async Task BrowserOriginsNeedExplicitAuthorization(string origin)
    {
        using var server = TextWebSocketServer.Start(0);
        Assert.StartsWith("HTTP/1.1 403", await UpgradeAsync(server.Port, origin), StringComparison.Ordinal);
        Assert.Equal(0, server.Served);
    }

    [Theory]
    [InlineData("untrusted.example:6677")]
    [InlineData("127.0.0.1.untrusted.example:6677")]
    [InlineData("localhost@untrusted.example:6677")]
    [InlineData("127.0.0.1:1")]
    public async Task RebindingHostsAreRejectedEvenWithoutAnOrigin(string host)
    {
        using var server = TextWebSocketServer.Start(0, origin: "https://reader.example");
        Assert.StartsWith("HTTP/1.1 403", await UpgradeAsync(server.Port, host: host), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 403", await UpgradeAsync(server.Port, "https://reader.example", host), StringComparison.Ordinal);
        Assert.Equal(0, server.Served);
    }

    [Theory]
    [InlineData("https://reader.example")]
    [InlineData("http://127.0.0.1:8000")]
    [InlineData("http://localhost:8000")]
    public async Task TheConfiguredBrowserReceivesPlainTextThroughTheOutput(string origin)
    {
        AppSettingsData settings = AppSettings.Parse($"text_websocket=on\ntext_websocket_origin={origin}");
        var port = new PortSettings(null, AppSettings.Parse(AppSettings.Format(settings)));
        using var output = new TextWebSocketOutput(() => port.TextWebSocket, () => 0, origin: () => port.TextWebSocketOrigin);
        output.Tick();
        TextWebSocketServer server = Assert.IsType<TextWebSocketServer>(output.Server);
        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", origin);
        using var timeout = new CancellationTokenSource(Wait);
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}"), timeout.Token);
        await UntilAsync(() => server.ClientCount == 1);
        await client.SendAsync("ignored input"u8.ToArray(), WebSocketMessageType.Text, true, timeout.Token);
        Assert.True(output.Send(TextKind.Message, "synthetic line"));
        var buffer = new byte[128];
        WebSocketReceiveResult frame = await client.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
        Assert.True(frame.EndOfMessage);
        Assert.Equal("synthetic line", Encoding.UTF8.GetString(buffer, 0, frame.Count));
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Theory]
    [InlineData("https://xn--bcher-kva.example", "https://xn--bcher-kva.example")]
    [InlineData("HTTPS://XN--BCHER-KVA.EXAMPLE:443/", "https://xn--bcher-kva.example")]
    [InlineData("https://xn--bcher-kva.example:444", "https://xn--bcher-kva.example:444")]
    [InlineData("http://xn--bcher-kva.example:80", "http://xn--bcher-kva.example")]
    [InlineData("https://XN--BCHER-KVA.EXAMPLE", "https://xn--bcher-kva.example")]
    [InlineData("HTTPS://READER.EXAMPLE:443/", "https://reader.example")]
    [InlineData("http://127.0.0.1:80/", "http://127.0.0.1")]
    [InlineData("http://127.0.0.1:8000", "http://127.0.0.1:8000")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:80/", "http://[::1]")]
    [InlineData("https://[2001:db8::1]:444/", "https://[2001:db8::1]:444")]
    public async Task ConfiguredOriginsMatchBrowserAsciiSerialization(string configured, string browserOrigin)
    {
        AppSettingsData settings = AppSettings.Parse("text_websocket_origin=" + configured);
        var port = new PortSettings(null, AppSettings.Parse(AppSettings.Format(settings)));
        using var server = TextWebSocketServer.Start(0, origin: port.TextWebSocketOrigin);
        Assert.StartsWith("HTTP/1.1 101", await UpgradeAsync(server.Port, browserOrigin), StringComparison.Ordinal);
        Assert.Equal(browserOrigin, settings.TextWebSocketOrigin);
        Assert.Equal(browserOrigin, port.TextWebSocketOrigin);
        Assert.True(AppSettings.TryParseWebSocketOrigin(browserOrigin, out string? asciiOrigin));
        Assert.Equal(browserOrigin, asciiOrigin);
    }

    [Theory]
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example")]
    [InlineData("HTTPS://BÜCHER.EXAMPLE:443/", "https://xn--bcher-kva.example")]
    [InlineData("https://bücher.example:444", "https://xn--bcher-kva.example:444")]
    [InlineData("http://bücher.example:80", "http://xn--bcher-kva.example")]
    [InlineData("https://bu\u0308cher.example", "https://xn--bcher-kva.example")]
    [InlineData("https://bu\u0308cher.example:444", "https://xn--bcher-kva.example:444")]
    [InlineData("https://ｂücher.example", "https://xn--bcher-kva.example")]
    public async Task UnicodeOriginConfigurationRequiresTheBrowserAsciiForm(string configured, string browserOrigin)
    {
        Assert.False(AppSettings.TryParseWebSocketOrigin(configured, out string? origin));
        Assert.Null(origin);
        AppSettingsData settings = AppSettings.Parse("text_websocket_origin=" + configured);
        Assert.Null(settings.TextWebSocketOrigin);
        var port = new PortSettings(null, AppSettings.Parse(AppSettings.Format(settings)));
        Assert.Null(port.TextWebSocketOrigin);
        using var server = TextWebSocketServer.Start(0, origin: configured);
        Assert.StartsWith("HTTP/1.1 403", await UpgradeAsync(server.Port, browserOrigin), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 101", await UpgradeAsync(server.Port), StringComparison.Ordinal);
        Assert.True(AppSettings.TryParseWebSocketOrigin(browserOrigin, out string? asciiOrigin));
        Assert.Equal(browserOrigin, asciiOrigin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("null")]
    [InlineData("ws://bücher.example")]
    [InlineData("wss://bücher.example")]
    [InlineData("file:///reader.html")]
    [InlineData("https://user@bücher.example")]
    [InlineData("https://bücher.example/page")]
    [InlineData("https://bücher.example?query")]
    [InlineData("https://bücher.example#fragment")]
    [InlineData("https://bücher.example\\")]
    [InlineData("https://bücher.example:65536")]
    [InlineData("https://bü cher.example")]
    [InlineData("https://bücher.example\r\nOrigin: https://untrusted.example")]
    [InlineData("https://user@xn--bcher-kva.example")]
    [InlineData("https://xn--bcher-kva.example/page")]
    [InlineData("https://xn--bcher-kva.example?query")]
    [InlineData("https://xn--bcher-kva.example#fragment")]
    [InlineData("https://xn--bcher-kva.example\\")]
    [InlineData("https://xn--bcher-kva.example:65536")]
    public void ConfiguredOriginNormalizationStillRejectsNonOrigins(string? configured)
    {
        Assert.False(AppSettings.TryParseWebSocketOrigin(configured, out string? origin));
        Assert.Null(origin);
    }

    [Fact]
    public void InvalidIdnHostsAreRejectedWithoutThrowing()
    {
        string[] origins = ["https://a\uD800.example", "https://" + new string('ü', 64) + ".example"];
        foreach (string configured in origins)
        {
            Assert.False(AppSettings.TryParseWebSocketOrigin(configured, out string? origin));
            Assert.Null(origin);
        }
    }

    [Theory]
    [InlineData("https://xn--bcher-kva.example.untrusted.example")]
    [InlineData("http://xn--bcher-kva.example")]
    [InlineData("https://xn--bcher-kva.example:444")]
    [InlineData("https://xn--bucher-xyd.example")]
    [InlineData("null")]
    public async Task AnInternationalizedOriginDoesNotAuthorizeOtherOrigins(string origin)
    {
        using var server = TextWebSocketServer.Start(0, origin: "https://xn--bcher-kva.example");
        Assert.StartsWith("HTTP/1.1 403", await UpgradeAsync(server.Port, origin), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 101", await UpgradeAsync(server.Port), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://reader.example.untrusted.example")]
    [InlineData("http://reader.example")]
    [InlineData("https://reader.example:444")]
    [InlineData("null")]
    public async Task ConfiguringOneOriginDoesNotAuthorizeOtherOrigins(string origin)
    {
        using var server = TextWebSocketServer.Start(0, origin: "https://reader.example");
        Assert.StartsWith("HTTP/1.1 403", await UpgradeAsync(server.Port, origin), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 101", await UpgradeAsync(server.Port, host: $"localhost:{server.Port}"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Host: untrusted.example\r\n")]
    [InlineData("Origin: https://untrusted.example\r\n")]
    [InlineData("Sec-WebSocket-Key: invalid\r\n")]
    [InlineData("Sec-WebSocket-Version: 12\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n")]
    [InlineData("Content-Length: 20\r\n")]
    public async Task AmbiguousHandshakesFailClosed(string extra)
    {
        using var server = TextWebSocketServer.Start(0, origin: "https://reader.example");
        Assert.StartsWith("HTTP/1.1 400", await UpgradeAsync(server.Port, "https://reader.example", extra: extra), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedHandshakesAreRejected()
    {
        using var server = TextWebSocketServer.Start(0);
        Assert.StartsWith("HTTP/1.1 400", await UpgradeAsync(server.Port, extra: "X-Long: " + new string('x', 17 * 1024) + "\r\n"), StringComparison.Ordinal);
        Assert.Equal(0, server.Served);
    }

    [Fact]
    public async Task TheConnectionLimitIncludesIncompleteHandshakesAndSlotsCanBeReused()
    {
        using var server = TextWebSocketServer.Start(0);
        var clients = new List<TcpClient>();
        using var timeout = new CancellationTokenSource(Wait);
        try
        {
            for (int i = 0; i < TextWebSocketServer.MaxConnections; i++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            }

            await UntilAsync(() => server.ConnectionCount == TextWebSocketServer.MaxConnections);
            Assert.Equal(0, server.ClientCount);
            using var refused = new TcpClient();
            await refused.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            await ClosedAsync(refused);
            Assert.Equal(TextWebSocketServer.MaxConnections, server.ConnectionCount);

            clients[0].Dispose();
            await UntilAsync(() => server.ConnectionCount == TextWebSocketServer.MaxConnections - 1);
            using TcpClient replacement = await OpenRawAsync(server.Port);
            await UntilAsync(() => server.ClientCount == 1);
            Assert.Equal(TextWebSocketServer.MaxConnections, server.ConnectionCount);
            server.Dispose();
            foreach (TcpClient client in clients.Skip(1))
            {
                await ClosedAsync(client);
            }
        }
        finally
        {
            foreach (TcpClient client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task AClientThatDoesNotReadIsDroppedWhenItsQueueFills()
    {
        using var server = TextWebSocketServer.Start(0);
        using TcpClient slow = await OpenRawAsync(server.Port);
        await UntilAsync(() => server.ClientCount == 1);
        string text = new('x', 256 * 1024);
        for (int i = 0; i < TextWebSocketServer.MaxQueuedMessages * 128; i++)
        {
            Assert.True(server.Broadcast(text));
        }

        await UntilAsync(() => server.ClientCount == 0);
        await UntilAsync(() => server.ConnectionCount == 0);
        server.Broadcast("a later line");
        using var healthy = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(Wait);
        await healthy.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}"), timeout.Token);
        var buffer = new byte[32];
        WebSocketReceiveResult frame = await healthy.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal("a later line", Encoding.UTF8.GetString(buffer, 0, frame.Count));
    }

    [Fact]
    public void OversizedOutputIsNotRetainedOrQueued()
    {
        using var server = TextWebSocketServer.Start(0);
        Assert.True(server.Broadcast("retained line"));
        Assert.False(server.Broadcast(new string('あ', TextWebSocketServer.MaxTextBytes / 3 + 1)));
        Assert.Equal("retained line", server.Latest);
    }

    [Theory]
    [InlineData(new byte[] { 0x81, 0x00 })]
    [InlineData(new byte[] { 0x09, 0x80 })]
    [InlineData(new byte[] { 0x89, 0xFE })]
    [InlineData(new byte[] { 0xC1, 0x80 })]
    [InlineData(new byte[] { 0x83, 0x80 })]
    [InlineData(new byte[] { 0x81, 0xFE, 0x00, 0x01 })]
    [InlineData(new byte[] { 0x82, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x01 })]
    public async Task InvalidOrOversizedFramesAreDroppedBeforeTheirPayload(byte[] header)
    {
        using var server = TextWebSocketServer.Start(0);
        using TcpClient client = await OpenRawAsync(server.Port);
        await UntilAsync(() => server.ClientCount == 1);
        using var timeout = new CancellationTokenSource(Wait);
        await client.GetStream().WriteAsync(header, timeout.Token);
        await ClosedAsync(client);
        await UntilAsync(() => server.ClientCount == 0);
    }

    [Fact]
    public async Task AMaskedPingGetsAPongAndAnIncompleteFrameTimesOut()
    {
        using var server = TextWebSocketServer.Start(0);
        using TcpClient client = await OpenRawAsync(server.Port);
        await UntilAsync(() => server.ClientCount == 1);
        using var timeout = new CancellationTokenSource(Wait);
        await client.GetStream().WriteAsync(new byte[] { 0x89, 0x82, 1, 2, 3, 4, (byte)('h' ^ 1), (byte)('i' ^ 2) }, timeout.Token);
        var pong = new byte[4];
        await client.GetStream().ReadExactlyAsync(pong, timeout.Token);
        Assert.Equal(new byte[] { 0x8A, 2, (byte)'h', (byte)'i' }, pong);
        await client.GetStream().WriteAsync(new byte[] { 0x81 }, timeout.Token);
        await ClosedAsync(client);
        await UntilAsync(() => server.ConnectionCount == 0);
    }
}
