using System.Text;
using System.Text.RegularExpressions;

namespace PageToMovie.Engine;

/// <summary>
/// Fountain 1.1 plain-text screenplay parser.
/// Spec: https://fountain.io/syntax/
/// </summary>
public static class FountainParser
{
    public enum ElementType
    {
        SceneHeading,
        Action,
        Character,
        Parenthetical,
        Dialogue,
        Transition,
        Lyric,
        Section,
        Synopsis,
        PageBreak,
        Note,
        Centered,
    }

    public sealed class Element
    {
        public ElementType Type { get; init; }
        public string Text { get; init; } = "";
        /// <summary>
        /// Character extension e.g. (O.S.); Section depth as "#"; dual dialogue "dual";
        /// Scene number if present.
        /// </summary>
        public string? Meta { get; init; }
    }

    public sealed class ParseResult
    {
        public Dictionary<string, string> TitlePage { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Element> Elements { get; } = new();
    }

    // INT / EXT / EST / INT./EXT / INT/EXT / I/E / I./E followed by . or space
    // (I./E is used in the nyousefi Fountain reference fixtures)
    private static readonly Regex SceneHeadingStart = new(
        @"^(INT\./EXT|INT/EXT|I\./E|I/E|INT\.?|EXT\.?|EST\.?)(\s|\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TransitionEnd = new(
        @"TO:$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Common standalone transitions that do not end in TO: (FADE IN / FADE OUT / …).
    /// </summary>
    private static readonly Regex StandaloneFadeTransition = new(
        @"^(FADE\s+IN|FADE\s+OUT|FADE\s+TO\s+BLACK|FADE\s+TO\s+WHITE|CUT\s+TO\s+BLACK|BLACK\s+OUT)[\.:]?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SceneNumberSuffix = new(
        @"\s+#([^#]+)#\s*$",
        RegexOptions.Compiled);

    private static readonly Regex TitleKeyLine = new(
        @"^([A-Za-z][A-Za-z0-9 ]*):\s*(.*)$",
        RegexOptions.Compiled);

    public static ParseResult Parse(string text)
    {
        text ??= "";
        // Normalize typographic punctuation so CONT'D / MARLEY'S match ASCII rules
        text = NormalizeTypographicPunctuation(text);
        // Boneyard /* ... */ may span lines — remove entirely
        text = Regex.Replace(text, @"/\*.*?\*/", "\n", RegexOptions.Singleline);

        // Extract and remove notes [[...]] (may span lines; empty line inside needs two spaces per spec)
        var notes = new List<string>();
        text = Regex.Replace(text, @"\[\[(.*?)\]\]", m =>
        {
            notes.Add(m.Groups[1].Value.Trim());
            return "";
        }, RegexOptions.Singleline);

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // Tabs → 4 spaces (Fountain: tabs converted to four spaces in Action)
        for (var t = 0; t < lines.Length; t++)
            lines[t] = lines[t].Replace("\t", "    ");

        var result = new ParseResult();
        foreach (var n in notes)
        {
            if (n.Length > 0)
                result.Elements.Add(new Element { Type = ElementType.Note, Text = n });
        }

        var i = ParseTitlePage(lines, 0, result);
        // Some Fountain files put dual-dialogue caret on its own line before the second speaker.
        var pendingDual = false;

        while (i < lines.Length)
        {
            var raw = lines[i];
            // Keep right-trim only for classification; Action may keep leading spaces
            var line = raw.TrimEnd();
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            // Dual dialogue marker alone on a line (non-spec but common) → next Character is dual
            if (trimmed == "^")
            {
                pendingDual = true;
                i++;
                continue;
            }

            // Page break: ===
            if (Regex.IsMatch(trimmed, @"^={3,}\s*$"))
            {
                result.Elements.Add(new Element { Type = ElementType.PageBreak, Text = trimmed });
                i++;
                continue;
            }

            // Section: # ...
            if (trimmed.StartsWith('#'))
            {
                var depth = trimmed.TakeWhile(c => c == '#').Count();
                result.Elements.Add(new Element
                {
                    Type = ElementType.Section,
                    Text = trimmed.TrimStart('#').Trim(),
                    Meta = depth.ToString(),
                });
                i++;
                continue;
            }

            // Synopsis: = ...  (not === page break)
            if (trimmed.StartsWith('=') && !trimmed.StartsWith("==="))
            {
                result.Elements.Add(new Element
                {
                    Type = ElementType.Synopsis,
                    Text = trimmed.TrimStart('=').Trim(),
                });
                i++;
                continue;
            }

            // Lyrics: ~...
            if (trimmed.StartsWith('~'))
            {
                result.Elements.Add(new Element
                {
                    Type = ElementType.Lyric,
                    Text = UnescapeFountain(trimmed.TrimStart('~').TrimStart()),
                });
                i++;
                continue;
            }

            // Forced action: !...
            if (trimmed.StartsWith('!'))
            {
                result.Elements.Add(new Element
                {
                    Type = ElementType.Action,
                    Text = PreserveActionIndent(raw, UnescapeFountain(trimmed[1..].TrimStart())),
                });
                i++;
                continue;
            }

            // Forced scene heading: .ALNUM... (single period, not ellipsis)
            if (trimmed.StartsWith('.') &&
                trimmed.Length > 1 &&
                char.IsLetterOrDigit(trimmed[1]))
            {
                var (heading, sceneNo) = SplitSceneNumber(trimmed[1..].Trim());
                result.Elements.Add(new Element
                {
                    Type = ElementType.SceneHeading,
                    Text = heading,
                    Meta = sceneNo,
                });
                i++;
                continue;
            }

            // Forced character: @Name
            if (trimmed.StartsWith('@'))
            {
                var dual = pendingDual || trimmed.TrimEnd().EndsWith('^');
                pendingDual = false;
                var (name, ext) = SplitCharacter(trimmed[1..].Trim().TrimEnd('^').Trim());
                result.Elements.Add(new Element
                {
                    Type = ElementType.Character,
                    Text = name,
                    Meta = BuildCharMeta(ext, dual),
                });
                i++;
                i = ConsumeDialogueBlock(lines, i, result);
                continue;
            }

            // Centered: > text <  (Action, leading spaces not preserved)
            if (IsCentered(trimmed))
            {
                var inner = trimmed.Trim().TrimStart('>').TrimEnd('<').Trim();
                result.Elements.Add(new Element
                {
                    Type = ElementType.Centered,
                    Text = UnescapeFountain(inner),
                });
                i++;
                continue;
            }

            // Forced transition: >...  (not centered)
            if (trimmed.StartsWith('>') && !IsCentered(trimmed))
            {
                result.Elements.Add(new Element
                {
                    Type = ElementType.Transition,
                    Text = UnescapeFountain(trimmed.TrimStart('>').Trim()),
                });
                i++;
                continue;
            }

            var prevBlank = PrevBlank(lines, i);
            var nextBlank = NextBlank(lines, i);
            var classify = trimmed; // already trimmed; indent ignored for non-action

            // Automatic scene heading: blank before + INT/EXT/...
            // Fountain prefers a blank after; we also accept page-tag / synopsis lines
            // immediately under the heading (= page N, = synopsis) for book tooling.
            if (prevBlank && SceneHeadingStart.IsMatch(classify) &&
                (nextBlank || NextIsPageTagOrSynopsis(lines, i)))
            {
                var (heading, sceneNo) = SplitSceneNumber(classify);
                result.Elements.Add(new Element
                {
                    Type = ElementType.SceneHeading,
                    Text = heading,
                    Meta = sceneNo,
                });
                i++;
                continue;
            }

            // Transition: uppercase, blank before/after.
            // Classic Fountain: ends with TO: (spaces after colon → Action).
            // Also: FADE IN / FADE OUT / FADE TO BLACK (common and would otherwise
            // invent a phantom scene if treated as Action before the first heading).
            if (prevBlank && nextBlank)
            {
                var transCandidate = raw.TrimStart(); // keep trailing spaces after colon for TO: rule
                if (IsStandaloneTransitionLine(transCandidate))
                {
                    result.Elements.Add(new Element
                    {
                        Type = ElementType.Transition,
                        Text = UnescapeFountain(transCandidate.Trim()),
                    });
                    i++;
                    continue;
                }
            }

            // Character + dialogue: blank before, NOT blank after, all-caps name
            // Also accept when a prior standalone ^ dual-marker left no blank "before"
            // (marker line is not blank, so prevBlank is false) — use pendingDual.
            if ((prevBlank || pendingDual) && !nextBlank && IsCharacterLine(classify))
            {
                var dual = pendingDual || classify.TrimEnd().EndsWith('^');
                pendingDual = false;
                var (name, ext) = SplitCharacter(classify.TrimEnd('^', ' ', '\t'));
                result.Elements.Add(new Element
                {
                    Type = ElementType.Character,
                    Text = name,
                    Meta = BuildCharMeta(ext, dual),
                });
                i++;
                i = ConsumeDialogueBlock(lines, i, result);
                continue;
            }

            // Default: Action (preserve leading indentation)
            pendingDual = false; // orphan dual marker shouldn't stick forever
            result.Elements.Add(new Element
            {
                Type = ElementType.Action,
                Text = PreserveActionIndent(raw, UnescapeFountain(trimmed)),
            });
            i++;
        }

        return result;
    }

    /// <summary>
    /// Strip Fountain/Markdown-style emphasis for plain-text import
    /// (*italic*, **bold**, ***both***, _underline_), honoring backslash escapes.
    /// Matches Fountain: spaces around markers matter (no emphasis when open is followed
    /// by whitespace or close is preceded by whitespace); emphasis does not span lines
    /// (caller processes one line at a time for most elements).
    /// </summary>
    public static string StripEmphasis(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Protect escapes (Markdown convention)
        text = text.Replace("\\*", "\u0001").Replace("\\_", "\u0002");
        // Content must start and end with non-whitespace (Markdown/Fountain spacing rules).
        // Single non-space char is allowed: *a*, **b**, etc.
        // ***bold italic*** then **bold** then *italic* then _underline_
        text = Regex.Replace(text, @"\*\*\*(\S(?:[^*]*\S)?)\*\*\*", "$1");
        text = Regex.Replace(text, @"\*\*(\S(?:[^*]*\S)?)\*\*", "$1");
        text = Regex.Replace(text, @"\*(\S(?:[^*]*\S)?)\*", "$1");
        text = Regex.Replace(text, @"_(\S(?:[^_]*\S)?)_", "$1");
        return text.Replace("\u0001", "*").Replace("\u0002", "_");
    }

    public static string UnescapeFountain(string text) => StripEmphasis(text);

    private static bool LooksLikeTitlePageKeyLine(string line)
    {
        var trimmed = line.Trim();
        if (!TitleKeyLine.IsMatch(trimmed)) return false;
        // Do not treat transitions (CUT TO:, FADE TO BLACK. is not Key:) or forced >
        // as title-page metadata. "CUT TO:" matches Key:value with empty value.
        if (trimmed.StartsWith('>')) return false;
        if (IsAllCapsLine(trimmed) && TransitionEnd.IsMatch(trimmed.TrimEnd()))
            return false;
        // Title keys are typically Title Case / mixed case (Title, Draft date, Author).
        // All-caps keys with empty values are almost always body elements.
        var m = TitleKeyLine.Match(trimmed);
        var key = m.Groups[1].Value;
        var rest = m.Groups[2].Value.Trim();
        if (rest.Length == 0 && key.Any(char.IsLetter) && key.Where(char.IsLetter).All(char.IsUpper))
            return false;
        return true;
    }

    private static int ParseTitlePage(string[] lines, int start, ParseResult result)
    {
        var i = start;
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
        if (i >= lines.Length) return i;

        // Title page only if first non-blank is a real title Key: (not CUT TO: etc.)
        if (!LooksLikeTitlePageKeyLine(lines[i]))
            return i;

        string? currentKey = null;
        var valueBuf = new StringBuilder();

        void Flush()
        {
            if (currentKey is null) return;
            var v = valueBuf.ToString().Trim();
            if (v.Length > 0)
            {
                v = UnescapeFountain(v);
                if (result.TitlePage.TryGetValue(currentKey, out var existing) && existing.Length > 0)
                    result.TitlePage[currentKey] = existing + "\n" + v;
                else
                    result.TitlePage[currentKey] = v;
            }
            currentKey = null;
            valueBuf.Clear();
        }

        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (result.TitlePage.Count > 0 || currentKey is not null)
                {
                    Flush();
                    i++;
                    break; // blank line ends title page
                }
                i++;
                continue;
            }

            if (LooksLikeTitlePageKeyLine(trimmed))
            {
                Flush();
                var m = TitleKeyLine.Match(trimmed.Trim());
                currentKey = m.Groups[1].Value.Trim();
                var rest = m.Groups[2].Value.Trim();
                if (rest.Length > 0)
                    valueBuf.Append(rest);
                i++;
                continue;
            }

            // Multiline value: 3+ spaces or was tab (already expanded)
            if (currentKey is not null &&
                (raw.StartsWith("   ", StringComparison.Ordinal) || raw.StartsWith('\t')))
            {
                if (valueBuf.Length > 0) valueBuf.Append('\n');
                valueBuf.Append(raw.Trim());
                i++;
                continue;
            }

            Flush();
            break;
        }

        Flush();
        return i;
    }

    private static int ConsumeDialogueBlock(string[] lines, int i, ParseResult result)
    {
        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = raw.TrimEnd().Trim();

            // Empty line: two+ spaces on the "blank" line continues dialogue (Fountain line breaks)
            if (trimmed.Length == 0)
            {
                if (IsTwoSpaceContinue(raw) &&
                    i + 1 < lines.Length &&
                    lines[i + 1].Trim().Length > 0 &&
                    !LooksLikeNewBlock(lines, i + 1))
                {
                    // preserve intentional blank inside dialogue as newline
                    result.Elements.Add(new Element { Type = ElementType.Dialogue, Text = "" });
                    i++;
                    continue;
                }
                break;
            }

            // Parenthetical
            if (trimmed.StartsWith('(') && trimmed.Contains(')'))
            {
                var close = trimmed.IndexOf(')');
                var inside = trimmed[1..close].Trim();
                result.Elements.Add(new Element
                {
                    Type = ElementType.Parenthetical,
                    Text = UnescapeFountain(inside),
                });
                var rest = trimmed[(close + 1)..].Trim();
                if (rest.Length > 0)
                    result.Elements.Add(new Element { Type = ElementType.Dialogue, Text = UnescapeFountain(rest) });
                i++;
                continue;
            }

            // Stop at new structural block
            if (LooksLikeNewBlock(lines, i))
                break;

            result.Elements.Add(new Element
            {
                Type = ElementType.Dialogue,
                Text = UnescapeFountain(trimmed),
            });
            i++;
        }
        return i;
    }

    private static bool LooksLikeNewBlock(string[] lines, int i)
    {
        var trimmed = lines[i].TrimEnd().Trim();
        if (trimmed.Length == 0) return true;
        var prevBlank = PrevBlank(lines, i);
        var nextBlank = NextBlank(lines, i);

        if (trimmed.StartsWith('#')) return true;
        if (trimmed.StartsWith('=') && !trimmed.StartsWith("===")) return true;
        if (Regex.IsMatch(trimmed, @"^={3,}\s*$")) return true;
        if (trimmed.StartsWith('.') && trimmed.Length > 1 && char.IsLetterOrDigit(trimmed[1])) return true;
        if (trimmed.StartsWith('@')) return true;
        if (trimmed.StartsWith('!')) return true;
        if (trimmed.StartsWith('~')) return true;
        if (IsCentered(trimmed)) return true;
        if (trimmed.StartsWith('>') && !IsCentered(trimmed)) return true;
        if (prevBlank && SceneHeadingStart.IsMatch(trimmed) &&
            (nextBlank || NextIsPageTagOrSynopsis(lines, i)))
            return true;
        if (prevBlank && nextBlank && IsStandaloneTransitionLine(lines[i].TrimStart()))
            return true;
        if (prevBlank && !nextBlank && IsCharacterLine(trimmed)) return true;
        return false;
    }

    private static bool IsCentered(string trimmed)
    {
        trimmed = trimmed.Trim();
        return trimmed.StartsWith('>') && trimmed.EndsWith('<') && trimmed.Length >= 2;
    }

    /// <summary>
    /// True for a line that should be a Transition element (not Action / not a scene).
    /// Public so importers can ignore transition-only noise without inventing scenes.
    /// </summary>
    public static bool IsStandaloneTransitionLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        // Strip emphasis so **FADE IN:** still matches
        var stripped = StripEmphasis(line).TrimStart();
        var core = stripped.TrimEnd();
        if (core.Length == 0) return false;
        if (!IsAllCapsLine(core)) return false;

        // FADE IN / FADE OUT / FADE TO BLACK (optional trailing period)
        if (StandaloneFadeTransition.IsMatch(core))
            return true;

        // Classic Fountain TO: — must end with TO: (trailing spaces after colon → Action)
        return TransitionEnd.IsMatch(stripped.TrimEnd('\r', '\n'));
    }

    private static bool IsCharacterLine(string trimmed)
    {
        trimmed = trimmed.Trim();
        if (trimmed.StartsWith('@')) return true;
        // Spec: when in doubt, Action. Never treat INT/EXT scene-heading-shaped lines as Character
        // (e.g. two headings stacked without a blank line between them).
        if (SceneHeadingStart.IsMatch(trimmed)) return false;
        var core = trimmed.TrimEnd('^', ' ', '\t');
        var namePart = core.Split('(')[0].Trim();
        if (namePart.Length < 1) return false;
        if (!namePart.Any(char.IsLetter)) return false; // "23" invalid; "R2D2" ok
        // Allow multi-word ALL CAPS including apostrophes / ampersands
        // (MARLEY'S GHOST, SCROOGE & MARLEY'S, GHOST OF CHRISTMAS PAST)
        return namePart.All(c =>
            !char.IsLetter(c) || char.IsUpper(c));
    }

    /// <summary>
    /// Map curly quotes/apostrophes/dashes to ASCII so CONT'D and MARLEY'S parse reliably.
    /// </summary>
    public static string NormalizeTypographicPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return text
            .Replace('\u2018', '\'') // ‘
            .Replace('\u2019', '\'') // ’
            .Replace('\u201C', '"')  // “
            .Replace('\u201D', '"')  // ”
            .Replace('\u2013', '-')  // –
            .Replace('\u2014', '-')  // —
            .Replace('\u00A0', ' '); // nbsp
    }

    private static bool IsAllCapsLine(string s) =>
        s.Any(char.IsLetter) && s.Where(char.IsLetter).All(char.IsUpper);

    private static bool PrevBlank(string[] lines, int i)
    {
        if (i <= 0) return true;
        return string.IsNullOrWhiteSpace(lines[i - 1]);
    }

    private static bool NextBlank(string[] lines, int i)
    {
        if (i + 1 >= lines.Length) return true;
        return string.IsNullOrWhiteSpace(lines[i + 1]);
    }

    /// <summary>
    /// True when the line after a potential scene heading is a Fountain synopsis
    /// or our book page tag (so headings stay recognized without a blank line).
    /// </summary>
    private static bool NextIsPageTagOrSynopsis(string[] lines, int i)
    {
        if (i + 1 >= lines.Length) return false;
        var next = lines[i + 1].Trim();
        if (next.Length == 0) return false;
        // Synopsis: = text (not === section)
        if (next.StartsWith('=') && !next.StartsWith("===", StringComparison.Ordinal))
            return true;
        // Page notes if not yet stripped: [[page N]]
        if (Regex.IsMatch(next, @"^\[\[\s*pages?\s+\d+", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    private static bool IsTwoSpaceContinue(string raw) =>
        raw.Length >= 2 && string.IsNullOrWhiteSpace(raw) && raw.Contains("  ");

    private static (string Name, string? Ext) SplitCharacter(string line)
    {
        line = line.Trim().TrimEnd('^').Trim();
        var open = line.IndexOf('(');
        if (open > 0 && line.EndsWith(')'))
            return (line[..open].Trim(), line[open..].Trim());
        // Extension with spaces: MOM (O. S.) already handled if ends with )
        if (open > 0)
        {
            var close = line.LastIndexOf(')');
            if (close > open)
                return (line[..open].Trim(), line[open..(close + 1)].Trim());
        }
        return (line, null);
    }

    private static string? BuildCharMeta(string? ext, bool dual)
    {
        if (string.IsNullOrWhiteSpace(ext) && !dual) return null;
        if (dual && string.IsNullOrWhiteSpace(ext)) return "dual";
        if (dual) return ext + "|dual";
        return ext;
    }

    private static (string Heading, string? SceneNumber) SplitSceneNumber(string heading)
    {
        var m = SceneNumberSuffix.Match(heading);
        if (!m.Success) return (heading.Trim(), null);
        return (heading[..m.Index].Trim(), m.Groups[1].Value);
    }

    private static string PreserveActionIndent(string rawLine, string content)
    {
        // Count leading spaces on raw line (tabs already expanded)
        var lead = 0;
        while (lead < rawLine.Length && rawLine[lead] == ' ') lead++;
        if (lead == 0) return content;
        return new string(' ', lead) + content;
    }

    /// <summary>
    /// Count character cues whose extension is voice-over (Meta contains V.O. / VO).
    /// On-camera NARRATOR cues (no V.O. extension) are NOT counted as voice-over.
    /// </summary>
    public static (int VoCues, int TotalCues) CountVoiceoverCues(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return (0, 0);

        var cues = Parse(fountain).Elements
            .Where(e => e.Type == ElementType.Character)
            .ToList();
        var vo = cues.Count(e => IsVoiceoverCue(e));
        return (vo, cues.Count);
    }

    /// <summary>True when a character element is tagged voice-over (not bare NARRATOR on camera).</summary>
    public static bool IsVoiceoverCue(Element e)
    {
        if (e.Type != ElementType.Character) return false;
        var meta = e.Meta ?? "";
        // Meta holds extension e.g. "(V.O.)", "(V.O.) (CONT'D)", "(V.O.)|dual"
        if (meta.Contains("V.O.", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(meta, @"\(?\s*V\s*\.?\s*O\s*\.?\s*\)?", RegexOptions.IgnoreCase)) return true;
        // Rare: extension left in Text if parser edge case
        var text = e.Text ?? "";
        if (text.Contains("V.O.", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
