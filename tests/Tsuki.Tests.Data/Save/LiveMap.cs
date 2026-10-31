using System.Buffers.Binary;
using System.Text.Json;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Data.Save;

/// <summary>
/// Maps the RAM windows of the save oracle (<c>LIVE</c> in tools/oracle/save.py) to the members of
/// <see cref="AdvLiveState"/>: <see cref="Set"/> loads a window, <see cref="Get"/> reads it back as the PS4 lays it out.
/// </summary>
internal static class LiveMap
{
    /// <summary>The window names, in the oracle's order.</summary>
    public static readonly string[] Names =
    [
        "script_number", "text_block", "text_line", "text_block2", "text_line2", "speaker", "var_1654208", "var_165420a",
        "bgm_track", "bgm_volume", "loop_se_ids", "se_vol_a", "se_vol_b", "se_arr_c", "se_arr_d", "se_arr_e", "channels",
        "window_164fd00", "window_164fd40", "window_164fd60", "message_text", "state_174dab0", "var_174d940", "scene_word",
        "scene", "committed", "f", "voice_id", "var_16541fc", "var_1654200", "var_16541ec", "var_16541f0", "var_16541e4",
        "var_16541d8", "var_16541f4", "restored_flag", "var_174de00", "redisplay_length", "var_86aa24", "text_fade",
        "word_1750646", "var_1653e24", "wait_flags", "wait_flags70", "word_1653e08", "redisplay_text", "redisplay_copy",
        "config", "scan_mode",
    ];

    /// <summary>Builds a state from the windows of a case.</summary>
    public static AdvLiveState Build(JsonElement windows)
    {
        var live = new AdvLiveState();
        foreach (string name in Names)
        {
            Set(live, name, Convert.FromHexString(windows.GetProperty(name).GetString()!));
        }

        return live;
    }

    /// <summary>The windows that differ between <paramref name="live"/> and the expectation (<paramref name="before"/> overlaid with <paramref name="changed"/>).</summary>
    public static List<string> Mismatches(AdvLiveState live, JsonElement before, JsonElement changed)
    {
        var bad = new List<string>();
        foreach (string name in Names)
        {
            string want = changed.TryGetProperty(name, out JsonElement c) ? c.GetString()! : before.GetProperty(name).GetString()!;
            string got = Convert.ToHexStringLower(Get(live, name));
            if (want != got)
            {
                bad.Add(name);
            }
        }

        return bad;
    }

    /// <summary>Loads window <paramref name="name"/>.</summary>
    public static void Set(AdvLiveState s, string name, byte[] b)
    {
        switch (name)
        {
            case "script_number": s.ScriptNumber = U16(b); break;
            case "text_block": s.TextBlock = I32(b); break;
            case "text_line": s.TextLine = I32(b); break;
            case "text_block2": s.TextBlock2 = I32(b); break;
            case "text_line2": s.TextLine2 = I32(b); break;
            case "speaker": s.Speaker = U16(b); break;
            case "var_1654208": s.Var1654208 = U16(b); break;
            case "var_165420a": s.Var165420A = U16(b); break;
            case "bgm_track": s.BgmTrack = (short)U16(b); break;
            case "bgm_volume": s.BgmVolume = (short)U16(b); break;
            case "loop_se_ids": For(b, 2, i => s.LoopSeIds[i] = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 2))); break;
            case "se_vol_a": For(b, 2, i => s.SeVolA[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i * 2))); break;
            case "se_vol_b": For(b, 2, i => s.SeVolB[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i * 2))); break;
            case "se_arr_c": For(b, 2, i => s.SeArrC[i] = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 2))); break;
            case "se_arr_d": For(b, 2, i => s.SeArrD[i] = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 2))); break;
            case "se_arr_e": b.CopyTo(s.SeArrE, 0); break;
            case "channels": b.CopyTo(s.Channels, 0); break;
            case "window_164fd00": b.CopyTo(s.Window164FD00, 0); break;
            case "window_164fd40": b.CopyTo(s.Window164FD40, 0); break;
            case "window_164fd60": b.CopyTo(s.Window164FD60, 0); break;
            case "message_text": b.CopyTo(s.MessageText, 0); break;
            case "state_174dab0": b.CopyTo(s.State174DAB0, 0); break;
            case "var_174d940": s.Var174D940 = U16(b); break;
            case "scene_word": s.SceneWord = U16(b); break;
            case "scene": s.Scene.CopyFrom(b); break;
            case "committed": s.Committed.CopyFrom(b); break;
            case "f": b.CopyTo(s.Vars.Bytes); break;
            case "voice_id": s.VoiceId = I32(b); break;
            case "var_16541fc": s.Var16541FC = I32(b); break;
            case "var_1654200": s.Var1654200 = I32(b); break;
            case "var_16541ec": s.Var16541EC = I32(b); break;
            case "var_16541f0": s.Var16541F0 = I32(b); break;
            case "var_16541e4": s.Var16541E4 = I32(b); break;
            case "var_16541d8": s.Var16541D8 = I32(b); break;
            case "var_16541f4": s.Var16541F4 = b[0]; break;
            case "restored_flag": s.RestoredFromHistory = b[0]; break;
            case "var_174de00": s.Var174DE00 = b[0]; break;
            case "redisplay_length": s.RedisplayLength = U16(b); break;
            case "var_86aa24": s.Var86AA24 = BinaryPrimitives.ReadUInt32LittleEndian(b); break;
            case "text_fade": s.TextFade = BinaryPrimitives.ReadSingleLittleEndian(b); break;
            case "word_1750646": s.Word1750646 = U16(b); break;
            case "var_1653e24": s.Var1653E24 = BinaryPrimitives.ReadUInt32LittleEndian(b); break;
            case "wait_flags": s.WaitFlags = BinaryPrimitives.ReadUInt64LittleEndian(b); break;
            case "wait_flags70": s.WaitFlags70 = BinaryPrimitives.ReadUInt64LittleEndian(b); break;
            case "word_1653e08": s.Word1653E08 = U16(b); break;
            case "redisplay_text": b.CopyTo(s.RedisplayText, 0); break;
            case "redisplay_copy": b.CopyTo(s.RedisplayCopy, 0); break;
            case "config": s.Config.CopyFrom(b); break;
            case "scan_mode": s.ScanMode = b[0]; break;
            default: throw new ArgumentException("unknown window " + name);
        }
    }

    /// <summary>Reads window <paramref name="name"/> in the PS4 layout.</summary>
    public static byte[] Get(AdvLiveState s, string name)
    {
        switch (name)
        {
            case "script_number": return P16(s.ScriptNumber);
            case "text_block": return P32(s.TextBlock);
            case "text_line": return P32(s.TextLine);
            case "text_block2": return P32(s.TextBlock2);
            case "text_line2": return P32(s.TextLine2);
            case "speaker": return P16(s.Speaker);
            case "var_1654208": return P16(s.Var1654208);
            case "var_165420a": return P16(s.Var165420A);
            case "bgm_track": return P16((ushort)s.BgmTrack);
            case "bgm_volume": return P16((ushort)s.BgmVolume);
            case "loop_se_ids": return Words(s.LoopSeIds.Select(v => (ushort)v));
            case "se_vol_a": return Words(s.SeVolA);
            case "se_vol_b": return Words(s.SeVolB);
            case "se_arr_c": return Words(s.SeArrC.Select(v => (ushort)v));
            case "se_arr_d": return Words(s.SeArrD.Select(v => (ushort)v));
            case "se_arr_e": return s.SeArrE;
            case "channels": return s.Channels;
            case "window_164fd00": return s.Window164FD00;
            case "window_164fd40": return s.Window164FD40;
            case "window_164fd60": return s.Window164FD60;
            case "message_text": return s.MessageText;
            case "state_174dab0": return s.State174DAB0;
            case "var_174d940": return P16(s.Var174D940);
            case "scene_word": return P16(s.SceneWord);
            case "scene": return s.Scene.Bytes.ToArray();
            case "committed": return s.Committed.Bytes.ToArray();
            case "f": return s.Vars.Bytes[..0x960].ToArray();
            case "voice_id": return P32(s.VoiceId);
            case "var_16541fc": return P32(s.Var16541FC);
            case "var_1654200": return P32(s.Var1654200);
            case "var_16541ec": return P32(s.Var16541EC);
            case "var_16541f0": return P32(s.Var16541F0);
            case "var_16541e4": return P32(s.Var16541E4);
            case "var_16541d8": return P32(s.Var16541D8);
            case "var_16541f4": return [s.Var16541F4];
            case "restored_flag": return [s.RestoredFromHistory];
            case "var_174de00": return [s.Var174DE00];
            case "redisplay_length": return P16(s.RedisplayLength);
            case "var_86aa24": return P32((int)s.Var86AA24);
            case "text_fade": { byte[] r = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(r, s.TextFade); return r; }
            case "word_1750646": return P16(s.Word1750646);
            case "var_1653e24": return P32((int)s.Var1653E24);
            case "wait_flags": { byte[] r = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(r, s.WaitFlags); return r; }
            case "wait_flags70": { byte[] r = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(r, s.WaitFlags70); return r; }
            case "word_1653e08": return P16(s.Word1653E08);
            case "redisplay_text": return s.RedisplayText;
            case "redisplay_copy": return s.RedisplayCopy;
            case "config": return s.Config.Bytes.ToArray();
            case "scan_mode": return [s.ScanMode];
            default: throw new ArgumentException("unknown window " + name);
        }
    }

    private static void For(byte[] b, int width, Action<int> set)
    {
        for (int i = 0; i < b.Length / width; i++)
        {
            set(i);
        }
    }

    private static ushort U16(byte[] b) => BinaryPrimitives.ReadUInt16LittleEndian(b);

    private static int I32(byte[] b) => BinaryPrimitives.ReadInt32LittleEndian(b);

    private static byte[] P16(ushort v) => [(byte)v, (byte)(v >> 8)];

    private static byte[] P32(int v) => BitConverter.GetBytes(v);

    private static byte[] Words(IEnumerable<ushort> values)
    {
        var ms = new List<byte>();
        foreach (ushort v in values)
        {
            ms.Add((byte)v);
            ms.Add((byte)(v >> 8));
        }

        return [.. ms];
    }
}
