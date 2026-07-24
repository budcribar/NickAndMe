using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class RemuxAcrossfadeTests
{
    [Fact]
    public void BuildConcatFilterComplex_two_inputs()
    {
        var fc = FfmpegRemuxService.BuildConcatFilterComplex(2);
        Assert.Equal("[0:v][0:a][1:v][1:a]concat=n=2:v=1:a=1[v][a]", fc);
    }

    [Fact]
    public void BuildConcatFilterComplex_three_inputs()
    {
        var fc = FfmpegRemuxService.BuildConcatFilterComplex(3);
        Assert.Equal("[0:v][0:a][1:v][1:a][2:v][2:a]concat=n=3:v=1:a=1[v][a]", fc);
    }
}
