using Tsuki.Engine.Contracts;
using Tsuki.Engine.Sound;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;

namespace Tsuki.Tests.Data.Ui;

/// <summary>
/// Package K36: the eboot's voice row tables as the Sound tab and the <c>_VPLY</c> marks use them: the 19 row words (text VA 0x1A6850) are distinct
/// G words inside the persistent G[0..499], disjoint from the last-voice words G[31..49] and the heard word G[73]; the face and name sheets have the
/// 20 cells the rows index (19 characters plus the unknown face / name).
/// </summary>
public sealed class VoiceRowsRealDataTests
{
    [GameDataFact]
    public void TheRowWordsArePersistentDistinctGWordsOutsideTheOtherMarks()
    {
        IUiTables t = new UiAssets(SharedSource.Source, Language.En).Tables;
        IReadOnlyList<int> words = t.VoiceRowWords;
        Assert.Equal(VoiceMarks.RowCount, words.Count);
        Assert.Equal(words.Count, words.Distinct().Count());
        foreach (int w in words)
        {
            Assert.InRange(w, 0, SystemBlock.GCount - 1);
            Assert.NotEqual(VoiceMarks.HeardWord, w);
            Assert.False(w >= VoiceMarks.LastVoiceWord && w < VoiceMarks.LastVoiceWord + VoiceMarks.RowCount, $"row word {w} collides with the last-voice words");
        }

        Assert.Equal(20, t.VoiceFaceCells.Count);
        foreach (Language lang in new[] { Language.Ja, Language.En, Language.Zc, Language.Zt })
        {
            Assert.Equal(20, t.VoiceNameCells(lang).Count);
        }

        // every character the voice groups name maps to one of the 19 rows, and the extra character (23) too, although the handler never marks it
        for (int c = 0; c < 24; c++)
        {
            Assert.InRange(VoiceGroups.SlotOf(c), 0, VoiceMarks.RowCount - 1);
        }
    }
}
