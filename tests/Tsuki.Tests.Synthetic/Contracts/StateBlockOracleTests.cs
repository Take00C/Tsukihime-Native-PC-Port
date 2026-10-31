using System.Security.Cryptography;
using System.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Synthetic.Contracts;

/// <summary>
/// The state blocks against values computed by the Python oracle (tools/tsuki) on the same bytes.
/// The expected values were printed by the commands quoted in each test (run with
/// PYTHONPATH=tools and .venv/Scripts/python.exe; docs/status/wave2/L1.md lists them). Inputs are
/// synthetic byte patterns, so no game data is needed except where noted.
/// </summary>
public sealed class StateBlockOracleTests
{
    /// <summary>Byte i of a test pattern: (i * mul + add) &amp; 0xFF, as <c>pat(n, mul, add)</c> in the Python commands.</summary>
    private static byte[] Pattern(int n, int mul, int add)
    {
        byte[] b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = (byte)((i * mul) + add);
        }

        return b;
    }

    [Fact]
    public void SceneStateDefaultEqualsTheOracle()
    {
        // hashlib.sha256(bytes(misc_layer_state_and_motions.SceneState().b)).hexdigest()
        Assert.Equal(
            "00e83ab93cc58a391d49cb2dfeb7473f040fa7a95a8904662bd721018a4eb9ac",
            Convert.ToHexStringLower(SHA256.HashData(SceneState.CreateDefault().Bytes)));
    }

    [Fact]
    public void ConfigBootDefaultsEqualTheEbootDataImage()
    {
        // hashlib.sha256(misc_adv_system_state.read_static_config()).hexdigest(): the 0x140 bytes at
        // VA 0x66C578 of the retail eboot (game data needed only to produce this number).
        Assert.Equal(
            "d437641bb05ae397d44a3aef054b4899a71b9e080adbe6f45a7f3169661a079f",
            Convert.ToHexStringLower(SHA256.HashData(ConfigBlock.CreateBootDefaults().Bytes)));
    }

    [Fact]
    public void SoundChannelRecordEqualsRecPack()
    {
        // misc_audio_runtime_semantics.Rec(state=2, kind=1, w8=1, idx=0x1234, vol=F32(40.0), vol_to=F32(0.0),
        //   vol_rate=F32(0.6666667), pan=F32(-250.0), pan_to=F32(1000.0), pan_rate=F32(12.5), p3=F32(1.0),
        //   p3_to=F32(2.0), p3_rate=F32(0.2), char=19).pack().hex()
        var c = new SoundChannelRecord
        {
            State = 2,
            VolumeClass = 1,
            Started = 1,
            Entry = 0x1234,
            Volume = 40f,
            VolumeTarget = 0f,
            VolumeStep = 0.6666667f,
            Pan = -250f,
            PanTarget = 1000f,
            PanStep = 12.5f,
            Ramp3 = 1f,
            Ramp3Target = 2f,
            Ramp3Step = 0.2f,
            Character = 19,
        };
        Assert.Equal(
            "0200000001000000010034120000204200000000abaa2a3f00007ac300007a44000048410000803f00000040cdcc4c3e13000000",
            Convert.ToHexStringLower(c.Bytes));
    }

    [Fact]
    public void SceneStateReadsLikeTheOracle()
    {
        // s = misc_layer_state_and_motions.SceneState(pat(0x1A08, 37, 11)); d = s.layer_dict(7)
        var s = new SceneState(Pattern(SceneState.Size, 37, 11));
        Assert.Equal(-13659, s.GetPic(7));
        Assert.Equal(194, s.GetFlags(7));
        Assert.Equal(-118, s.GetFxMode(7));
        Assert.Equal(-11603, s.GetX(7));
        Assert.Equal(-3379, s.GetAlpha(7));
        Assert.Equal(90, s.GetState(7));
        Assert.Equal(-5291329034398082893L, s.GetRot(7));
        Assert.Equal(-1366727617, s.GetParent(7));
        Assert.Equal(new[] { 8443, 27205, -19313, -295 }, Enumerable.Range(0, 4).Select(i => (int)s.GetCrop(7, i)));
        Assert.Equal(20009, s.GetA16(7));
        Assert.Equal(new[] { 209, 246 }, Enumerable.Range(0, 2).Select(i => (int)s.GetShakeKind(7, i)));
        Assert.Equal(
            new[] { 2275, 21037, -25481, -6463, 12299, 31317, -15201, 3817, 22579, -23939, -4921, 13841 },
            Enumerable.Range(0, 12).Select(i => (int)s.GetShakePar(7, i)));
        Assert.Equal(182, s.GetStnsB(7));
        Assert.Equal(141, s.GetU19D3(7));
        Assert.Equal("53789dc2e70c31567ba0c5ea0f34597ea3c8ed12375c81a6", Convert.ToHexStringLower(s.Motion(7)));
        Assert.Equal("b1d6fb20456a8fb4d9fe23486d92b7dc0126", Convert.ToHexStringLower(s.Ext(7)));

        // struct.unpack_from("<h", s.b, 0x19FC), ("<Q", 0x1990), ("<b", 0x19AA)
        Assert.Equal(-25481, s.ScreenEffect);
        Assert.Equal(16049021952817692891UL, s.Reset1990);
        Assert.Equal(-99, s.Byte19AA);
    }

    [Fact]
    public void HistoryRecordReadsLikeTheOracle()
    {
        // h = misc_adv_system_state.parse_history_record(pat(0x3A10, 53, 7))
        var r = new HistoryRecord(Pattern(HistoryRecord.Size, 53, 7));
        Assert.Equal(-1502528505, r.RingIndex);
        Assert.Equal(2051346651, r.Serial);
        Assert.Equal(2516870640860521647UL, r.CmdOffset);
        Assert.Equal(14597679509131070551UL, r.ResumeOffset);
        Assert.Equal(13567, r.Script);
        Assert.Equal(-24983, r.TextBlock);
        Assert.Equal(2259, r.TextLine);
        Assert.Equal(61, r.Speaker);
        Assert.Equal(114, r.Var1654208);
        Assert.Equal(45179, r.Kind);
        Assert.Equal(6885, r.BgmTrack);
        Assert.Equal(13825, r.SceneWord);
        Assert.Equal(1231, r.Var174D940);
        Assert.Equal(-1772016393, r.VoiceId);
        Assert.Equal(203, r.VoiceCharacter);
        Assert.Equal(0x6A35, r.EffectActiveBits);   // b'5j'
        Assert.Equal(0x3E09, r.Flags363A);          // b'\t>'
        Assert.Equal(-22413, r.BgmVolume);
        Assert.Equal(4829, r.Config04);
        Assert.Equal(71, r.Var3640);
        Assert.Equal(124, r.Var165420A);
    }

    [Fact]
    public void HistoryFValuesEqualTheOracle()
    {
        // fv = misc_adv_system_state.history_f_values(pat(0x3A10, 53, 7), lists=(HISTORY_U16, HISTORY_BOOL, HISTORY_S8));
        // hashlib.sha256(";".join(f"{i}={v}" for i, v in sorted(fv.items())).encode()).hexdigest() -> 356 values, 24 bits set
        var r = new HistoryRecord(Pattern(HistoryRecord.Size, 53, 7));
        var f = new SortedDictionary<int, int>();
        for (int k = 0; k < HistoryRecord.FU16Indices.Length; k++)
        {
            f[HistoryRecord.FU16Indices[k]] = r.GetFU16(k);
        }

        for (int k = 0; k < HistoryRecord.FS8Indices.Length; k++)
        {
            int idx = HistoryRecord.FS8Indices[k];
            if (idx is >= 0 and <= 1200)
            {
                f[idx] = (ushort)r.GetFS8(k);
            }
        }

        for (int k = 0; k < HistoryRecord.FBoolIndices.Length; k++)
        {
            int idx = HistoryRecord.FBoolIndices[k];
            if (idx is >= 0 and <= 1200)
            {
                f[idx] = r.GetFBoolBit(k) ? 1 : 0;
            }
        }

        Assert.Equal(356, f.Count);
        Assert.Equal(24, HistoryRecord.FBoolIndices.Count(i => f[i] == 1));
        Assert.Equal(65500, f[823]);
        Assert.Equal(16, f[967]);
        string canon = string.Join(";", f.Select(kv => $"{kv.Key}={kv.Value}"));
        Assert.Equal(
            "fb9f8feb1ff1102db391cfaf4a7c6fba6789957f2c31ade348b000a605eb7d14",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(canon))));
    }
}
