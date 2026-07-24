using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class AssemblyGateTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("pass", false)]
    [InlineData("ok", false)]
    [InlineData("fine", false)]
    [InlineData("short", false)]
    [InlineData("Acceptable continuity despite style note for this cut.", true)]
    public void Override_note_validation(string? note, bool ok) =>
        Assert.Equal(ok, EditLogService.IsValidAutoFailOverrideNote(note));

    [Theory]
    [InlineData("fail", true)]
    [InlineData("FAIL", true)]
    [InlineData("pass", false)]
    [InlineData("unclear", false)]
    [InlineData(null, false)]
    public void Auto_fail_detection(string? suggestion, bool isFail) =>
        Assert.Equal(isFail, EditLogService.IsAutoFailSuggestion(suggestion));
}
