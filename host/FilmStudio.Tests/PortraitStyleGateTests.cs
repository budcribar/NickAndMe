using FilmStudio.Engine;
using Xunit;

namespace FilmStudio.Tests;

public sealed class PortraitStyleGateTests
{
    [Theory]
    [InlineData("STYLE LOCK: Live-action gothic period drama; photoreal human faces", false)]
    [InlineData("STYLE LOCK: children's picture-book illustration, painted cartoon", true)]
    [InlineData("photoreal live-action period drama circa 1840s", false)]
    public void PrefersIllustrated_FromProjectStyle(string style, bool illustrated)
    {
        Assert.Equal(
            illustrated,
            CharacterDesignService.PrefersIllustratedPortraitStyle(style, hasImageHints: false, isAnimal: false));
    }

    [Fact]
    public void PrefersIllustrated_DefaultsToIllustrated_WithBookPlatesOrAnimal()
    {
        Assert.True(CharacterDesignService.PrefersIllustratedPortraitStyle(null, hasImageHints: true, isAnimal: false));
        Assert.True(CharacterDesignService.PrefersIllustratedPortraitStyle("", hasImageHints: false, isAnimal: true));
        Assert.False(CharacterDesignService.PrefersIllustratedPortraitStyle(null, hasImageHints: false, isAnimal: false));
    }

    [Fact]
    public void ParseGate_PassPhotoreal()
    {
        var g = CharacterDesignService.ParsePortraitStyleGateResponse(
            """{"pass":true,"medium":"photoreal","reason":"photo skin"}""");
        Assert.NotNull(g);
        Assert.True(g.Value.Pass);
        Assert.Equal("photoreal", g.Value.Medium);
    }

    [Fact]
    public void ParseGate_FailSketch()
    {
        var g = CharacterDesignService.ParsePortraitStyleGateResponse(
            """{"pass":false,"medium":"sketch","reason":"pencil drawing"}""");
        Assert.NotNull(g);
        Assert.False(g.Value.Pass);
        Assert.Equal("sketch", g.Value.Medium);
    }

    [Fact]
    public void ParseGate_NormalizesAliases()
    {
        var g = CharacterDesignService.ParsePortraitStyleGateResponse(
            """{"pass":true,"medium":"live-action","reason":"ok"}""");
        Assert.NotNull(g);
        Assert.Equal("photoreal", g.Value.Medium);
    }
}
