using System.Text;
using Tsuki.Data.Census;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Census;

/// <summary>The rules the census follows from <c>tsuki.script</c> (variant suffix, layer tracking), checked on synthetic statements.</summary>
public sealed class CensusRulesTests
{
    private static StatementView View(string name, params (int Index, string Value)[] args)
    {
        int count = args.Length == 0 ? 0 : args.Max(a => a.Index) + 1;
        string[] list = new string[count];
        Array.Fill(list, string.Empty);
        foreach ((int index, string value) in args)
        {
            list[index] = value;
        }

        byte[] bytes = Encoding.ASCII.GetBytes("(" + string.Join(",", list) + ");");
        return new StatementView(new Statement(0, 0, 4 + bytes.Length, CommandName.FromAscii(name), bytes));
    }

    [Fact]
    public void TheVariantDigitsOfALayersCurrentPictureFollowTheEnginesRule()
    {
        // VA 0x81862..0x81935: seven characters at least, '_' at length - 6 and length - 3, and the prefix IMG or fifteen characters
        Assert.Equal((11, 11), LayerNameTracker.VariantDigits("IMG1696_11_11"));
        Assert.Equal((0, 4), LayerNameTracker.VariantDigits("IMG0342_00_04"));
        Assert.Equal((30, 30), LayerNameTracker.VariantDigits("AKI_A_00_03_N04_30_30"));
        Assert.Equal((2, 30), LayerNameTracker.VariantDigits("SIK_A_32_00_N00_02_30"));
        Assert.Equal((12, 34), LayerNameTracker.VariantDigits("ABCDEFGHI_12_34")); // 15 characters, no IMG prefix
        Assert.Equal((12, 34), LayerNameTracker.VariantDigits("IMG_12_34")); // the prefix makes up for the length
        Assert.Null(LayerNameTracker.VariantDigits("ABCDEFGH_12_34")); // 14 characters, no IMG prefix
        Assert.Null(LayerNameTracker.VariantDigits("IMG_12_3")); // the second underscore is not at length - 3
        Assert.Null(LayerNameTracker.VariantDigits("IMG1696")); // no suffix
        Assert.Null(LayerNameTracker.VariantDigits("_12_34")); // six characters
        Assert.Null(LayerNameTracker.VariantDigits(string.Empty));

        // the four digit bytes are not validated: 'x' - '0' = 72
        Assert.Equal((10 * 72 + 72, 10 * 72 + 72), LayerNameTracker.VariantDigits("IMG_xx_xx"));
    }

    [Fact]
    public void AnStchNameTakesItsSuffixFromArguments9And10()
    {
        var tracker = new LayerNameTracker();
        StatementView stch = View("STCH", (0, "4"), (1, "img1696"), (9, "11"), (10, "11"));
        byte[]? name = LayerNameTracker.PictureName(stch, tracker.SourceName(stch), out int aa, out int bb);
        Assert.Equal("img1696", Encoding.ASCII.GetString(name!));
        Assert.Equal((11, 11), (aa, bb));

        StatementView plain = View("STCH", (0, "8"), (1, "img0342"), (8, "CC"), (9, "0"), (10, "0"));
        Assert.Equal("img0342", Encoding.ASCII.GetString(LayerNameTracker.PictureName(plain, null, out aa, out bb)!));
        Assert.Equal((0, 0), (aa, bb));

        Assert.Null(LayerNameTracker.PictureName(View("STCH", (0, "4")), null, out _, out _)); // no name
        Assert.Null(LayerNameTracker.PictureName(View("STCH", (0, "4"), (1, string.Empty)), null, out _, out _)); // an empty name
    }

    [Fact]
    public void ALayerTakenOverWithArgument18PassesItsVariantOnUnlessTheArgumentsOverrideIt()
    {
        var tracker = new LayerNameTracker();
        StatementView first = View("STCH", (0, "4"), (1, "img1696"), (9, "11"), (10, "12"));
        LayerNameTracker.PictureName(first, tracker.SourceName(first), out _, out _);
        tracker.Update(first, "IMG1696_11_12");

        StatementView takeOver = View("SCH2", (0, "5"), (1, "img1696"), (18, "4"));
        Assert.Equal("IMG1696_11_12", tracker.SourceName(takeOver));
        LayerNameTracker.PictureName(takeOver, tracker.SourceName(takeOver), out int aa, out int bb);
        Assert.Equal((11, 12), (aa, bb));

        StatementView override9 = View("SCH2", (0, "5"), (1, "img1696"), (9, "2"), (18, "4"));
        LayerNameTracker.PictureName(override9, tracker.SourceName(override9), out aa, out bb);
        Assert.Equal((2, 12), (aa, bb)); // argument 9 wins, argument 10 is inherited

        // a layer nothing was put on passes nothing on; only STCH and SCH2 take a layer over
        StatementView far = View("SCH2", (0, "5"), (1, "img1696"), (18, "20"));
        Assert.Null(tracker.SourceName(far));
        StatementView stchTakeOver = View("STCH", (0, "5"), (1, "img1696"), (18, "4"));
        Assert.Equal("IMG1696_11_12", tracker.SourceName(stchTakeOver));
        Assert.Null(tracker.SourceName(View("SPFC", (0, "5"), (1, "x"), (18, "4"))));

        // STCC(layer) forgets one layer, STCC(-1) all of them
        tracker.Update(View("STCC", (0, "4")), null);
        Assert.Null(tracker.SourceName(takeOver));
        tracker.Update(first, "IMG1696_11_12");
        Assert.NotNull(tracker.SourceName(takeOver));
        tracker.Update(View("STCC", (0, "-1")), null);
        Assert.Null(tracker.SourceName(takeOver));
    }

    [Fact]
    public void AStatementViewCutsTheArgumentsBothWays()
    {
        StatementView s = View("STCH", (0, "4"), (1, "\"a,b\""), (2, "`017:5"));
        Assert.Equal("STCH", s.Name);
        Assert.Equal(4, s.TextCount); // the textual cut does not know quotes: 4, "a, b", `017:5
        Assert.Equal("5", s.TextString(3)); // the keyword tag is not part of the value
        Assert.Equal("`017:5", Encoding.ASCII.GetString(s.TextRaw(3)));
        Assert.Equal("a,b", Encoding.ASCII.GetString(s.ObjArg(1))); // the object parser keeps a quoted argument whole and drops its quotes
        Assert.True(s.ObjHas(0));
        Assert.False(s.ObjHas(7));
        Assert.Equal(4, s.Expr(0));
        Assert.Null(s.Expr(7));

        var marker = new StatementView(new Statement(0, 0, 20, CommandName.FromAscii("ZM00"), Encoding.ASCII.GetBytes("1001(\"x\");")));
        Assert.Equal("ZM", marker.Name);
        Assert.Equal("ZM00", marker.Key);
    }
}
