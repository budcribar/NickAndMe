using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class CharacterVisualTextScrubberTests
{
    [Theory]
    [InlineData("matching the dog's CG look")]
    [InlineData("matching the fox's picture-book style")]
    [InlineData("same CG look as the hero animal")]
    [InlineData("matching HeroAnimal's CG look")]
    public void SoftenCrossSpecies_default_is_neutral_medium_not_human_adult(string input)
    {
        var outText = CharacterVisualTextScrubber.SoftenCrossSpeciesStyleLanguage(input);
        Assert.Contains(
            CharacterVisualTextScrubber.SharedFilmMediumPhrase,
            outText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("human adult", outText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not an animal", outText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SoftenCrossSpecies_human_disambiguation_opt_in()
    {
        var outText = CharacterVisualTextScrubber.SoftenCrossSpeciesStyleLanguage(
            "matching the dog's CG look",
            disambiguateAsHuman: true);
        Assert.Contains(
            CharacterVisualTextScrubber.SharedFilmMediumHumanDisambiguationPhrase,
            outText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("human", outText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not an animal", outText, StringComparison.OrdinalIgnoreCase);
        // Prefer "human" over the old "human adult" forced age band
        Assert.DoesNotContain("human adult", outText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScrubVisualProse_animal_seed_does_not_force_human()
    {
        var scrubbed = CharacterVisualTextScrubber.ScrubVisualProse(
            "A small orange cat matching the dog's CG look; soft fur.",
            disambiguateCrossSpeciesAsHuman: false);
        Assert.DoesNotContain("human adult", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cat", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            CharacterVisualTextScrubber.SharedFilmMediumPhrase,
            scrubbed,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScrubVisualProse_human_opt_in_keeps_anti_animal_bleed()
    {
        var scrubbed = CharacterVisualTextScrubber.ScrubVisualProse(
            "A middle-aged woman matching the dog's CG look.",
            disambiguateCrossSpeciesAsHuman: true);
        Assert.Contains("not an animal", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("human adult", scrubbed, StringComparison.OrdinalIgnoreCase);
    }
}
