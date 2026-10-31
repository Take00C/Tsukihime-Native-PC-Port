// The text WebSocket (package K23): a feed of every message and choice list for browser text hookers (texthooker-ui and the like, with Yomitan), as a minimal
// RFC 6455 server on a TcpListener bound to 127.0.0.1 only (no HttpListener, no URL reservation, no firewall prompt). Each text is one unfragmented text frame
// to every connected client; a client that connects late first receives the latest text. Off by default: Config > System > Text WebSocket; settings.ini
// text_websocket=on|off and text_websocket_port= (default 6677, the port texthooker-ui connects to out of the box).
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Tsuki.App.Play;

/// <summary>A small WebSocket server that broadcasts text frames.</summary>
internal sealed class TextWebSocketServer : IDisposable
{
    /// <summary>The port when settings.ini names none.</summary>
    public const int DefaultPort = 6677;

    /// <summary>The GUID of the handshake (RFC 6455 section 1.3).</summary>
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private const int MaxHandshakeBytes = 16 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<Client> _clients = [];
    private string? _latest;
    private int _served;
    private bool _disposed;

    private TextWebSocketServer(TcpListener listener, Action<string>? log)
    {
        _listener = listener;
        _log = log;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The port the server listens on (the one asked for, or the one the system gave for 0).</summary>
    public int Port { get; }

    /// <summary>The address: always the IPv4 loopback.</summary>
    public static IPAddress Address => IPAddress.Loopback;

    /// <summary>Clients with a finished handshake.</summary>
    public int ClientCount
    {
        get
        {
            lock (_lock)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>Clients served since the start (the tests).</summary>
    public int Served => Volatile.Read(ref _served);

    /// <summary>The last text broadcast, which a late client receives first.</summary>
    public string? Latest
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    /// <summary>Binds 127.0.0.1:<paramref name="port"/> (0 for any free port) and starts accepting.</summary>
    /// <exception cref="SocketException">The port is in use or cannot be bound.</exception>
    public static TextWebSocketServer Start(int port, Action<string>? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        var listener = new TcpListener(Address, port);
        listener.Start(backlog: 8);
        var server = new TextWebSocketServer(listener, log);
        _ = Task.Run(server.AcceptLoopAsync);
        return server;
    }

    /// <summary>Sends <paramref name="text"/> to every client as one text frame and keeps it for late clients. Safe from any thread.</summary>
    public void Broadcast(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Client[] clients;
        lock (_lock)
        {
            _latest = text;
            clients = [.. _clients];
        }

        foreach (Client c in clients)
        {
            c.Queue.Writer.TryWrite(text);
        }
    }

    /// <summary>Stops accepting, closes every client.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _listener.Stop();
        Client[] clients;
        lock (_lock)
        {
            clients = [.. _clients];
            _clients.Clear();
        }

        foreach (Client c in clients)
        {
            c.Close();
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(tcp));
        }
    }

    private async Task ServeAsync(TcpClient tcp)
    {
        Client? client = null;
        try
        {
            tcp.NoDelay = true;
            NetworkStream stream = tcp.GetStream();
            string? key = await ReadHandshakeAsync(stream).ConfigureAwait(false);
            if (key is null)
            {
                await WriteAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")).ConfigureAwait(false);
                return;
            }

            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));
            await WriteAsync(stream, Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n")).ConfigureAwait(false);
            client = new Client(tcp, stream);
            string? latest;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _clients.Add(client);
                latest = _latest;
            }

            Interlocked.Increment(ref _served);
            _log?.Invoke($"websocket: client connected ({ClientCount} connected)");
            if (latest is not null)
            {
                client.Queue.Writer.TryWrite(latest);                           // a late client gets the current line at once
            }

            Task writer = WriteLoopAsync(client);
            await ReadLoopAsync(client).ConfigureAwait(false);
            client.Queue.Writer.TryComplete();
            await writer.ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // the client went away, or we are stopping
        }
        finally
        {
            if (client is not null)
            {
                bool removed;
                lock (_lock)
                {
                    removed = _clients.Remove(client);
                }

                client.Close();
                if (removed)
                {
                    _log?.Invoke($"websocket: client left ({ClientCount} connected)");
                }
            }
            else
            {
                tcp.Dispose();
            }
        }
    }

    /// <summary>Reads the HTTP upgrade request and returns its Sec-WebSocket-Key, or null when it is not a WebSocket handshake.</summary>
    private async Task<string?> ReadHandshakeAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(HandshakeTimeout);
        var buffer = new byte[MaxHandshakeBytes];
        int length = 0;
        int end = -1;
        while (end < 0)
        {
            if (length == buffer.Length)
            {
                return null;
            }

            int n = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            length += n;
            end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }

        string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal))
        {
            return null;
        }

        string? key = null;
        bool upgrade = false;
        bool connection = false;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                key = value;
            }
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                upgrade = value.Contains("websocket", StringComparison.OrdinalIgnoreCase);
            }
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                connection = value.Contains("Upgrade", StringComparison.OrdinalIgnoreCase);
            }
        }

        return upgrade && connection && key is { Length: > 0 } ? key : null;
    }

    private async Task WriteLoopAsync(Client client)
    {
        try
        {
            await foreach (string text in client.Queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await client.SendAsync(Frame(0x1, Encoding.UTF8.GetBytes(text)), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            client.Close();                                                   // a client that stopped reading (the write timed out) is dropped
        }
    }

    /// <summary>Reads the client's frames: a close is answered and ends the connection, a ping gets a pong, data frames are ignored.</summary>
    private async Task ReadLoopAsync(Client client)
    {
        var header = new byte[14];
        while (!_stop.IsCancellationRequested)
        {
            await client.Stream.ReadExactlyAsync(header.AsMemory(0, 2), _stop.Token).ConfigureAwait(false);
            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;
            if (length == 126)
            {
                await client.Stream.ReadExactlyAsync(header.AsMemory(2, 2), _stop.Token).ConfigureAwait(false);
                length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            }
            else if (length == 127)
            {
                await client.Stream.ReadExactlyAsync(header.AsMemory(2, 8), _stop.Token).ConfigureAwait(false);
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(2));
            }

            if (length is < 0 or > 1 << 20)
            {
                return;                                                       // nobody sends us a megabyte: drop the connection
            }

            var mask = new byte[4];
            if (masked)
            {
                await client.Stream.ReadExactlyAsync(mask, _stop.Token).ConfigureAwait(false);
            }

            var payload = new byte[length];
            if (length > 0)
            {
                await client.Stream.ReadExactlyAsync(payload, _stop.Token).ConfigureAwait(false);
                if (masked)
                {
                    for (int i = 0; i < payload.Length; i++)
                    {
                        payload[i] ^= mask[i & 3];
                    }
                }
            }

            switch (opcode)
            {
                case 0x8:
                    // close: echo the status code (if any) and finish
                    await client.SendAsync(Frame(0x8, payload.Length >= 2 ? payload.AsSpan(0, 2).ToArray() : []), _stop.Token).ConfigureAwait(false);
                    return;
                case 0x9:
                    await client.SendAsync(Frame(0xA, payload), _stop.Token).ConfigureAwait(false);
                    break;
                default:
                    break;                                                    // text, binary, pong, continuation: nothing to do with them
            }
        }
    }

    /// <summary>One unmasked, unfragmented frame (FIN set) of <paramref name="opcode"/> with <paramref name="payload"/>.</summary>
    internal static byte[] Frame(int opcode, ReadOnlySpan<byte> payload)
    {
        int headerLength = payload.Length < 126 ? 2 : payload.Length <= ushort.MaxValue ? 4 : 10;
        var frame = new byte[headerLength + payload.Length];
        frame[0] = (byte)(0x80 | opcode);
        if (payload.Length < 126)
        {
            frame[1] = (byte)payload.Length;
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            frame[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
        }
        else
        {
            frame[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)payload.Length);
        }

        payload.CopyTo(frame.AsSpan(headerLength));
        return frame;
    }

    private static async Task WriteAsync(NetworkStream stream, byte[] bytes)
    {
        using var timeout = new CancellationTokenSource(WriteTimeout);
        await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
    }

    private sealed class Client(TcpClient tcp, NetworkStream stream)
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public NetworkStream Stream { get; } = stream;

        public Channel<string> Queue { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        public async Task SendAsync(byte[] frame, CancellationToken stop)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(WriteTimeout);
            await _write.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await Stream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }

        public void Close()
        {
            Queue.Writer.TryComplete();
            try
            {
                tcp.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

/// <summary>The WebSocket output of the text feed: starts the server when the switch goes on, stops it when it goes off, broadcasts each text.</summary>
internal sealed class TextWebSocketOutput(Func<bool> enabled, Func<int> port, Action<string>? log = null) : ITextOutput, IDisposable
{
    private readonly ConcurrentQueue<string> _pending = new();
    private TextWebSocketServer? _server;
    private int _failedPort = -1;

    /// <inheritdoc/>
    public string Name => "websocket";

    /// <inheritdoc/>
    public bool Enabled => enabled();

    /// <summary>The running server, or null while the switch is off or the port could not be bound.</summary>
    public TextWebSocketServer? Server => _server;

    /// <summary>Texts broadcast so far.</summary>
    public int Sent { get; private set; }

    /// <summary>Raised on the game thread when the server started listening (the port).</summary>
    public event Action<int>? Listening;

    /// <summary>Raised on the game thread when the port could not be bound (the port, the system's words); not retried until the switch is toggled.</summary>
    public event Action<int, string>? Failed;

    /// <summary>For the state line: "off", "port 6677 (1 client)" or "port 6677 failed".</summary>
    public string State => _server is { } s ? $"port {s.Port} ({s.ClientCount} clients)" : _failedPort >= 0 ? $"port {_failedPort} failed" : "off";

    /// <inheritdoc/>
    public void Tick()
    {
        while (_pending.TryDequeue(out string? line))
        {
            log?.Invoke(line);
        }

        bool on = Enabled;
        if (on && _server is null && _failedPort < 0)
        {
            int p = port();
            try
            {
                _server = TextWebSocketServer.Start(p, _pending.Enqueue);
                log?.Invoke($"websocket: listening on ws://127.0.0.1:{_server.Port}");
                Listening?.Invoke(_server.Port);
            }
            catch (SocketException e)
            {
                _failedPort = p;
                log?.Invoke($"websocket: port {p} could not be bound ({e.SocketErrorCode}: {e.Message})");
                Failed?.Invoke(p, e.Message);
            }
        }
        else if (!on)
        {
            _failedPort = -1;                                                  // the next switch-on tries the port again
            if (_server is not null)
            {
                _server.Dispose();
                _server = null;
                log?.Invoke("websocket: stopped");
            }
        }
    }

    /// <inheritdoc/>
    public bool Send(TextKind kind, string text)
    {
        if (_server is null)
        {
            return false;
        }

        _server.Broadcast(text);
        Sent++;
        log?.Invoke($"websocket: {(kind == TextKind.Message ? "message" : "choice")}, {text.Length} chars to {_server.ClientCount} clients");
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _server?.Dispose();
        _server = null;
    }
}
