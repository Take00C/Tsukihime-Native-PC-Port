using Tsuki.Data.Images;

namespace Tsuki.Tests.Data.Images;

/// <summary>Sprite file names (images.md 7): the cases of <c>tsuki.image.split_sprite_name</c>.</summary>
public sealed class SpriteNameTests
{
    [Fact]
    public void ABodyLayerHasTheSuffixB()
    {
        var n = SpriteName.Split("ARU_L_81_00_N00_B.MZP");
        Assert.NotNull(n);
        Assert.Equal(("ARU", "L", "81", "00", "N00"), (n.Char, n.Size, n.Pose, n.Variant, n.Face));
        Assert.True(n.IsBase);
        Assert.Equal("ARU_L_81_00", n.Group);
    }

    [Fact]
    public void AFaceLayerKeepsItsSuffixParts()
    {
        var n = SpriteName.Split("NOE_A_72_00_N00_01_01.MZP");
        Assert.NotNull(n);
        Assert.False(n.IsBase);
        Assert.Equal(["01", "01"], n.Suffix);
        var plain = SpriteName.Split("ARU_L_81_00_N00.MZP");
        Assert.NotNull(plain);
        Assert.Empty(plain.Suffix);
        Assert.False(plain.IsBase);
        Assert.Equal("ARU_L_81_00", plain.Group);
        var noExtension = SpriteName.Split("HIS_M_21_01_A01_B");
        Assert.NotNull(noExtension);
        Assert.True(noExtension.IsBase);
    }

    [Theory]
    [InlineData("IMG0001.MZP")]
    [InlineData("IMG1481_20_00.MZP")]
    [InlineData("BLACK.MZP")]
    [InlineData("DUMMY.MRG")]
    [InlineData("AR_L_81_00_N00.MZP")]       // character code of 2 letters
    [InlineData("ARU_LL_81_00_N00.MZP")]     // size class of 2 letters
    [InlineData("ARU_L_81_00_N0.MZP")]       // face of 2 characters
    [InlineData("")]
    public void OtherNamesAreNotSprites(string name) => Assert.Null(SpriteName.Split(name));

    [Fact]
    public void OnlyTheLastDotEndsTheStem()
    {
        var n = SpriteName.Split("ARU_L_81_00_N00_B.v2.MZP");
        Assert.NotNull(n);
        Assert.Equal(["B.v2"], n.Suffix);
        Assert.False(n.IsBase);
    }
}
