using System.Buffers.Binary;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Synthetic.Contracts;

/// <summary>
/// Offsets, sizes and aliasing of the state blocks checked against numbers typed in from the format
/// documents (not from the generator's sources), so a wrong field map or a wrong generator shows up
/// here even when the map tiles (ARCHITECTURE 3.2, PACE 7.1 L-1).
/// </summary>
public sealed class StateBlockLayoutTests
{
    [Fact]
    public void SizesOfArchitecture32()
    {
        Assert.Equal(0x1A08, SceneState.Size);
        Assert.Equal(0x3A10, HistoryRecord.Size);
        Assert.Equal(0x34, SoundChannelRecord.Size);
        Assert.Equal(0x140, ConfigBlock.Size);
        Assert.Equal(0x18AC, SystemBlock.Size);
        Assert.Equal(1200, Vars.FCount);
        Assert.Equal(1400, Vars.ZCount);
        Assert.Equal(2000, Vars.GCount);
        Assert.Equal(0x960 + 0xAF0 + 0xFA0, Vars.Size);
    }

    [Fact]
    public void SceneStateOffsetsOfLayers3()
    {
        var s = new SceneState();
        s.SetPic(1, 0x1234);                       // +0x004 i16, layer 1 at +0x006
        Assert.Equal(0x1234, U16(s, 0x006));
        s.Clear();
        s.SetAlpha(39, 255);                       // +0x20C i16
        Assert.Equal(255, U16(s, 0x20C + 78));
        s.Clear();
        s.SetRot(2, -2);                           // +0x350 i64
        Assert.Equal(-2L, BinaryPrimitives.ReadInt64LittleEndian(s.Bytes[(0x350 + 16)..]));
        s.Clear();
        s.SetParent(39, -1);                       // +0xA08 i32
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(s.Bytes[(0xA08 + 156)..]));
        s.Clear();
        s.SetCrop(1, 3, 0x0BCD);                   // +0xAF8 4 x i16
        Assert.Equal(0x0BCD, U16(s, 0xAF8 + 8 + 6));
        s.Clear();
        s.SetShakePar(2, 11, 0x0777);              // +0x1250 12 x i16 (24 bytes per layer)
        Assert.Equal(0x0777, U16(s, 0x1250 + 48 + 22));
        s.Clear();
        s.SetZoomPivot(3, -1);                     // +0x940 i8
        Assert.Equal(0xFF, s.Bytes[0x943]);
        s.Clear();
        s.SetMotionFrames(5, 0x0102030405060708);  // motion record +0xD00, 24 bytes; frames at +8
        Assert.Equal(0x0102030405060708L, BinaryPrimitives.ReadInt64LittleEndian(s.Bytes[(0xD00 + 120 + 8)..]));
        Assert.Equal(0x08, s.Motion(5)[8]);
        s.Clear();
        s.SetExtColourR(0, 0xAA);                  // ext record +0x16B0, 18 bytes; RGB at +6..+8
        s.SetExtOffsetY(1, -3);
        Assert.Equal(0xAA, s.Bytes[0x16B0 + 6]);
        Assert.Equal(0xFFFD, U16(s, 0x16B0 + 18 + 4));
        s.Clear();
        s.F995Low = 0x7F;                          // adv_state 2.6 globals
        s.TextWindowA = 3;
        s.ScreenEffect = 2;
        s.ScreenEffectP3 = -5;
        Assert.Equal(0x7F, s.Bytes[0x19A3]);
        Assert.Equal(3, U16(s, 0x1982));
        Assert.Equal(2, U16(s, 0x19FC));
        Assert.Equal(0xFFFB, U16(s, 0x1A04));
    }

    [Fact]
    public void SceneStateDefaultsOfLayers3()
    {
        var s = SceneState.CreateDefault();
        for (int l = 0; l < SceneState.Layers; l++)
        {
            Assert.Equal(-1, s.GetPic(l));
            Assert.Equal(255, s.GetAlpha(l));
            Assert.Equal(1000, s.GetScale(l));
            Assert.Equal(1000, s.GetZoomX(l));
            Assert.Equal(1000, s.GetZoomY(l));
            Assert.Equal(-1, s.GetMask(l));
            Assert.Equal(-1, s.GetSttr(l));
            Assert.Equal(-1, s.GetParent(l));
            Assert.Equal(0, s.GetZ(l));
        }

        Assert.Equal(0, s.ScenePic);
        s.SetPic(4, 9);
        s.SetMotionId(4, 3);
        s.ResetLayer(4);
        Assert.True(s.ContentEquals(SceneState.CreateDefault()));
    }

    [Fact]
    public void HistoryRecordOffsetsOfAdvState21()
    {
        var r = new HistoryRecord();
        r.Kind = 9;                                    // +0x24 u16
        r.VoiceId = -1;                                // +0x3630 i32
        r.Script = 2;                                  // +0x18 u16
        r.CmdOffset = 0x1122334455667788;              // +0x08 u64
        Assert.Equal(9, U16(r, 0x24));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(r.Bytes[0x3630..]));
        Assert.Equal(2, U16(r, 0x18));
        Assert.Equal(0x88, r.Bytes[0x08]);

        r.SceneState.SetPic(0, 0x0405);                // scene_state at +0x58
        r.SceneCommitted.SetPic(0, 0x0607);            // committed copy at +0x1A60
        Assert.Equal(0x0405, U16(r, 0x58 + 4));
        Assert.Equal(0x0607, U16(r, 0x1A60 + 4));
        Assert.Equal(0xFA, r.MessageText.Length);      // +0x38FE, 0xFA bytes
        r.MessageText[0] = (byte)'A';
        Assert.Equal((byte)'A', r.Bytes[0x38FE]);
        Assert.Equal(0x18, r.Channel(19).Length);      // +0x36D0, 20 x 0x18
        r.Channel(19)[0] = 1;
        Assert.Equal(1, r.Bytes[0x36D0 + (19 * 0x18)]);

        // F lists: 39 u16, 43 bits, 276 s8; slot 32 of the s8 list is F823 at +0x3521 (adv_state 2.1)
        Assert.Equal(39, HistoryRecord.FU16Indices.Length);
        Assert.Equal(43, HistoryRecord.FBoolIndices.Length);
        Assert.Equal(276, HistoryRecord.FS8Indices.Length);
        Assert.Equal(823, HistoryRecord.FS8Indices[32]);
        r.SetFS8(32, -2);
        Assert.Equal(0xFE, r.Bytes[0x3521]);
        Assert.Contains(991, HistoryRecord.FU16Indices);    // F991 in the u16 and the bool list
        Assert.Contains(991, HistoryRecord.FBoolIndices);
        Assert.Contains(967, HistoryRecord.FS8Indices);     // F967 in the u16 and the s8 list
        Assert.Contains(1118, HistoryRecord.FBoolIndices);
        int k = HistoryRecord.FBoolIndices.IndexOf(1118);
        r.SetFBoolBit(k, true);
        Assert.Equal(1 << (k & 7), r.Bytes[0x34FA + (k >> 3)]);
    }

    [Fact]
    public void SoundChannelRecordOffsetsOfSoundRuntime22()
    {
        var c = new SoundChannelRecord { State = 2, VolumeClass = 1, Entry = 40, Volume = 100f, PanStep = -0.5f, Character = 19 };
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(c.Bytes));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(c.Bytes[0x04..]));
        Assert.Equal(40, U16(c, 0x0A));
        Assert.Equal(100f, BinaryPrimitives.ReadSingleLittleEndian(c.Bytes[0x0C..]));
        Assert.Equal(-0.5f, BinaryPrimitives.ReadSingleLittleEndian(c.Bytes[0x20..]));
        Assert.Equal(19, c.Bytes[0x30]);
        Assert.Equal(0xDD86A0, SoundChannelRecord.BgmRecordsVa);
        Assert.Equal(0xDD8B20, SoundChannelRecord.VoiceRecordsVa);
        Assert.Equal(0xDD8710, SoundChannelRecord.SeRecordsVa);
    }

    [Fact]
    public void ConfigAndSystemOffsetsOfAdvState75And92()
    {
        var c = ConfigBlock.CreateBootDefaults();
        Assert.Equal(40, c.MusicVolume);               // +0x14
        Assert.Equal(100, c.PlayAllVoices);            // +0x16
        Assert.Equal(40, c.SfxVolume);                 // +0x18
        Assert.Equal(10, c.SystemSeVolume);            // +0x1A
        Assert.Equal(35, c.UnseenTextSpeed);           // +0x20
        Assert.Equal(15, c.SeenTextSpeed);             // +0x22
        Assert.Equal(-1, c.Language);                  // +0x24, boot 0xFFFF = system language
        Assert.Equal(2200, c.AutoModeWait);            // +0x2E
        Assert.Equal(0x3FFFFFFFu, c.VoiceEnableBits);  // +0x3C
        Assert.Equal(3000, c.AutoModePageSpeed);       // +0x9E
        Assert.Equal(19, ConfigBlock.CharacterVoiceVolumeCount);
        Assert.Equal(50, c.GetCharacterVoiceVolume(18));
        Assert.Equal(50, U16(c, 0x76 + 36));
        Assert.Equal(0x1C, c.BootRecords.Length);      // +0x5A

        var s = new SystemBlock { Magic = SystemBlock.MagicValue, LatestSlot = 139 };
        s.Config.MusicVolume = 40;                     // config at +0x04
        s.SetG(499, 7);                                // G[0..499] at +0x144
        s.SetReadmax(4019, 0x55);                      // readmax[4020] at +0x738
        s.SetPictureSeen(3199, true);                  // unlock bits at +0x52C
        s.SetBgmHeard(0x207, true);
        Assert.Equal(0x2AF8, U16(s, 0));
        Assert.Equal(40, U16(s, 0x04 + 0x14));
        Assert.Equal(7, U16(s, 0x144 + 998));
        Assert.Equal(0x55, s.Bytes[0x738 + 4019]);
        Assert.Equal(139, U16(s, 0x16EE));
        Assert.Equal(0x80, s.Bytes[0x52C + 399]);
        Assert.Equal(1 << ((0xC80 + 0x207) & 7), s.Bytes[0x52C + ((0xC80 + 0x207) >> 3)]);
        Assert.True(s.IsPictureSeen(3199));
        Assert.False(s.IsPictureSeen(-1));
        Assert.True(s.IsBgmHeard(0x207));
        Assert.False(s.IsBgmHeard(0x208));
        s.SetChoiceTaken(5, 0b100);                    // choice_taken bytes at +0x16F4
        Assert.Equal(0b100, s.Bytes[0x16F4 + 5]);
        Assert.True(s.IsChoiceTaken(5, 2));
        Assert.False(s.IsChoiceTaken(5, 1));
    }

    [Fact]
    public void VarsAliasingOfVmVariables21And22()
    {
        var v = new Vars();
        Assert.Equal(0x164B1D0, Vars.FVa);
        Assert.Equal(0x164BB30, Vars.ZVa);
        Assert.Equal(0x164C620, Vars.GVa);
        Assert.Equal(Vars.ZVa, Vars.VaOfWord(Vars.ZWord));
        Assert.Equal(Vars.GVa, Vars.VaOfWord(Vars.GWord));

        // contiguous arrays: F[1200 + i] = Z[i], F[2600 + i] = Z[1400 + i] = G[i]
        v.SetF(1200, 11);
        Assert.Equal(11, v.GetZ(0));
        v.SetG(0, 12);
        Assert.Equal(12, v.GetF(2600));
        Assert.Equal(12, v.GetZ(1400));
        v.SetG(-1, 13);                                // G-1 is Z[1399]
        Assert.Equal(13, v.GetZ(1399));
        Assert.Equal(2 * 2600, Vars.Fields[2].Offset);

        // operand index arithmetic (vm_variables.md 2.2 examples)
        Assert.Equal(Vars.ZWord, Vars.FWordOfOperand(1200));              // F1200 -> Z[0]
        Assert.Equal(Vars.GWord, Vars.FWordOfOperand(2600));              // F2600 -> G[0]
        Assert.Equal(-3, Vars.FWordOfOperand(-3));                        // F-3 -> 0x164B1CA
        Assert.Equal(0x164B1CA, Vars.VaOfWord(Vars.FWordOfOperand(-3)));
        Assert.Equal(0x163B1D0, Vars.VaOfWord(Vars.FWordOfOperand(32768))); // n wraps to -32768
        Assert.Equal(0x164B1CE, Vars.VaOfWord(Vars.FWordOfOperand(65535)));
        Assert.Equal(Vars.GWord + 1864, Vars.FWordOfOperand(70000));      // F70000 -> G[1864]
        Assert.Equal(0x164D5C0, Vars.VaOfWord(Vars.GWordOfOperand(2000))); // G2000 -> 0x164D5C0
        Assert.False(Vars.InBlock(Vars.GWordOfOperand(2000)));
        Assert.Equal(Vars.ZWord + 1399, Vars.GWordOfOperand(-1));         // G-1 -> Z[1399]
        Assert.False(Vars.InBlock(Vars.FWordOfOperand(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => v.GetF(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => v.GetG(2000));
        Assert.True(Vars.TryWordOfVa(0x164C620, out int w) && w == Vars.GWord);
        Assert.False(Vars.TryWordOfVa(0x164C621, out _));
        Assert.False(Vars.TryWordOfVa(0x164D5C0, out _));

        // C<n> = F[F823 + n] (vm_variables.md 2.3): during message k, C847 = F[847 + k]
        Assert.Equal(823, Vars.MessageIndexVar);
        v.SetF(823, 3);
        v.SetC(847, 9);
        Assert.Equal(9, v.GetF(850));
        Assert.Equal(850, v.CWord(847));
        v.SetF(823, 0);
        Assert.Equal(847, v.CWord(847));
        Assert.Equal(846, Vars.CWordOfOperand(0xFFFF, 847));             // s16(F823) = -1
        Assert.Equal(-1, Vars.CWordOfOperand(0, 65535));                   // s16(n) = -1
    }

    private static int U16(StateBlock b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.Bytes[offset..]);
}
