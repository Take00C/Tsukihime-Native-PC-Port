using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Save;

/// <summary>
/// The "RAM side" of the history record: every live engine variable that <c>History_Push</c> (VA 0x7AF90) copies
/// into a record and <c>History_Restore</c> (VA 0x7C6D0) copies back (docs/formats/adv_state.md 2.1, 3). The PS4 code
/// reads and writes these at fixed addresses; the port keeps them in one object so that the history ring, the
/// interpreter (package K3) and the save code agree on what a record means. The interpreter either keeps its
/// own variables in this object, or copies them in before a push and out after a restore.
/// </summary>
/// <remarks>
/// Names follow adv_state.md; fields the document does not explain are named after their PS4 address (the
/// <c>Word…</c> / <c>Var…</c> members). Each member says which PS4 variable it is, so a port can map it. Variables
/// that exist only because a push or restore reads or writes them (flags of the wait model, the re-display
/// buffers) are kept so that the oracle comparison covers the whole restore; the interpreter may ignore them.
/// </remarks>
public sealed class AdvLiveState
{
    /// <summary>Sound channels in the history record (loop SE ids, volume arrays, channel table).</summary>
    public const int SoundChannels = 20;

    /// <summary>Bytes of one entry of the channel table (RAM 0x16543F0).</summary>
    public const int ChannelEntrySize = 0x18;

    /// <summary>Bytes of the current-message buffer (RAM 0x174D9B0, record +0x38FE): 0xF8 text bytes and two spare.</summary>
    public const int MessageTextSize = 0xFA;

    /// <summary>Bytes of the statement-text buffers of the kind-0 re-display (RAM 0x16537E0, 0x1653AF0).</summary>
    public const int RedisplayBufferSize = 0x300;

    /// <summary>Creates a state with new zeroed blocks (config at its boot defaults).</summary>
    public AdvLiveState()
        : this(new Vars(), ConfigBlock.CreateBootDefaults(), new SceneState(), new SceneState())
    {
    }

    /// <summary>Creates a state over existing blocks (the interpreter's variables, the system block's config, the stage's scene copies).</summary>
    public AdvLiveState(Vars vars, ConfigBlock config, SceneState scene, SceneState committed)
    {
        Vars = vars ?? throw new ArgumentNullException(nameof(vars));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        Committed = committed ?? throw new ArgumentNullException(nameof(committed));
        ResetSound();
    }

    /// <summary>
    /// The sound-array resets of Slot_Load (VA 0xA2DD8..0xA2EB0), also the state of a new game: no loop SEs (id -1), pitch word
    /// 100 on every channel, the other sound arrays and the channel table cleared (the table's entries get -1 in the fields the
    /// engine uses as "none"), no BGM.
    /// </summary>
    public void ResetSound()
    {
        for (int i = 0; i < SoundChannels; i++)
        {
            LoopSeIds[i] = -1;
            SeVolA[i] = 100;
            SeVolB[i] = 0;
            SeArrC[i] = 0;
            SeArrD[i] = 0;
            SeArrE[i] = 0;
            Span<byte> e = Channels.AsSpan(i * ChannelEntrySize, ChannelEntrySize);
            e.Slice(0, 2).Clear();
            e[2] = 0;
            e[4] = 0xFF;
            e[5] = 0xFF;
            e.Slice(8, 8).Fill(0xFF);
            e.Slice(0xD, 8).Fill(0xFF);
            e[0x15] = 0;
        }

        BgmTrack = -1;
    }

    // -- blocks ------------------------------------------------------------------------------------------------

    /// <summary>F / Z / G (RAM 0x164B1D0). Push reads F; restore writes F (lists), a slot load clears F and Z.</summary>
    public Vars Vars { get; }

    /// <summary>The live config (RAM 0x66C578; the system block's config). Push reads +0x04, restore writes it when +0x4C is set.</summary>
    public ConfigBlock Config { get; }

    /// <summary>The script-side scene state (RAM 0x16500C0).</summary>
    public SceneState Scene { get; }

    /// <summary>The committed scene copy (RAM 0x1651AC8).</summary>
    public SceneState Committed { get; }

    // -- script position and text --------------------------------------------------------------------------------

    /// <summary>Current script number (RAM 0x86AA18). A restore of a record of another script sets it and asks for a reload.</summary>
    public ushort ScriptNumber { get; set; }

    /// <summary>
    /// script_ptr (RAM 0x16543B0) as an offset from the start of the decoded script. Restore sets it to the record's
    /// <c>CmdOffset</c> (the command runs again), or to <c>ResumeOffset</c> for a rewind to a message.
    /// </summary>
    public ulong ScriptOffset { get; set; }

    /// <summary>Message block of the current text (RAM 0x1654344 as an i32; a record keeps the low word).</summary>
    public int TextBlock { get; set; }

    /// <summary>Message line of the current text (RAM 0x1654348).</summary>
    public int TextLine { get; set; }

    /// <summary>Copy of <see cref="TextBlock"/> written by the kind-0 re-display (RAM 0x165434C).</summary>
    public int TextBlock2 { get; set; }

    /// <summary>Copy of <see cref="TextLine"/> written by the kind-0 re-display (RAM 0x1654350).</summary>
    public int TextLine2 { get; set; }

    /// <summary>Speaker / name-colour index (RAM 0x164FCF6; a record keeps the low byte, restore sign-extends it).</summary>
    public ushort Speaker { get; set; }

    /// <summary>RAM 0x1654208 (a record keeps the low byte).</summary>
    public ushort Var1654208 { get; set; }

    /// <summary>RAM 0x165420A (record +0x3641).</summary>
    public ushort Var165420A { get; set; }

    /// <summary>The current message text buffer (RAM 0x174D9B0, <see cref="MessageTextSize"/> bytes).</summary>
    public byte[] MessageText { get; } = new byte[MessageTextSize];

    /// <summary>RAM 0x174DAB0, 0x40 bytes (record +0x3468; meaning not traced).</summary>
    public byte[] State174DAB0 { get; } = new byte[0x40];

    /// <summary>RAM 0x174D940 (record +0x34A8; meaning not traced).</summary>
    public ushort Var174D940 { get; set; }

    /// <summary>RAM 0x16500BE (record +0x52).</summary>
    public ushort SceneWord { get; set; }

    // -- sound -------------------------------------------------------------------------------------------------

    /// <summary>BGM track (RAM 0x16545D0), -1 = none.</summary>
    public short BgmTrack { get; set; } = -1;

    /// <summary>BGM volume word (RAM 0x1654238).</summary>
    public short BgmVolume { get; set; }

    /// <summary>Loop SE id per channel (RAM 0x1654240, i16[20]); -1 = none.</summary>
    public short[] LoopSeIds { get; } = new short[SoundChannels];

    /// <summary>Per-channel word 0x1654270[i] (record keeps the low byte; the restore passes it as the SE pitch).</summary>
    public ushort[] SeVolA { get; } = new ushort[SoundChannels];

    /// <summary>Per-channel word 0x16542A0[i] (record keeps the low byte; a loop SE restarts only while it is above 0).</summary>
    public ushort[] SeVolB { get; } = new ushort[SoundChannels];

    /// <summary>i16[20] at RAM 0x16542D0 (passed to 0xA9E0 when a loop SE restarts).</summary>
    public short[] SeArrC { get; } = new short[SoundChannels];

    /// <summary>i16[20] at RAM 0x1654300 (passed to 0xAA50 when a loop SE restarts).</summary>
    public short[] SeArrD { get; } = new short[SoundChannels];

    /// <summary>u8[20] at RAM 0x1654330 (the volume of a restarted loop SE; see <see cref="AdvHistory.Ps4StaleSeBytes"/>).</summary>
    public byte[] SeArrE { get; } = new byte[SoundChannels];

    /// <summary>The channel table (RAM 0x16543F0, 20 x 0x18 bytes); push and restore move only some bytes of each entry.</summary>
    public byte[] Channels { get; } = new byte[SoundChannels * ChannelEntrySize];

    /// <summary>The voice of the line (RAM 0x1654204; push stores -1 in the record, voice play stores the line's voice there).</summary>
    public int VoiceId { get; set; } = -1;

    /// <summary>RAM 0x16541FC (written by every restore from the record's voice id; -1 after a kind-0 re-display).</summary>
    public int Var16541FC { get; set; } = -1;

    /// <summary>RAM 0x1654200 (same rule as <see cref="Var16541FC"/>).</summary>
    public int Var1654200 { get; set; } = -1;

    // -- message window ---------------------------------------------------------------------------------------

    /// <summary>Window state (RAM 0x164FD00, 0x32 bytes).</summary>
    public byte[] Window164FD00 { get; } = new byte[0x32];

    /// <summary>Name-plate state (RAM 0x164FD40, 0x1C bytes); a restore of a non-message record clears byte 0.</summary>
    public byte[] Window164FD40 { get; } = new byte[0x1C];

    /// <summary>Name-plate state after a kind-0 re-display moved it on (RAM 0x164FD60, 0x1C bytes).</summary>
    public byte[] Window164FD60 { get; } = new byte[0x1C];

    // -- flags written by a restore (wait model, re-display); mapped by the interpreter ------------------------------

    /// <summary>RAM 0x16541EC: 1 when the name plate has text (written together with <see cref="Var16541F0"/>).</summary>
    public int Var16541EC { get; set; }

    /// <summary>RAM 0x16541F0 (see <see cref="Var16541EC"/>).</summary>
    public int Var16541F0 { get; set; }

    /// <summary>RAM 0x16541E4 (restore: sign-extended scene byte +0x19A4).</summary>
    public int Var16541E4 { get; set; }

    /// <summary>RAM 0x16541D8 (the kind-0 re-display sets it to 1).</summary>
    public int Var16541D8 { get; set; }

    /// <summary>
    /// RAM byte 0x16541F4. A push does nothing while it is set (<see cref="AdvHistory.Push"/>); the kind-0 re-display
    /// sets it to 1 for a line ending in <c>@n</c> and clears it otherwise.
    /// </summary>
    public byte Var16541F4 { get; set; }

    /// <summary>RAM byte 0x174D9A8: "the line was re-displayed by a rewind". A push clears it, a kind-0 re-display sets it.</summary>
    public byte RestoredFromHistory { get; set; }

    /// <summary>RAM byte 0x174DE00 (cleared by a kind-0 restore).</summary>
    public byte Var174DE00 { get; set; }

    /// <summary>RAM word 0x1653AE0: length of the re-displayed statement text (0 after any other restore step).</summary>
    public ushort RedisplayLength { get; set; }

    /// <summary>RAM dword 0x86AA24 (a kind-0 restore flushes pending effects when it is set and clears it).</summary>
    public uint Var86AA24 { get; set; }

    /// <summary>The text fade value (RAM 0x86AA1C), set to 1.0 by every restore.</summary>
    public float TextFade { get; set; } = 1.0f;

    /// <summary>RAM word 0x1750646 (cleared by every restore).</summary>
    public ushort Word1750646 { get; set; }

    /// <summary>RAM dword 0x1653E24 (cleared by every restore).</summary>
    public uint Var1653E24 { get; set; }

    /// <summary>RAM qword 0x1653E00: the wait / pending-effect flag word of Adv_Update (wait_model.md).</summary>
    public ulong WaitFlags { get; set; }

    /// <summary>RAM qword 0x1653E70: second flag word (bit 0 is set by a kind-0 re-display).</summary>
    public ulong WaitFlags70 { get; set; }

    /// <summary>RAM word 0x1653E08 (cleared by a re-display of a line ending in <c>@n</c>).</summary>
    public ushort Word1653E08 { get; set; }

    /// <summary>RAM 0x16537E0: statement text of the kind-0 re-display (NUL-terminated unless it is 0x300 bytes long).</summary>
    public byte[] RedisplayText { get; } = new byte[RedisplayBufferSize];

    /// <summary>RAM 0x1653AF0: a copy of <see cref="RedisplayText"/>.</summary>
    public byte[] RedisplayCopy { get; } = new byte[RedisplayBufferSize];

    // -- queries and gates the engine answers by calling other functions -----------------------------------------

    /// <summary>Scan mode (RAM byte 0x174DE48): pushes are suppressed while it is set.</summary>
    public byte ScanMode { get; set; }

    /// <summary>
    /// Bit i = effect slot i is active (VA 0x5C660 for i &lt; 14). A push stores it; a restore stops the slots that
    /// are active here and clear in the record (<see cref="RestoreOutcome.StopEffectSlots"/>).
    /// </summary>
    public ushort EffectActiveBits { get; set; }

    /// <summary>Bit i = effect slot i is blocking (VA 0x5C680: active and its motion flag 8 clear). A push stores it.</summary>
    public ushort EffectBlockingBits { get; set; }

    /// <summary>True when the last message of the page is shown (the WNTY word 0xDF08DC bit 0, read by VA 0x53620 at a push).</summary>
    public bool LastMessageOfPage { get; set; }

    /// <summary>The "display end of page instantly" latch (VA 0x56140), read by the kind-0 re-display to decide the end code.</summary>
    public bool PageEndInstantLatch { get; set; }

    // -- helpers -------------------------------------------------------------------------------------------------

    /// <summary>F[<paramref name="index"/>] as an unsigned word.</summary>
    public ushort F(int index) => Vars.GetWord(index);

    /// <summary>F[<paramref name="index"/>] as a signed word.</summary>
    public short FSigned(int index) => (short)Vars.GetWord(index);

    /// <summary>The F indices the history code reads or writes by name (adv_state.md 2.1, 3, 7.3).</summary>
    public static class FIndex
    {
        /// <summary>F823: message index of the page; the staging snapshot and the rewind stop need it &lt;= 1.</summary>
        public const int PageMessage = 823;

        /// <summary>F799: 1 = the last message of its page.</summary>
        public const int LastMessage = 799;

        /// <summary>F929: while non-zero no history record is pushed.</summary>
        public const int PushBlock = 929;

        /// <summary>F931: copied to scene +0x1984 by a kind-0 restore when not negative.</summary>
        public const int WindowStyle = 931;

        /// <summary>F967: scene-bound value, reset by the restore rule when it disagrees with scene +0x19A6.</summary>
        public const int SceneBase = 967;

        /// <summary>F991: a layer change is pending (kind 1 pushed, kind 9 not yet).</summary>
        public const int LayerPending = 991;

        /// <summary>F995: its low byte is stored in scene +0x19A3 at every push.</summary>
        public const int Low995 = 995;

        /// <summary>F1102: while non-zero only kinds 0 and 11 are pushed.</summary>
        public const int OnlyMessages = 1102;

        /// <summary>F1118: set in manual and auto saves (Slot_Store).</summary>
        public const int SavedMarker = 1118;
    }
}
