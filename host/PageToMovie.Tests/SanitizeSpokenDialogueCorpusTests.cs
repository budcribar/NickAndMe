using System.Text;
using System.Text.RegularExpressions;
using PageToMovie.Engine;
using Xunit;
using Xunit.Abstractions;

namespace PageToMovie.Tests;

/// <summary>
/// Corpus check: every dialogue line in repo fountain files survives speech sanitization
/// sensibly (used at clip gen). Reports changed lines via test output.
/// </summary>
public class SanitizeSpokenDialogueCorpusTests
{
    private readonly ITestOutputHelper _out;

    public SanitizeSpokenDialogueCorpusTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void All_repo_fountain_dialogue_sanitizes_safely()
    {
        var roots = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixtures")),
        };
        // Fixtures are copied to test output; also scan repo projects + host fixtures
        var searchRoots = new List<string>();
        foreach (var r in roots)
        {
            if (Directory.Exists(r))
                searchRoots.Add(r);
        }
        // Repo root from Engine assembly path is fragile — walk from known test Fixtures + optional env
        var testProj = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var repoHost = Path.GetFullPath(Path.Combine(testProj, ".."));
        var repoRoot = Path.GetFullPath(Path.Combine(repoHost, ".."));
        foreach (var dir in new[]
                 {
                     Path.Combine(testProj, "Fixtures"),
                     Path.Combine(repoHost, "playwright", "fixtures"),
                     Path.Combine(repoRoot, "projects"),
                 })
        {
            if (Directory.Exists(dir))
                searchRoots.Add(dir);
        }

        var files = searchRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.fountain", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(files.Count >= 20, $"Expected many fountain files, found {files.Count}");

        var totalLines = 0;
        var changedLines = 0;
        var emptyCollapse = 0;
        var gluedFixed = 0;
        var emFixed = 0;
        var samples = new List<string>();
        var suspicious = new List<string>();
        var byFile = new List<(string File, int Lines, int Changed)>();

        // Compounds that must never be pause-split (corpus regression guard)
        var mustKeepHyphen = new[]
        {
            "to-day", "to-morrow", "to-night", "good-bye", "writing-desk",
            "tea-time", "tea-party", "door-nail", "well-known", "age-old",
            "half-past", "bread-and-butter", "bed-curtains", "look-out",
        };

        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            var parsed = FountainParser.Parse(text); // applies NormalizeTypographicPunctuation
            var dialogueChunks = ExtractDialogueLines(parsed);
            var fileChanged = 0;
            foreach (var raw in dialogueChunks)
            {
                totalLines++;
                var cleaned = ClipVideoPromptBuilder.SanitizeSpokenDialogue(raw);
                if (string.IsNullOrWhiteSpace(cleaned) && !string.IsNullOrWhiteSpace(raw))
                {
                    emptyCollapse++;
                    suspicious.Add($"EMPTY: {Rel(path, repoRoot)} :: {Trunc(raw, 80)}");
                    continue;
                }

                if (!string.Equals(raw.Trim(), cleaned, StringComparison.Ordinal))
                {
                    changedLines++;
                    fileChanged++;
                    if (raw.Contains("!-") || raw.Contains("?-") || raw.Contains(";-"))
                        gluedFixed++;
                    if (raw.IndexOf('\u2014') >= 0 || raw.IndexOf('\u2013') >= 0 ||
                        cleaned.Contains('—'))
                        emFixed++;

                    if (samples.Count < 40)
                        samples.Add($"{Rel(path, repoRoot)}\n  IN : {Trunc(raw, 100)}\n  OUT: {Trunc(cleaned, 100)}");
                }

                // Regression: protected compounds present in raw must remain hyphenated in cleaned
                foreach (var compound in mustKeepHyphen)
                {
                    if (!raw.Contains(compound, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!cleaned.Contains(compound, StringComparison.OrdinalIgnoreCase))
                    {
                        suspicious.Add(
                            $"COMPOUND-BROKEN: {Rel(path, repoRoot)} :: lost '{compound}' in: {Trunc(cleaned, 90)}");
                    }
                }

                // Must not reintroduce glued !-
                Assert.DoesNotContain("!-", cleaned, StringComparison.Ordinal);
                Assert.DoesNotContain("?-", cleaned, StringComparison.Ordinal);
            }

            if (dialogueChunks.Count > 0)
                byFile.Add((Rel(path, repoRoot), dialogueChunks.Count, fileChanged));
        }

        _out.WriteLine($"Files scanned: {files.Count}");
        _out.WriteLine($"Dialogue lines: {totalLines}");
        _out.WriteLine($"Changed by SanitizeSpokenDialogue: {changedLines}");
        _out.WriteLine($"Empty collapses (bad): {emptyCollapse}");
        _out.WriteLine($"Lines with !-/ glue pattern fixed (subset): {gluedFixed}");
        _out.WriteLine("");
        _out.WriteLine("=== Sample transforms (up to 40) ===");
        foreach (var s in samples)
            _out.WriteLine(s + "\n");

        _out.WriteLine("=== Per-file (lines / changed) ===");
        foreach (var row in byFile.OrderByDescending(x => x.Changed).ThenBy(x => x.File))
            _out.WriteLine($"{row.Changed,4}/{row.Lines,-4}  {row.File}");

        if (suspicious.Count > 0)
        {
            _out.WriteLine("");
            _out.WriteLine($"=== Compound regressions ({suspicious.Count}) ===");
            foreach (var s in suspicious.Distinct().Take(80))
                _out.WriteLine(s);
        }

        Assert.Equal(0, emptyCollapse);
        Assert.True(totalLines > 50, $"Expected substantial dialogue corpus, got {totalLines}");
        Assert.True(
            suspicious.Count == 0,
            "Protected compounds were broken:\n" + string.Join("\n", suspicious.Take(20)));

        // Iconic Tell-Tale line must clean either as crushed or unicode form
        var iconicCrushed = "True!-nervous-very, very dreadfully nervous I had been and am;";
        var iconicOut = ClipVideoPromptBuilder.SanitizeSpokenDialogue(iconicCrushed);
        Assert.Equal("True! Nervous — very, very dreadfully nervous I had been and am;", iconicOut);
        Assert.DoesNotContain("True!-nervous", iconicOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extract dialogue the way gen would see it after Fountain parse (typographic normalize applied).
    /// </summary>
    private static List<string> ExtractDialogueLines(FountainParser.ParseResult parsed)
    {
        var list = new List<string>();
        var buf = new StringBuilder();
        void Flush()
        {
            var t = buf.ToString().Trim();
            buf.Clear();
            if (t.Length > 0)
                list.Add(t);
        }

        foreach (var el in parsed.Elements)
        {
            switch (el.Type)
            {
                case FountainParser.ElementType.Dialogue:
                    if (buf.Length > 0) buf.Append(' ');
                    buf.Append(el.Text);
                    break;
                case FountainParser.ElementType.Parenthetical:
                    // keep attached to dialogue stream; don't flush
                    break;
                default:
                    Flush();
                    break;
            }
        }

        Flush();
        return list;
    }

    private static string Rel(string path, string repoRoot)
    {
        try
        {
            if (path.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
                return path[(repoRoot.Length)..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { /* ignore */ }
        return Path.GetFileName(path);
    }

    private static string Trunc(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}
