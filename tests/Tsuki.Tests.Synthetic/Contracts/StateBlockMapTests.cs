using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Synthetic.Contracts;

/// <summary>
/// Generic contract checks of the generated state blocks (ARCHITECTURE 3.2, PACE 7.1 L-1): the size,
/// the field map tiling the block, and for every field a round trip through its accessor that writes
/// exactly the field's bytes and nothing else (so offsets, sizes and types agree with the map).
/// </summary>
public sealed class StateBlockMapTests
{
    [Fact]
    public void SceneStateMap() => BlockChecks<SceneState>.All(0x1A08);

    [Fact]
    public void HistoryRecordMap() => BlockChecks<HistoryRecord>.All(0x3A10);

    [Fact]
    public void SoundChannelRecordMap() => BlockChecks<SoundChannelRecord>.All(0x34);

    [Fact]
    public void ConfigBlockMap() => BlockChecks<ConfigBlock>.All(0x140);

    [Fact]
    public void SystemBlockMap() => BlockChecks<SystemBlock>.All(0x18AC);

    [Fact]
    public void VarsMap() => BlockChecks<Vars>.All(2 * (1200 + 1400 + 2000));

    [Fact]
    public void ViewsShareTheirStorage()
    {
        byte[] big = new byte[SceneState.Size + 5];
        var view = new SceneState(big, 5);
        view.SetPic(0, 0x0102);
        Assert.Equal(0x02, big[5 + 4]);
        Assert.Equal(0x01, big[5 + 5]);
        Assert.Throws<ArgumentException>(() => new SceneState(big, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneState(big, -1));

        var copy = view.Clone();
        Assert.NotSame(view.Storage, copy.Storage);
        Assert.True(copy.ContentEquals(view));
        copy.SetPic(0, 7);
        Assert.False(copy.ContentEquals(view));
        view.CopyFrom(copy);
        Assert.Equal(7, view.GetPic(0));
        Assert.Throws<ArgumentException>(() => view.CopyFrom(new byte[3]));
    }
}

/// <summary>Reflection-driven checks for one generated block type.</summary>
internal static class BlockChecks<T>
    where T : StateBlock, new()
{
    private delegate Span<byte> SpanProperty(T block);

    private delegate Span<byte> SpanAt(T block, int index);

    public static void All(int expectedSize)
    {
        var fields = Fields();
        Assert.Equal(expectedSize, (int)typeof(T).GetField("Size")!.GetValue(null)!);
        Assert.Equal(expectedSize, new T().Length);
        Tiling(fields, expectedSize);
        foreach (var f in fields)
        {
            switch (f.Kind)
            {
                case StateFieldKind.Scalar: Scalar(f); break;
                case StateFieldKind.Array when f.Type == "bytes": ByteEntries(f); break;
                case StateFieldKind.Array: Array(f); break;
                case StateFieldKind.Bits: Bits(f); break;
                case StateFieldKind.Bytes: Bytes(f); break;
                case StateFieldKind.Block: Block(f); break;
                case StateFieldKind.Unused: Unused(f); break;
                default: Assert.Fail($"{f.Name}: kind {f.Kind}"); break;
            }
        }

        // the same accessors on a view at an odd offset of a larger array
        byte[] big = new byte[expectedSize + 3];
        var view = (T)Activator.CreateInstance(typeof(T), big, 3)!;
        Assert.Equal(3, view.Offset);
        view.Bytes.Fill(0xEE);
        Assert.Equal(0, big[0] | big[1] | big[2]);
        Assert.Equal(0xEE, big[^1]);
    }

    private static ImmutableArray<StateField> Fields() =>
        (ImmutableArray<StateField>)typeof(T).GetProperty("Fields", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    private static void Tiling(ImmutableArray<StateField> fields, int size)
    {
        int pos = 0;
        foreach (var f in fields)
        {
            Assert.True(f.Offset == pos, $"{typeof(T).Name}: gap or overlap at 0x{pos:X} before {f.Name}");
            Assert.True(f.Size > 0, $"{f.Name} is empty");
            if (f.Kind == StateFieldKind.Array)
            {
                Assert.Equal(0, f.Size % f.Count);
                if (f.Type != "bytes")
                {
                    Assert.Equal(f.EntrySize, f.PerEntry * TypeSize(f.Type));
                }
            }
            else if (f.Kind == StateFieldKind.Scalar)
            {
                Assert.Equal(f.Size, TypeSize(f.Type));
            }
            else if (f.Kind == StateFieldKind.Bits)
            {
                Assert.InRange(f.Count, (8 * f.Size) - 7, 8 * f.Size);
            }

            pos = f.End;
        }

        Assert.Equal(size, pos);
        var names = fields.Where(f => f.Kind != StateFieldKind.Unused).Select(f => f.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    private static void Scalar(StateField f)
    {
        var p = typeof(T).GetProperty(f.Name, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(p is not null && p.CanRead && p.CanWrite, $"{typeof(T).Name}.{f.Name}: no get/set property");
        Assert.Equal(ClrType(f.Type), p.PropertyType);
        var block = new T();
        byte[] sentinel = Sentinel(f.Size, f.Offset);
        object value = FromBytes(f.Type, sentinel);
        p.SetValue(block, value);
        OnlyWritten(block, f.Offset, sentinel, f.Name);
        Assert.Equal(value, p.GetValue(block));
    }

    private static void Array(StateField f)
    {
        bool two = f.PerEntry > 1;
        Type clr = ClrType(f.Type);
        Type[] getArgs = two ? [typeof(int), typeof(int)] : [typeof(int)];
        Type[] setArgs = two ? [typeof(int), typeof(int), clr] : [typeof(int), clr];
        var get = typeof(T).GetMethod("Get" + f.Name, getArgs);
        var set = typeof(T).GetMethod("Set" + f.Name, setArgs);
        Assert.True(get is not null && set is not null, $"{typeof(T).Name}: Get/Set{f.Name} missing");
        Assert.Equal(clr, get.ReturnType);
        int es = TypeSize(f.Type);
        var block = new T();
        for (int i = 0; i < f.Count; i++)
        {
            for (int j = 0; j < f.PerEntry; j++)
            {
                int at = f.Offset + (i * f.EntrySize) + (j * es);
                byte[] sentinel = Sentinel(es, at);
                object value = FromBytes(f.Type, sentinel);
                object[] idx = two ? [i, j] : [i];
                set.Invoke(block, [.. idx, value]);
                OnlyWritten(block, at, sentinel, $"{f.Name}[{i}][{j}]");
                Assert.Equal(value, get.Invoke(block, idx));
                block.Clear();
            }
        }

        if (typeof(T) == typeof(Vars))
        {
            // Vars.GetF / GetZ / GetG reach the neighbouring arrays on purpose (F[1200] = Z[0], G[-1] = Z[1399]);
            // only the block's own ends throw: StateBlockLayoutTests.VarsAliasingOfVmVariables21And22
            return;
        }

        Throws(() => get.Invoke(block, two ? [f.Count, 0] : [f.Count]));
        Throws(() => get.Invoke(block, two ? [-1, 0] : [-1]));
        if (two)
        {
            Throws(() => get.Invoke(block, [0, f.PerEntry]));
        }
    }

    private static void ByteEntries(StateField f)
    {
        var m = typeof(T).GetMethod(f.Name, [typeof(int)]);
        Assert.True(m is not null && m.ReturnType == typeof(Span<byte>), $"{typeof(T).Name}.{f.Name}(int) missing");
        var at = (SpanAt)Delegate.CreateDelegate(typeof(SpanAt), m);
        var block = new T();
        for (int i = 0; i < f.Count; i++)
        {
            int start = f.Offset + (i * f.EntrySize);
            byte[] sentinel = Sentinel(f.EntrySize, start);
            var span = at(block, i);
            Assert.Equal(f.EntrySize, span.Length);
            sentinel.CopyTo(span);
            OnlyWritten(block, start, sentinel, $"{f.Name}({i})");
            block.Clear();
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => at(block, f.Count).Length);
    }

    private static void Bits(StateField f)
    {
        var get = typeof(T).GetMethod("Get" + f.Name, [typeof(int)]);
        var set = typeof(T).GetMethod("Set" + f.Name, [typeof(int), typeof(bool)]);
        Assert.True(get is not null && set is not null, $"{typeof(T).Name}: Get/Set{f.Name} (bits) missing");
        var block = new T();
        for (int k = 0; k < f.Count; k++)
        {
            set.Invoke(block, [k, true]);
            byte[] expected = [(byte)(1 << (k & 7))];
            OnlyWritten(block, f.Offset + (k >> 3), expected, $"{f.Name} bit {k}");
            Assert.True((bool)get.Invoke(block, [k])!);
            set.Invoke(block, [k, false]);
            Assert.Equal(-1, block.Bytes.IndexOfAnyExcept((byte)0));
        }

        Throws(() => get.Invoke(block, [f.Count]));
    }

    private static void Bytes(StateField f)
    {
        var p = typeof(T).GetProperty(f.Name, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(p is not null && p.PropertyType == typeof(Span<byte>), $"{typeof(T).Name}.{f.Name}: no Span<byte> property");
        var getter = (SpanProperty)Delegate.CreateDelegate(typeof(SpanProperty), p.GetMethod!);
        var block = new T();
        var span = getter(block);
        Assert.Equal(f.Size, span.Length);
        byte[] sentinel = Sentinel(f.Size, f.Offset);
        sentinel.CopyTo(span);
        OnlyWritten(block, f.Offset, sentinel, f.Name);
    }

    private static void Block(StateField f)
    {
        var p = typeof(T).GetProperty(f.Name, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(p is not null && typeof(StateBlock).IsAssignableFrom(p.PropertyType), $"{typeof(T).Name}.{f.Name}: no block view");
        Assert.Equal(f.Type, p.PropertyType.Name);
        byte[] big = new byte[new T().Length + 7];
        var block = (T)Activator.CreateInstance(typeof(T), big, 7)!;
        var inner = (StateBlock)p.GetValue(block)!;
        Assert.Same(big, inner.Storage);
        Assert.Equal(7 + f.Offset, inner.Offset);
        Assert.Equal(f.Size, inner.Length);
        Assert.Same(inner, p.GetValue(block));
        inner.Bytes.Fill(0x5A);
        Assert.Equal(f.Size, block.Bytes.Count((byte)0x5A));
        Assert.Equal(0x5A, block.Bytes[f.Offset]);
        Assert.Equal(0x5A, block.Bytes[f.End - 1]);
    }

    private static void Unused(StateField f)
    {
        Assert.Null(typeof(T).GetProperty(f.Name));
        Assert.DoesNotContain(typeof(T).GetMethods(), m => m.Name == "Get" + f.Name || m.Name == f.Name);
    }

    /// <summary>Asserts that the block holds <paramref name="expected"/> at <paramref name="at"/> and zero everywhere else.</summary>
    private static void OnlyWritten(T block, int at, byte[] expected, string what)
    {
        var b = block.Bytes;
        Assert.True(b.Slice(at, expected.Length).SequenceEqual(expected), $"{typeof(T).Name}.{what}: bytes at 0x{at:X} differ");
        Assert.True(b[..at].IndexOfAnyExcept((byte)0) < 0, $"{typeof(T).Name}.{what}: wrote before 0x{at:X}");
        Assert.True(b[(at + expected.Length)..].IndexOfAnyExcept((byte)0) < 0, $"{typeof(T).Name}.{what}: wrote after 0x{at + expected.Length - 1:X}");
    }

    private static void Throws(Action a)
    {
        var e = Assert.Throws<TargetInvocationException>(a);
        Assert.IsType<ArgumentOutOfRangeException>(e.InnerException);
    }

    /// <summary>Non-zero bytes that differ per position (and give a normal, non-NaN float).</summary>
    private static byte[] Sentinel(int n, int seed)
    {
        byte[] b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = (byte)(0x41 + ((seed + (i * 7)) % 0x3E));
        }

        return b;
    }

    internal static int TypeSize(string type) => type switch
    {
        "u8" or "i8" => 1,
        "u16" or "i16" => 2,
        "u32" or "i32" or "f32" => 4,
        "u64" or "i64" => 8,
        _ => throw new ArgumentException(type),
    };

    private static Type ClrType(string type) => type switch
    {
        "u8" => typeof(byte),
        "i8" => typeof(sbyte),
        "u16" => typeof(ushort),
        "i16" => typeof(short),
        "u32" => typeof(uint),
        "i32" => typeof(int),
        "u64" => typeof(ulong),
        "i64" => typeof(long),
        "f32" => typeof(float),
        _ => throw new ArgumentException(type),
    };

    private static object FromBytes(string type, ReadOnlySpan<byte> b) => type switch
    {
        "u8" => (object)b[0],
        "i8" => (object)(sbyte)b[0],
        "u16" => (object)BinaryPrimitives.ReadUInt16LittleEndian(b),
        "i16" => (object)BinaryPrimitives.ReadInt16LittleEndian(b),
        "u32" => (object)BinaryPrimitives.ReadUInt32LittleEndian(b),
        "i32" => (object)BinaryPrimitives.ReadInt32LittleEndian(b),
        "u64" => (object)BinaryPrimitives.ReadUInt64LittleEndian(b),
        "i64" => (object)BinaryPrimitives.ReadInt64LittleEndian(b),
        "f32" => (object)BinaryPrimitives.ReadSingleLittleEndian(b),
        _ => throw new ArgumentException(type),
    };
}
