using FilmStudio.Engine;
using Xunit;

namespace FilmStudio.Tests;

public class BookOcrPlateShortlistTests
{
    [Fact]
    public void Buster2_ocr_shortlist_finds_3_buster_and_bunny_p13()
    {
        var repo = FindRepo();
        var bookTxt = Path.Combine(repo, "projects", "Buster2", "source", "book_full.txt");
        Assert.True(File.Exists(bookTxt), $"Missing {bookTxt}");
        var pages = BookOcrPlateShortlist.ParseBookFull(File.ReadAllText(bookTxt));
        Assert.Equal(15, pages.Count);

        var busterAliases = BookOcrPlateShortlist.AliasesForSeed("Character_Buster", null);
        busterAliases.Add("buster");
        var bunnyAliases = new List<string> { "bunnies", "bunny", "rabbit", "rabbits" };

        var buster = BookOcrPlateShortlist.ShortlistArtPages(pages, busterAliases, maxPlates: 3);
        var bunny = BookOcrPlateShortlist.ShortlistArtPages(pages, bunnyAliases, maxPlates: 1);

        Assert.True(buster.Count >= 3, $"Buster plates: {string.Join(",", buster)}");
        // Gold odds for Buster
        var busterGold = new HashSet<int> { 1, 3, 5, 7, 9, 11, 13, 15 };
        Assert.Equal(3, buster.Count(p => busterGold.Contains(p)));
        Assert.Contains(13, bunny);
    }

    [Fact]
    public void Bunnies_text_on_p12_maps_to_art_p13()
    {
        var pages = BookOcrPlateShortlist.ParseBookFull("""
            --- PAGE 12 ---
            Or bunnies running 'cross the yard
            --- PAGE 13 ---
            (illustration only)
            """);
        var plates = BookOcrPlateShortlist.ShortlistArtPages(
            pages, new[] { "bunnies", "bunny" }, maxPlates: 1);
        Assert.Equal(new[] { 13 }, plates);
    }

    static string FindRepo()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "projects")) &&
                Directory.Exists(Path.Combine(d.FullName, "host")))
                return d.FullName;
            d = d.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
