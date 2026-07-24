using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class VoicePreviewServiceTests
{
    [Fact]
    public void Sample_dialogue_is_short_name_line()
    {
        var t = VoicePreviewService.BuildSampleDialogue("Daddy");
        Assert.Contains("Daddy", t);
        Assert.DoesNotContain("Adult male", t, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fingerprint_changes_when_profile_edited()
    {
        var a = VoicePreviewService.ComputeFingerprint(
            "Character_Daddy", "Adult male; low", "Daddy", "Hello");
        var b = VoicePreviewService.ComputeFingerprint(
            "Character_Daddy", "Adult male; deep gravel", "Daddy", "Hello");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Fingerprint_stable_for_same_inputs()
    {
        var a = VoicePreviewService.ComputeFingerprint(
            "Character_Momma", "warm", "Momma", "Hi");
        var b = VoicePreviewService.ComputeFingerprint(
            "Character_Momma", "warm", "Momma", "Hi");
        Assert.Equal(a, b);
    }
}
