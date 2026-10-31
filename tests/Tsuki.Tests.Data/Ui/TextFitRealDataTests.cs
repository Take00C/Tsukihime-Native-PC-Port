using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.Ui;

/// <summary>
/// The menu texts that have to fit a fixed picture, laid out with the game's own engine on the user's data in every language (package R1): the dialog messages inside
/// the 824 px dialog window, the quick load's thumbnail below its message, and the flowchart's scene card texts inside the card's white area.
/// </summary>
public sealed class TextFitRealDataTests
{
    private static readonly Language[] Languages = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    /// <summary>Every message a <see cref="ConfirmDialog"/> or a <see cref="MessagePopup"/> shows: the dialog table, the quit, flowchart and delete texts, the save popups and the ending replay prompts.</summary>
    private static IEnumerable<(string Kind, int Id)> DialogIds(UiAssets assets)
    {
        IUiTables ui = assets.Tables;
        for (int i = 0; i < ui.DialogMessages.Count; i++)
        {
            yield return ($"dialog {i}", ui.DialogMessages[i]);
        }

        yield return ("quit", 236);
        yield return ("flowchart locked", 1497);
        yield return ("flowchart rewind", 1498);
        yield return ("deleted", 217);
        foreach (int knife in new[] { 160, 183, 184, 188, 189, 190, 193 })
        {
            yield return ($"sysmes {knife}", UiAssets.Sysmes(knife));
        }

        if (assets.Gallery is { } g)
        {
            for (int i = 0; i < g.Endings.Count; i++)
            {
                yield return ($"ending {i}", g.Endings[i].MessageUi);
            }
        }
    }

    private static float WidestLine(UiText text, string message, in TextStyle style)
    {
        float widest = 0f;
        foreach (string line in message.Split('^'))
        {
            (float left, float right) = text.InkSpan(line, style);
            widest = MathF.Max(widest, right - left);
        }

        return widest;
    }

    [GameDataFact]
    public void EveryDialogMessageFitsTheDialogWindowInEveryLanguage()
    {
        foreach (Language lang in Languages)
        {
            var assets = new UiAssets(SharedSource.Source, lang);
            var text = new UiText(assets);
            int fitted = 0;
            int checkedMessages = 0;
            foreach ((string kind, int id) in DialogIds(assets))
            {
                string message = assets.Text(id);
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                checkedMessages++;
                foreach (float lineSpacing in new[] { 0f, 4f })
                {
                    TextStyle style = ConfirmDialog.DialogStyle(text, message, lineSpacing);
                    TextStyle plain = TextStyle.Slot(0.6, 0.7) with { Width = 640, Height = 200, Leading = style.Leading, DrawFlags = style.DrawFlags };
                    float widest = WidestLine(text, message, style);
                    Assert.True(widest <= ConfirmDialog.DialogInnerWidth, $"{lang} {kind} (ui_text {id}): widest line {widest:F0} px in the {ConfirmDialog.DialogInnerWidth} px window: \"{message}\"");
                    Assert.Equal(text.Layout(message, plain).NLines, text.Layout(message, style).NLines);
                    if (WidestLine(text, message, plain) <= ConfirmDialog.DialogTextWidth)
                    {
                        Assert.Equal(plain, style);
                    }
                    else
                    {
                        fitted++;
                        Assert.InRange(style.Sx / plain.Sx, 0.8, 1.0);
                        Assert.Equal(style.Sx / plain.Sx, style.Sy / plain.Sy, 6);
                    }
                }
            }

            Assert.True(checkedMessages >= 60, $"{lang}: {checkedMessages} messages");
            if (lang is Language.En or Language.Ja)
            {
                // the English quick jump / rewind / Recommended Settings messages and the Japanese ending prompts are the ones the fit is for
                Assert.True(fitted > 0, $"{lang}: no message needed the fit");
            }
        }
    }

    [GameDataFact]
    public void TheQuickLoadThumbnailHangsBelowItsMessageInEveryLanguage()
    {
        foreach (Language lang in Languages)
        {
            var assets = new UiAssets(SharedSource.Source, lang);
            var text = new UiText(assets);
            string message = assets.Text(new ConfirmDialog(ConfirmKind.QuickLoad, _ => { }).MessageId(assets));
            TextStyle style = ConfirmDialog.DialogStyle(text, message, 0f);
            // the message's lines hang from 160; the last line's box ends before the block's trailing leading
            float bottom = 160f + text.Height(message, style) - style.Leading;
            float frame = ConfirmDialog.ThumbnailTop(lang);
            Assert.True(bottom <= frame, $"{lang}: the message ends at {bottom:F1} units, the thumbnail frame starts at {frame}: \"{message}\"");
            Assert.True(frame + (53f * 1.0859f * 0.825f) + 4f <= 261.46f + 4f, $"{lang}: the frame runs into the buttons");
        }
    }

    [GameDataFact]
    public void EveryFlowchartCardTextFitsTheCardInEveryLanguage()
    {
        foreach (Language lang in Languages)
        {
            var assets = new UiAssets(SharedSource.Source, lang);
            var text = new UiText(assets);
            IUiGalleryTables tables = assets.Gallery ?? throw new InvalidOperationException("no gallery tables");
            var services = new PreviewServices(lang);
            services.Believe(SharedSource.Source, "full");
            FlowchartChart chart = FlowchartChart.Build(tables, lang, services, gallery: true);
            FlowchartScreen.CardText card = FlowchartScreen.CardTextOf(lang);
            var seen = new HashSet<(int, int)>();
            int fittedTitles = 0;
            foreach (ChartNode n in chart.Nodes)
            {
                if (!seen.Add((n.TitleUi, n.SynopsisUi)))
                {
                    continue;
                }

                string title = assets.Text(n.TitleUi).Replace("^", string.Empty, StringComparison.Ordinal);
                string synopsis = assets.Text(n.SynopsisUi);
                TextStyle titleStyle = FlowchartScreen.TitleStyle(text, card, title);
                if (titleStyle != card.Title)
                {
                    fittedTitles++;
                }

                Assert.Equal(1, text.Layout(title, titleStyle).NLines);
                // English titles end inside the white area; the other languages keep knife's layout, whose widest Japanese title ends 13 px past it, on the card's edge
                float titleRight = card.X + text.InkSpan(title, titleStyle).Right;
                float limit = lang == Language.En ? FlowchartScreen.CardTextRight : 1840f;
                Assert.True(titleRight <= limit + 1f, $"{lang} node {n.Node}: the title's ink ends at {titleRight:F0}: \"{title}\"");
                float titleBottom = card.TitleY + ((text.Height(title, titleStyle) - titleStyle.Leading) * UiMetrics.UnitY);
                Assert.True(titleBottom <= card.SynopsisY, $"{lang} node {n.Node}: the title ends at {titleBottom:F0}, the synopsis starts at {card.SynopsisY}: \"{title}\"");
                float bottom = card.SynopsisY + (text.Height(synopsis, card.Synopsis) * UiMetrics.UnitY);
                Assert.True(bottom <= FlowchartScreen.CardTextBottom, $"{lang} node {n.Node}: the synopsis ends at {bottom:F0}, the card's white area at {FlowchartScreen.CardTextBottom}: \"{synopsis}\"");
            }

            Assert.True(seen.Count >= 400, $"{lang}: {seen.Count} cards");
            if (lang == Language.En)
            {
                Assert.InRange(fittedTitles, 1, 12);
            }
            else
            {
                Assert.Equal(0, fittedTitles);
            }
        }
    }
}
