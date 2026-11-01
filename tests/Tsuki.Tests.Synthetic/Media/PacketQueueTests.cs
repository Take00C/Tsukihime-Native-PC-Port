using System.Reflection.Emit;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Tsuki.Media.Ffmpeg;
using Tsuki.Media.Movie;

namespace Tsuki.Tests.Synthetic.Media;

public sealed class PacketQueueTests
{
    public static bool NativeAvailable => MovieInputSecurityTests.NativeAvailable;

    [Fact(Skip = "FFmpeg 9 shared libraries are required.", SkipUnless = nameof(NativeAvailable))]
    public void AProducerReturningAfterCloseReleasesItsPacket()
    {
        using var native = new NativeApi();
        using var queue = new QueueAccess();
        using var packet = new NativePacket(native);
        Assert.Equal(2, packet.References);

        queue.Close();
        packet.Enqueue(queue);

        Assert.Equal(1, packet.References);
        Assert.Equal(0, queue.Count);
        Assert.False(queue.Take(out nint empty, out string kind));
        Assert.Equal(0, empty);
        Assert.Equal("End", kind);
        queue.Close();
        Assert.Equal(1, packet.References);
    }

    [Fact(Skip = "FFmpeg 9 shared libraries are required.", SkipUnless = nameof(NativeAvailable))]
    public void CloseReleasesBufferedPacketsOnceAndSkipsMarkers()
    {
        using var native = new NativeApi();
        using var queue = new QueueAccess();
        using var first = new NativePacket(native);
        using var second = new NativePacket(native);
        first.Enqueue(queue);
        queue.Marker("Loop");
        queue.Add(0);
        second.Enqueue(queue);
        queue.Marker("End");

        queue.Close();
        queue.Close();

        Assert.Equal(0, queue.Count);
        Assert.Equal(1, first.References);
        Assert.Equal(1, second.References);
        Assert.False(queue.Take(out _, out _));
    }

    [Fact(Skip = "FFmpeg 9 shared libraries are required.", SkipUnless = nameof(NativeAvailable))]
    public void APacketTakenBeforeCloseRemainsOwnedByTheConsumer()
    {
        using var native = new NativeApi();
        using var queue = new QueueAccess();
        using var packet = new NativePacket(native);
        nint expected = packet.Address;
        packet.Enqueue(queue);
        Assert.True(queue.Take(out nint taken, out string kind));
        try
        {
            Assert.Equal(expected, taken);
            Assert.Equal("Packet", kind);
            queue.Close();
            Assert.Equal(2, packet.References);
        }
        finally
        {
            native.FreePacket(ref taken);
        }

        Assert.Equal(1, packet.References);
        queue.Close();
        Assert.Equal(1, packet.References);
    }

    [Fact(Skip = "FFmpeg 9 shared libraries are required.", SkipUnless = nameof(NativeAvailable))]
    public void NullPacketsAndDrainMarkersKeepTheirOrder()
    {
        using var queue = new QueueAccess();
        queue.Add(0);
        queue.Marker("Loop");
        queue.Marker("End");

        foreach (string expected in new[] { "Packet", "Loop", "End" })
        {
            Assert.True(queue.Take(out nint empty, out string kind));
            Assert.Equal(0, empty);
            Assert.Equal(expected, kind);
        }

        Assert.Equal(0, queue.Count);
        queue.Close();
        queue.Add(0);
        Assert.Equal(0, queue.Count);
        Assert.False(queue.Take(out nint closed, out string end));
        Assert.Equal(0, closed);
        Assert.Equal("End", end);
    }

    private sealed class QueueAccess : IDisposable
    {
        private delegate bool TakeEntry(object queue, out nint packet, out byte kind);

        private static readonly Type QueueType = typeof(MoviePipeline).Assembly.GetType("Tsuki.Media.Movie.PacketQueue", throwOnError: true)!;
        private static readonly Type KindType = typeof(MoviePipeline).Assembly.GetType("Tsuki.Media.Movie.PacketKind", throwOnError: true)!;
        private static readonly TakeEntry TakePacket = CreateTake();
        private readonly object _queue = Activator.CreateInstance(QueueType, nonPublic: true)!;

        public int Count => (int)QueueType.GetProperty("Count")!.GetValue(_queue)!;

        public void Add(nint packet) => QueueType.GetMethod("Add")!.Invoke(_queue, [packet]);

        public void Marker(string kind) => QueueType.GetMethod("Marker")!.Invoke(_queue, [Enum.Parse(KindType, kind)]);

        public bool Take(out nint packet, out string kind)
        {
            bool taken = TakePacket(_queue, out packet, out byte value);
            kind = Enum.ToObject(KindType, value).ToString()!;
            return taken;
        }

        private static TakeEntry CreateTake()
        {
            // Reflection cannot invoke pointer out parameters.
            var bridge = new DynamicMethod("TakePacket", typeof(bool), [typeof(object), typeof(nint).MakeByRefType(), typeof(byte).MakeByRefType()], QueueType.Module, skipVisibility: true);
            ILGenerator il = bridge.GetILGenerator();
            LocalBuilder packet = il.DeclareLocal(typeof(AVPacket).MakePointerType());
            LocalBuilder kind = il.DeclareLocal(KindType);
            LocalBuilder result = il.DeclareLocal(typeof(bool));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, QueueType);
            il.Emit(OpCodes.Ldloca, packet);
            il.Emit(OpCodes.Ldloca, kind);
            il.Emit(OpCodes.Call, QueueType.GetMethod("Take")!);
            il.Emit(OpCodes.Stloc, result);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, packet);
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Stind_I);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldloc, kind);
            il.Emit(OpCodes.Stind_I1);
            il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Ret);
            return bridge.CreateDelegate<TakeEntry>();
        }

        public void Close() => QueueType.GetMethod("Close")!.Invoke(_queue, null);

        public void Dispose() => Close();
    }

    private sealed class NativePacket : IDisposable
    {
        private readonly NativeApi _native;
        private nint _packet;
        private nint _reference;

        public NativePacket(NativeApi native)
        {
            _native = native;
            _packet = native.AllocatePacket();
            try
            {
                Assert.NotEqual(0, _packet);
                Assert.Equal(0, native.NewPacket(_packet, 16));
                nint buffer = Marshal.ReadIntPtr(_packet, (int)Marshal.OffsetOf<AVPacket>("buf"));
                _reference = native.ReferenceBuffer(buffer);
                Assert.NotEqual(0, _reference);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public nint Address => _packet;

        public int References => _native.BufferReferences(_reference);

        public void Enqueue(QueueAccess queue)
        {
            queue.Add(_packet);
            _packet = 0;
        }

        public void Dispose()
        {
            _native.FreePacket(ref _packet);
            _native.UnreferenceBuffer(ref _reference);
        }
    }

    private sealed class NativeApi : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nint Allocate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int Initialize(nint packet, int size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void Release(ref nint pointer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nint Reference(nint buffer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int CountReferences(nint buffer);

        private readonly nint _codec;
        private readonly nint _util;

        public NativeApi()
        {
            FfmpegLibrary.Load();
            _codec = Load("avcodec");
            _util = Load("avutil");
            AllocatePacket = Bind<Allocate>(_codec, "av_packet_alloc");
            NewPacket = Bind<Initialize>(_codec, "av_new_packet");
            FreePacket = Bind<Release>(_codec, "av_packet_free");
            ReferenceBuffer = Bind<Reference>(_util, "av_buffer_ref");
            UnreferenceBuffer = Bind<Release>(_util, "av_buffer_unref");
            BufferReferences = Bind<CountReferences>(_util, "av_buffer_get_ref_count");
        }

        public Allocate AllocatePacket { get; }
        public Initialize NewPacket { get; }
        public Release FreePacket { get; }
        public Reference ReferenceBuffer { get; }
        public Release UnreferenceBuffer { get; }
        public CountReferences BufferReferences { get; }

        private static nint Load(string name) => NativeLibrary.Load(Path.Combine(FfmpegLibrary.RootPath, FfmpegLibrary.RequiredDlls.Single(n => n.Contains(name, StringComparison.Ordinal))));

        private static T Bind<T>(nint library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        public void Dispose()
        {
            NativeLibrary.Free(_util);
            NativeLibrary.Free(_codec);
        }
    }
}
