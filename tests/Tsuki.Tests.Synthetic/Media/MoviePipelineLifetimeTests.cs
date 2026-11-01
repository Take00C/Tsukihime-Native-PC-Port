using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Tsuki.Media.Ffmpeg;
using Tsuki.Media.Movie;

namespace Tsuki.Tests.Synthetic.Media;

[CollectionDefinition("Movie native callbacks", DisableParallelization = true)]
public sealed class MovieNativeCallbacksCollection;

[Collection("Movie native callbacks")]
public sealed class MoviePipelineLifetimeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tsuki-movie-lifetime-" + Guid.NewGuid().ToString("N"));

    public static bool NativeAvailable => MovieInputSecurityTests.NativeAvailable;

    private MoviePipeline Open()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "synthetic.mp4");
        string fixture = (string)typeof(MovieInputSecurityTests).GetField("Mp4", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        File.WriteAllBytes(path, Convert.FromBase64String(fixture));
        return new MoviePipeline(path, new MoviePipelineOptions(FrameBuffers: 2, VideoThreads: 1));
    }

    [Theory(Skip = "FFmpeg 9 shared libraries are required.", SkipUnless = nameof(NativeAvailable))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeKeepsNativeObjectsAliveUntilTheirWorkerReturns(bool video)
    {
        using MoviePipeline pipeline = Open();
        object demux = Field(pipeline, "_dmx");
        object decoder = Field(pipeline, video ? "_vdec" : "_adec");
        nint context = PointerField(decoder, "_ctx");
        nint format = PointerField(demux, "_fmt");
        nint frame = PointerField(decoder, "_frame");
        nint resampler = video ? 0 : PointerField(decoder, "_swr");
        Assert.NotEqual(0, context);
        Assert.NotEqual(0, format);
        Assert.NotEqual(0, frame);
        if (!video)
        {
            Assert.NotEqual(0, resampler);
        }

        var frees = new ConcurrentDictionary<nint, int>();
        void CountFree(nint address) => frees.AddOrUpdate(Marshal.ReadIntPtr(address), 1, (_, count) => count + 1);
        using var freeContext = new NativeHook("avcodec_free_context", CountFree);
        using var freeFormat = new NativeHook("avformat_close_input", CountFree);
        using var freeFrame = new NativeHook("av_frame_free", CountFree);
        using var freeResampler = new NativeHook("swr_free", CountFree);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool resumeNative = false;
        using var delayed = new NativeHook(video ? "avcodec_receive_frame" : "av_read_frame", (first, _) =>
        {
            if (first != (video ? context : format))
            {
                return null;
            }

            entered.Set();
            if (!release.Wait(Timeout))
            {
                throw new TimeoutException("the delayed native call was not released");
            }

            // A failed assertion must not resume native code with a freed pointer.
            return Volatile.Read(ref resumeNative) ? null : ffmpeg.AVERROR_EXIT;
        });

        Task? disposal = null;
        try
        {
            pipeline.Start();
            Assert.True(entered.Wait(Timeout, TestContext.Current.CancellationToken), "the movie worker did not reach the native callback");
            disposal = Task.Run(pipeline.Dispose, TestContext.Current.CancellationToken);
            await disposal.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Thread worker = (Thread)Field(pipeline, video ? "_decodeThread" : "_demuxThread");
            Assert.True(worker.IsAlive);
            Assert.Equal(context, PointerField(decoder, "_ctx"));
            Assert.Equal(frame, PointerField(decoder, "_frame"));
            Assert.Equal(0, frees.GetValueOrDefault(context));
            Assert.Equal(0, frees.GetValueOrDefault(frame));
            if (!video)
            {
                Assert.Equal(format, PointerField(demux, "_fmt"));
                Assert.Equal(resampler, PointerField(decoder, "_swr"));
                Assert.Equal(0, frees.GetValueOrDefault(format));
                Assert.Equal(0, frees.GetValueOrDefault(resampler));
            }

            pipeline.Dispose();
            Volatile.Write(ref resumeNative, true);
        }
        finally
        {
            release.Set();
            if (disposal is not null)
            {
                await disposal.WaitAsync(Timeout, CancellationToken.None);
            }

            pipeline.Stop();
            foreach (string name in new[] { "_demuxThread", "_decodeThread" })
            {
                if (typeof(MoviePipeline).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipeline) is Thread worker)
                {
                    Assert.True(worker.Join(Timeout), "the movie worker did not exit after release");
                }
            }
        }

        Assert.Null(pipeline.Error);
        Assert.Equal(0, PointerField(decoder, "_ctx"));
        Assert.Equal(0, PointerField(decoder, "_frame"));
        Assert.Equal(1, frees.GetValueOrDefault(context));
        Assert.Equal(1, frees.GetValueOrDefault(frame));
        if (!video)
        {
            Assert.Equal(0, PointerField(demux, "_fmt"));
            Assert.Equal(0, PointerField(decoder, "_swr"));
            Assert.Equal(1, frees.GetValueOrDefault(format));
            Assert.Equal(1, frees.GetValueOrDefault(resampler));
        }

        pipeline.Dispose();
        Assert.Equal(1, frees.GetValueOrDefault(context));
    }

    [Fact(Skip = "FFmpeg 9 shared libraries are required.", SkipUnless = nameof(NativeAvailable))]
    public void DisposeBeforeStartReleasesNativeObjectsAndCannotRestart()
    {
        using MoviePipeline pipeline = Open();
        object video = Field(pipeline, "_vdec");
        object audio = Field(pipeline, "_adec");
        object demux = Field(pipeline, "_dmx");
        Assert.NotEqual(0, PointerField(video, "_ctx"));
        Assert.NotEqual(0, PointerField(audio, "_ctx"));
        Assert.NotEqual(0, PointerField(demux, "_fmt"));

        pipeline.Dispose();
        pipeline.Dispose();

        Assert.Equal(0, PointerField(video, "_ctx"));
        Assert.Equal(0, PointerField(video, "_frame"));
        Assert.Equal(0, PointerField(audio, "_ctx"));
        Assert.Equal(0, PointerField(audio, "_frame"));
        Assert.Equal(0, PointerField(audio, "_swr"));
        Assert.Equal(0, PointerField(demux, "_fmt"));
        Assert.Throws<ObjectDisposedException>(pipeline.Start);
    }

    [Theory(Skip = "FFmpeg 9 shared libraries are required.", SkipUnless = nameof(NativeAvailable))]
    [InlineData(false)]
    [InlineData(true)]
    public void StoppedPipelinesCannotStartAgain(bool startFirst)
    {
        using MoviePipeline pipeline = Open();
        if (startFirst)
        {
            pipeline.Start();
        }

        pipeline.Stop();
        Assert.Throws<InvalidOperationException>(pipeline.Start);
        pipeline.Dispose();
        Assert.Throws<ObjectDisposedException>(pipeline.Start);
    }

    private static object Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static nint PointerField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var method = new DynamicMethod("ReadNativePointer", typeof(nint), [typeof(object)], instance.GetType().Module, skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instance.GetType());
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<object, nint>>()(instance);
    }

    private sealed class NativeHook : IDisposable
    {
        private readonly FieldInfo _field;
        private readonly Delegate _original;
        private readonly nint _library;

        public NativeHook(string name, Action<nint> before)
        {
            _field = Binding(name);
            _original = (Delegate)_field.GetValue(null)!;
            Action<nint> call = Adapt<Action<nint>>(LoadNative(name, out _library));
            Action<nint> wrapper = pointer =>
            {
                before(pointer);
                call(pointer);
            };
            _field.SetValue(null, Adapt(wrapper, _field.FieldType));
        }

        public NativeHook(string name, Func<nint, nint, int?> intercept)
        {
            _field = Binding(name);
            _original = (Delegate)_field.GetValue(null)!;
            Func<nint, nint, int> call = Adapt<Func<nint, nint, int>>(LoadNative(name, out _library));
            Func<nint, nint, int> wrapper = (first, second) => intercept(first, second) ?? call(first, second);
            _field.SetValue(null, Adapt(wrapper, _field.FieldType));
        }

        private static FieldInfo Binding(string name) => Assert.Single(typeof(ffmpeg).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)),
            f => f.Name == name && typeof(Delegate).IsAssignableFrom(f.FieldType));

        private Delegate LoadNative(string name, out nint library)
        {
            string component = name.StartsWith("avcodec", StringComparison.Ordinal) ? "avcodec"
                : name == "av_frame_free" ? "avutil"
                : name == "swr_free" ? "swresample" : "avformat";
            string file = FfmpegLibrary.RequiredDlls.Single(f => f.Contains(component, StringComparison.Ordinal));
            library = NativeLibrary.Load(Path.Combine(FfmpegLibrary.RootPath, file));
            // Calling AutoGen's lazy binding could replace our hook on another worker.
            return Marshal.GetDelegateForFunctionPointer(NativeLibrary.GetExport(library, name), _field.FieldType);
        }

        private static T Adapt<T>(Delegate callback) where T : Delegate => (T)Adapt(callback, typeof(T));

        private static Delegate Adapt(Delegate callback, Type targetType)
        {
            MethodInfo target = targetType.GetMethod("Invoke")!;
            Type[] parameters = target.GetParameters().Select(p => p.ParameterType).ToArray();
            var method = new DynamicMethod("NativeCallback", target.ReturnType, [callback.GetType(), .. parameters], typeof(NativeHook).Module, skipVisibility: true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < parameters.Length; i++)
            {
                il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Conv_I);
            }

            il.Emit(OpCodes.Callvirt, callback.GetType().GetMethod("Invoke")!);
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate(targetType, callback);
        }

        public void Dispose()
        {
            _field.SetValue(null, _original);
            NativeLibrary.Free(_library);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
