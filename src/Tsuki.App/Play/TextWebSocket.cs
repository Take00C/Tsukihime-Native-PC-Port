// Loopback text feed for native hookers and an explicitly trusted browser origin.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Tsuki.App.Services;

namespace Tsuki.App.Play;

/// <summary>A small WebSocket server that broadcasts text frames.</summary>
internal sealed class TextWebSocketServer : IDisposable
{
    /// <summary>The port when settings.ini names none.</summary>
    public const int DefaultPort = 6677;

    /// <summary>The GUID of the handshake (RFC 6455 section 1.3).</summary>
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private const int MaxHandshakeBytes = 16 * 1024;
    internal const int MaxConnections = 8;
    internal const int MaxQueuedMessages = 16;
    internal const int MaxTextBytes = 1 << 20;
    private const int MaxFrameBytes = 1 << 20;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly string? _origin;
    private readonly Lock _lock = new();
    private readonly List<Client> _clients = [];
    private readonly HashSet<TcpClient> _connections = [];
    private string? _latest;
    private int _served;
    private bool _disposed;

    private TextWebSocketServer(TcpListener listener, Action<string>? log, string? origin)
    {
        _listener = listener;
        _log = log;
        _stopToken = _stop.Token;
        _origin = origin;
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

    internal int ConnectionCount
    {
        get
        {
            lock (_lock)
            {
                return _connections.Count;
            }
        }
    }

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
    public static TextWebSocketServer Start(int port, Action<string>? log = null, string? origin = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        var listener = new TcpListener(Address, port);
        listener.Start(backlog: 8);
        AppSettings.TryParseWebSocketOrigin(origin, out string? trustedOrigin);
        var server = new TextWebSocketServer(listener, log, trustedOrigin);
        _ = Task.Run(server.AcceptLoopAsync);
        return server;
    }

    /// <summary>Sends <paramref name="text"/> to every client as one text frame and keeps it for late clients. Safe from any thread.</summary>
    public bool Broadcast(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxTextBytes || Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
        {
            return false;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            _latest = text;
            foreach (Client c in _clients)
            {
                if (!c.Queue.Writer.TryWrite(text))
                {
                    c.Close();
                }
            }
        }

        return true;
    }

    /// <summary>Stops accepting, closes every client.</summary>
    public void Dispose()
    {
        Client[] clients;
        TcpClient[] connections;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            clients = [.. _clients];
            connections = [.. _connections];
            _clients.Clear();
            _connections.Clear();
        }

        _stop.Cancel();
        _listener.Stop();
        foreach (Client c in clients)
        {
            c.Close();
        }

        foreach (TcpClient tcp in connections)
        {
            tcp.Dispose();
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_stopToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            lock (_lock)
            {
                if (_disposed || _connections.Count >= MaxConnections)
                {
                    tcp.Dispose();
                    continue;
                }

                _connections.Add(tcp);
            }

            _ = ServeAsync(tcp);
        }
    }

    private async Task ServeAsync(TcpClient tcp)
    {
        Client? client = null;
        try
        {
            tcp.NoDelay = true;
            NetworkStream stream = tcp.GetStream();
            (string? key, int status) = await ReadHandshakeAsync(stream).ConfigureAwait(false);
            if (key is null)
            {
                string reason = status == 403 ? "Forbidden" : "Bad Request";
                await WriteAsync(stream, Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")).ConfigureAwait(false);
                return;
            }

            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));
            await WriteAsync(stream, Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n")).ConfigureAwait(false);
            client = new Client(tcp, stream);
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_latest is { } latest)
                {
                    client.Queue.Writer.TryWrite(latest);
                }

                _clients.Add(client);
            }

            Interlocked.Increment(ref _served);
            _log?.Invoke($"websocket: client connected ({ClientCount} connected)");
            Task writer = WriteLoopAsync(client);
            try
            {
                await ReadLoopAsync(client).ConfigureAwait(false);
            }
            finally
            {
                client.Close();
                await writer.ConfigureAwait(false);
            }
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

            lock (_lock)
            {
                _connections.Remove(tcp);
            }
        }
    }

    private async Task<(string? Key, int Status)> ReadHandshakeAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
        timeout.CancelAfter(HandshakeTimeout);
        var buffer = new byte[MaxHandshakeBytes];
        int length = 0;
        int end = -1;
        while (end < 0)
        {
            if (length == buffer.Length)
            {
                return (null, 400);
            }

            int n = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (n == 0)
            {
                return (null, 400);
            }

            length += n;
            end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }

        if (end + 4 != length)
        {
            return (null, 400);
        }

        for (int i = 0; i < end; i++)
        {
            if (buffer[i] > 126 || (buffer[i] < 32 && buffer[i] is not (9 or 10 or 13)))
            {
                return (null, 400);
            }
        }

        string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        string[] request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || !request[1].StartsWith('/') || request[2] != "HTTP/1.1")
        {
            return (null, 400);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return (null, 400);
            }

            string name = line[..colon];
            string value = line[(colon + 1)..].Trim();
            if (name.Any(c => c <= 32 || c >= 127) || !headers.TryAdd(name, value))
            {
                return (null, 400);
            }
        }

        if (!headers.TryGetValue("Upgrade", out string? upgrade) || !HasToken(upgrade, "websocket")
            || !headers.TryGetValue("Connection", out string? connection) || !HasToken(connection, "Upgrade")
            || !headers.TryGetValue("Sec-WebSocket-Version", out string? version) || version != "13"
            || !headers.TryGetValue("Sec-WebSocket-Key", out string? key) || key.Length != 24
            || !Convert.TryFromBase64String(key, new byte[16], out int keyLength) || keyLength != 16
            || headers.ContainsKey("Transfer-Encoding")
            || (headers.TryGetValue("Content-Length", out string? bodyLength) && bodyLength != "0"))
        {
            return (null, 400);
        }

        if (!headers.TryGetValue("Host", out string? host)
            || !(host.Equals($"127.0.0.1:{Port}", StringComparison.Ordinal)
                || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase)
                || (Port == 80 && (host == "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))))
            || (headers.TryGetValue("Origin", out string? origin) && (_origin is null || origin != _origin)))
        {
            return (null, 403);
        }

        return (key, 101);
    }

    private static bool HasToken(string value, string token) => value.Split(',').Any(part => part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));

    private async Task WriteLoopAsync(Client client)
    {
        try
        {
            await foreach (string text in client.Queue.Reader.ReadAllAsync(_stopToken).ConfigureAwait(false))
            {
                await client.SendAsync(Frame(0x1, Encoding.UTF8.GetBytes(text)), _stopToken).ConfigureAwait(false);
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
        var payload = new byte[4096];
        var mask = new byte[4];
        while (!_stopToken.IsCancellationRequested)
        {
            await client.Stream.ReadExactlyAsync(header.AsMemory(0, 1), _stopToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
            timeout.CancelAfter(FrameTimeout);
            await client.Stream.ReadExactlyAsync(header.AsMemory(1, 1), timeout.Token).ConfigureAwait(false);
            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            bool control = opcode >= 0x8;
            if (!masked || (header[0] & 0x70) != 0 || opcode is not (0x0 or 0x1 or 0x2 or 0x8 or 0x9 or 0xA)
                || (control && ((header[0] & 0x80) == 0 || (header[1] & 0x7F) > 125)))
            {
                return;
            }

            long length = header[1] & 0x7F;
            if (length == 126)
            {
                await client.Stream.ReadExactlyAsync(header.AsMemory(2, 2), timeout.Token).ConfigureAwait(false);
                length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                if (length < 126)
                {
                    return;
                }
            }
            else if (length == 127)
            {
                await client.Stream.ReadExactlyAsync(header.AsMemory(2, 8), timeout.Token).ConfigureAwait(false);
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(2));
                if (length <= ushort.MaxValue)
                {
                    return;
                }
            }

            if (length is < 0 or > MaxFrameBytes || (opcode == 0x8 && length == 1))
            {
                return;
            }

            await client.Stream.ReadExactlyAsync(mask, timeout.Token).ConfigureAwait(false);
            int offset = 0;
            while (offset < length)
            {
                int count = (int)Math.Min(payload.Length, length - offset);
                await client.Stream.ReadExactlyAsync(payload.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                if (control)
                {
                    for (int i = 0; i < count; i++)
                    {
                        payload[i] ^= mask[i & 3];
                    }
                }

                offset += count;
            }

            switch (opcode)
            {
                case 0x8:
                    // close: echo the status code (if any) and finish
                    await client.SendAsync(Frame(0x8, length >= 2 ? payload.AsSpan(0, 2) : []), _stopToken).ConfigureAwait(false);
                    return;
                case 0x9:
                    await client.SendAsync(Frame(0xA, payload.AsSpan(0, (int)length)), _stopToken).ConfigureAwait(false);
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

        public Channel<string> Queue { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedMessages) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

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
internal sealed class TextWebSocketOutput(Func<bool> enabled, Func<int> port, Action<string>? log = null, Func<string?>? origin = null) : ITextOutput, IDisposable
{
    private const int MaxPendingLogs = 64;
    private readonly Channel<string> _pending = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxPendingLogs) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
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
        for (int i = 0; i < MaxPendingLogs && _pending.Reader.TryRead(out string? line); i++)
        {
            log?.Invoke(line);
        }

        bool on = Enabled;
        if (on && _server is null && _failedPort < 0)
        {
            int p = port();
            try
            {
                _server = TextWebSocketServer.Start(p, line => _pending.Writer.TryWrite(line), origin?.Invoke());
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

        if (!_server.Broadcast(text))
        {
            return false;
        }

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
