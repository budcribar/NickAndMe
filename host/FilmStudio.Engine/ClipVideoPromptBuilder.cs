using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilmStudio.Engine;

/// <summary>
/// Builds Grok video prompts (character variables + visual + audio) and resolves
/// character ref image paths for reference-to-video / image-to-video.
/// Owns CAST COUNT; strips Stage2-embedded count from action prose.
/// </summary>
public static class ClipVideoPromptBuilder
{
    /// <summary>
    /// Hint only — we do not pre-truncate to this. On API "prompt too long" errors the client
    /// shortens and retries (see <see cref="ShortenPromptForRetry"/>).
    /// </summary>
    public const int MaxPromptChars = 1_000_000;

    public sealed class CharacterProfile
    {
        public string Key { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Description { get; init; } = "";
        public string VisualLock { get; init; } = "";
        public string VoiceProfile { get; init; } = "";
        public string VoiceLabel { get; init; } = "";
        public bool VoiceOnly { get; init; }
    }

    public sealed class PromptBuildResult
    {
        /// <summary>Full flat prompt sent to the video API (may include learning addenda).</summary>
        public string Prompt { get; init; } = "";
        /// <summary>Ordered character ref images for reference_images / &lt;IMAGE_n&gt; tags.</summary>
        public IReadOnlyList<string> ReferenceImagePaths { get; init; } = Array.Empty<string>();
        /// <summary>When set, image-to-video start frame (e.g. last frame of previous clip).</summary>
        public string? StartFrameImagePath { get; init; }
        public string Mode { get; init; } = "fresh";
        /// <summary>All character keys referenced (includes voice-only speakers).</summary>
        public IReadOnlyList<string> CharacterKeys { get; init; } = Array.Empty<string>();
        /// <summary>On-screen only keys used for CAST COUNT and ref attachment.</summary>
        public IReadOnlyList<string> OnScreenKeys { get; init; } = Array.Empty<string>();
        public int CastCount { get; init; }
        public string StyleHead { get; init; } = "";
        public string CharacterVariables { get; init; } = "";
        public string AudioBlock { get; init; } = "";
        public string ContinuityBlock { get; init; } = "";
        public string ActionText { get; init; } = "";
        public string CastCountLine { get; init; } = "";
        /// <summary>Whether locked refs were attached to the API payload for this build.</summary>
        public bool RefsAttachedToApi { get; init; }
        public string PromptLogSummary { get; init; } = "";

        public PromptBuildResult WithPrompt(string prompt, string? summarySuffix = null) => new()
        {
            Prompt = prompt,
            ReferenceImagePaths = ReferenceImagePaths,
            StartFrameImagePath = StartFrameImagePath,
            Mode = Mode,
            CharacterKeys = CharacterKeys,
            OnScreenKeys = OnScreenKeys,
            CastCount = CastCount,
            StyleHead = StyleHead,
            CharacterVariables = CharacterVariables,
            AudioBlock = AudioBlock,
            ContinuityBlock = ContinuityBlock,
            ActionText = ActionText,
            CastCountLine = CastCountLine,
            RefsAttachedToApi = RefsAttachedToApi,
            PromptLogSummary = string.IsNullOrWhiteSpace(summarySuffix)
                ? PromptLogSummary
                : PromptLogSummary + summarySuffix,
        };
    }

    public static PromptBuildResult Build(
        JsonElement clipEl,
        string projectDir,
        IReadOnlyDictionary<string, CharacterProfile>? characters = null,
        string? previousClipVisualPrompt = null,
        string? previousClipVideoPath = null,
        string? startFrameImagePath = null,
        int maxRefs = 5,
        string? styleHead = null)
    {
        characters ??= new Dictionary<string, CharacterProfile>(StringComparer.OrdinalIgnoreCase);

        // Mode follows actual media inputs, not blueprint cont alone.
        // Cast-change reseed (PR2) clears previousClipVideoPath while blueprint may still say
        // extend_previous — that must be fresh+refs, not continue-without-frame.
        var hasPrevVideo = !string.IsNullOrWhiteSpace(previousClipVideoPath) &&
                           File.Exists(previousClipVideoPath);
        var hasStartFrame = !string.IsNullOrWhiteSpace(startFrameImagePath) &&
                            File.Exists(startFrameImagePath!);

        var mode = hasPrevVideo ? "video-extend"
            : hasStartFrame ? "continue"
            : "fresh";

        var allKeys = ResolveClipCharacterKeys(clipEl, characters);
        var onScreenKeys = allKeys
            .Where(k => !IsVoiceOnlyKey(k, characters))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rawVisual = clipEl.TryGetProperty("visual_prompt", out var vp)
            ? (vp.GetString() ?? "").Trim()
            : "";
        var actionText = SanitizeActionText(rawVisual, onScreenKeys);

        var refPaths = FindCharacterRefPathsForKeys(onScreenKeys, projectDir, maxRefs);
        var useReferenceImages =
            string.IsNullOrWhiteSpace(startFrameImagePath) &&
            !hasPrevVideo &&
            refPaths.Count > 0;

        var imageTagByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (useReferenceImages)
        {
            var orderedPaths = new List<string>();
            var n = 0;
            foreach (var key in onScreenKeys.OrderBy(CharacterRefPriority)
                         .ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                if (orderedPaths.Count >= maxRefs) break;
                var path = ResolveCharacterRefPath(projectDir, key);
                if (path is null) continue;
                n++;
                orderedPaths.Add(path);
                imageTagByKey[key] = $"<IMAGE_{n}>";
            }
            refPaths = orderedPaths;
        }

        var style = (styleHead ?? ExtractStyleHead(rawVisual) ?? "").Trim();
        var varBlock = BuildCharacterVariablesBlock(allKeys, characters, imageTagByKey, useReferenceImages);
        var audioBlock = BuildAudioBlock(clipEl, characters);

        var continuityBlock = mode switch
        {
            "video-extend" =>
                "CONTINUITY: This is a seamless EXTENSION of the provided previous video. " +
                "Pick up from its last frame. Same character identity, wardrobe, lighting, and location. " +
                "Natural progressive motion only — do not invent a new establishing shot or redesign faces/outfits.",
            "continue" =>
                "CONTINUITY: Continue seamlessly from the provided starting frame (end of previous clip). " +
                "Same character identity, wardrobe, lighting, and location. Natural progressive motion only — " +
                "do not invent a new establishing shot or redesign faces/outfits.",
            _ =>
                "Follow the camera framing and location in this prompt exactly. " +
                "Prioritize the PRIMARY subject and ONE clear action with visible motion; " +
                "background characters may stay mostly still.",
        };

        // video-extend cannot attach locked plates (API continues from previous video only).
        // Reinforce identity from CHARACTER VARIABLES text so faces/wardrobe do not drift.
        if (mode is "video-extend" or "continue")
            continuityBlock += IdentityReinforceBlock(onScreenKeys, useReferenceImages);

        if (!string.IsNullOrWhiteSpace(previousClipVisualPrompt) &&
            mode is "continue" or "video-extend")
        {
            var prevClean = SanitizeActionText(previousClipVisualPrompt!, onScreenKeys);
            continuityBlock =
                (mode == "video-extend"
                    ? "PREVIOUS CLIP (already provided as video input — continue from its last frame):\n"
                    : "PREVIOUS CLIP (context — match look & continue motion from its end):\n") +
                prevClean + "\n\n" + continuityBlock;
        }
        else if (!string.IsNullOrWhiteSpace(previousClipVisualPrompt) && mode == "fresh")
        {
            // Cast-change reseed: no video input, but keep prior clip prose for location/lighting only.
            var prevClean = SanitizeActionText(previousClipVisualPrompt!, onScreenKeys);
            continuityBlock =
                "CONTEXT (prior clip in scene — new cast plate refs attached; match location/lighting if still valid; " +
                "identity from CHARACTER VARIABLES + locked plates only):\n" +
                prevClean + "\n\n" + continuityBlock;
        }

        var castCountLine = onScreenKeys.Count > 0
            ? $"CAST COUNT: exactly {onScreenKeys.Count} distinct on-screen character identity(ies) only — " +
              string.Join(", ", onScreenKeys) +
              ". Do not invent extra people, duplicate faces, or crowd extras not listed."
            : "";

        var actionTagged = actionText;
        foreach (var (key, tag) in imageTagByKey)
        {
            actionTagged = Regex.Replace(
                actionTagged,
                Regex.Escape(key),
                $"{key} {tag}",
                RegexOptions.IgnoreCase);
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(style))
        {
            sb.AppendLine(style.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase)
                ? style
                : "STYLE LOCK: " + style);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(varBlock))
        {
            sb.AppendLine(varBlock);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(castCountLine))
        {
            sb.AppendLine(castCountLine);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(audioBlock))
        {
            sb.AppendLine(audioBlock);
            sb.AppendLine();
        }

        sb.AppendLine(continuityBlock);
        sb.AppendLine();
        sb.AppendLine("THIS CLIP:");
        sb.AppendLine(
            "End cleanly when the spoken line and primary action finish — " +
            "do not hold a frozen pose or empty silence after dialogue.");
        sb.Append(actionTagged);

        var prompt = sb.ToString().Trim();
        IReadOnlyList<string> attached = useReferenceImages ? refPaths : Array.Empty<string>();

        var summary =
            $"mode={mode} chars={allKeys.Count} onScreen={onScreenKeys.Count} " +
            $"refs={attached.Count} startFrame={(startFrameImagePath is null ? "no" : "yes")} " +
            $"promptLen={prompt.Length}" +
            (previousClipVideoPath is { Length: > 0 }
                ? $" prevVideo={Path.GetFileName(previousClipVideoPath)}"
                : "");

        return new PromptBuildResult
        {
            Prompt = prompt,
            ReferenceImagePaths = attached,
            StartFrameImagePath = startFrameImagePath,
            Mode = mode,
            CharacterKeys = allKeys,
            OnScreenKeys = onScreenKeys,
            CastCount = onScreenKeys.Count,
            StyleHead = style,
            CharacterVariables = varBlock,
            AudioBlock = audioBlock,
            ContinuityBlock = continuityBlock,
            ActionText = actionTagged,
            CastCountLine = castCountLine,
            RefsAttachedToApi = useReferenceImages && attached.Count > 0,
            PromptLogSummary = summary,
        };
    }

    /// <summary>
    /// Remove Stage2-embedded CAST COUNT so the builder owns a single count line.
    /// Ensures each on-screen key appears at least once in action prose.
    /// </summary>
    public static string SanitizeActionText(string visual, IReadOnlyList<string>? onScreenKeys = null)
    {
        if (string.IsNullOrWhiteSpace(visual)) return "";
        var v = visual.Trim();
        v = Regex.Replace(v, @"\s*/\s*\d+p[^/]*24fps\s*$", "", RegexOptions.IgnoreCase).Trim();
        v = Regex.Replace(
            v,
            @"\bCAST COUNT:\s*exactly\s+\d+[^.]*\.\s*(?:No extra people\.\s*)?",
            "",
            RegexOptions.IgnoreCase);
        v = Regex.Replace(v, @"\bNo extra people\.\s*", "", RegexOptions.IgnoreCase);
        v = SimplifyVisual(v);
        if (onScreenKeys is { Count: > 0 })
        {
            foreach (var key in onScreenKeys)
            {
                if (!v.Contains(key, StringComparison.OrdinalIgnoreCase))
                    v = $"{v} {key} is on screen.".Trim();
            }
        }
        return v.Trim();
    }

    /// <summary>Prefer plan <c>characters_on_screen</c>, then token scan, then prose→keys.</summary>
    public static List<string> ResolveClipCharacterKeys(
        JsonElement clipEl,
        IReadOnlyDictionary<string, CharacterProfile>? characters = null)
    {
        var found = new List<string>();
        void Add(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            key = key.Trim();
            if (!key.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)) return;
            if (found.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))) return;
            found.Add(key);
        }

        if (clipEl.TryGetProperty("characters_on_screen", out var cos) &&
            cos.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in cos.EnumerateArray())
                Add(x.GetString());
        }

        foreach (var k in ClipCharacterKeys(clipEl))
            Add(k);

        if (characters is { Count: > 0 } &&
            clipEl.TryGetProperty("visual_prompt", out var vp))
        {
            foreach (var key in InferKeysFromProse(vp.GetString() ?? "", characters))
                Add(key);
        }

        return found;
    }

    /// <summary>
    /// Map free-form names in prose to Character_* keys using display names / key suffixes.
    /// </summary>
    public static List<string> InferKeysFromProse(
        string prose,
        IReadOnlyDictionary<string, CharacterProfile> characters)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(prose) || characters.Count == 0) return list;
        var text = prose.ToLowerInvariant();

        var officerKeys = characters.Keys
            .Where(k => k.Contains("Officer", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (officerKeys.Count > 0 &&
            Regex.IsMatch(text, @"\b(three|3)\s+officers?\b|\bofficers?\s+sit\b|\bthe officers\b"))
        {
            foreach (var k in officerKeys)
            {
                if (!list.Contains(k, StringComparer.OrdinalIgnoreCase))
                    list.Add(k);
            }
        }

        foreach (var (key, prof) in characters)
        {
            if (list.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(prof.DisplayName))
                names.Add(prof.DisplayName.Trim());
            var suffix = key.Replace("Character_", "", StringComparison.OrdinalIgnoreCase)
                .Replace('_', ' ').Trim();
            if (suffix.Length > 0) names.Add(suffix);
            if (key.Contains("Old_Man", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("OldMan", StringComparison.OrdinalIgnoreCase))
                names.Add("old man");
            if (key.Contains("Narrator", StringComparison.OrdinalIgnoreCase))
                names.Add("narrator");

            foreach (var n in names)
            {
                if (n.Length < 3) continue;
                if (text.Contains(n.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    list.Add(key);
                    break;
                }
            }
        }

        return list;
    }

    /// <summary>Pull leading STYLE LOCK sentence from plan visual if present.</summary>
    public static string? ExtractStyleHead(string visual)
    {
        if (string.IsNullOrWhiteSpace(visual)) return null;
        var m = Regex.Match(
            visual,
            @"STYLE LOCK:\s*([^.]+\.)",
            RegexOptions.IgnoreCase);
        return m.Success ? ("STYLE LOCK: " + m.Groups[1].Value.Trim()) : null;
    }
    /// <summary>Legacy entry used by older call sites.</summary>
    public static string BuildPrompt(
        JsonElement clipEl,
        string projectDir,
        Dictionary<string, Dictionary<string, string>>? characterVoiceByKey = null,
        string mode = "fresh")
    {
        Dictionary<string, CharacterProfile>? profiles = null;
        if (characterVoiceByKey is not null)
        {
            profiles = new Dictionary<string, CharacterProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in characterVoiceByKey)
            {
                profiles[k] = new CharacterProfile
                {
                    Key = k,
                    VoiceProfile = v.TryGetValue("voice_profile", out var p) ? p : "",
                    VoiceLabel = v.TryGetValue("voice_label", out var l) ? l : "",
                    VoiceOnly = false,
                };
            }
        }

        var result = Build(clipEl, projectDir, profiles);
        return result.Prompt;
    }

    public static List<string> FindCharacterRefPaths(
        JsonElement clipEl,
        string projectDir,
        int maxRefs = 5)
    {
        var keys = ResolveClipCharacterKeys(clipEl)
            .Where(k => !IsVoiceOnlyKey(k, null))
            .ToList();
        return FindCharacterRefPathsForKeys(keys, projectDir, maxRefs);
    }

    public static List<string> FindCharacterRefPathsForKeys(
        IReadOnlyList<string> keys,
        string projectDir,
        int maxRefs = 5)
    {
        if (maxRefs <= 0 || string.IsNullOrWhiteSpace(projectDir))
            return new List<string>();
        maxRefs = Math.Min(maxRefs, 32);

        var paths = new List<string>();
        foreach (var key in keys
                     .OrderBy(CharacterRefPriority)
                     .ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (paths.Count >= maxRefs) break;
            if (IsVoiceOnlyKey(key, null)) continue;
            var full = ResolveCharacterRefPath(projectDir, key);
            if (full is not null)
                paths.Add(full);
        }
        return paths;
    }

    public static List<string> ClipCharacterKeys(JsonElement clipEl)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in Regex.Matches(text, @"Character_[A-Za-z0-9_]+"))
                found.Add(m.Value);
        }
        if (clipEl.TryGetProperty("visual_prompt", out var vp))
            Scan(vp.GetString());
        if (clipEl.TryGetProperty("primary_subject", out var ps))
            Scan(ps.GetString());
        if (clipEl.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object)
        {
            if (ap.TryGetProperty("speaker", out var sp))
                Scan(sp.GetString());
        }
        if (clipEl.TryGetProperty("characters_on_screen", out var cos) && cos.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in cos.EnumerateArray())
                Scan(x.GetString());
        }
        return found.ToList();
    }

    private static string? ResolveCharacterRefPath(string projectDir, string key) =>
        ResolveCharacterRefPathPublic(projectDir, key);

    /// <summary>Resolve locked <c>*_ref.png</c> for a character (canonical + aliases).</summary>
    public static string? ResolveCharacterRefPathPublic(string projectDir, string key)
    {
        var charDir = Path.Combine(projectDir, "assets", "characters");
        foreach (var name in ProjectStore.CharacterRefFileCandidates(key))
        {
            var full = Path.Combine(charDir, name);
            if (File.Exists(full) && new FileInfo(full).Length >= 64)
                return full;
        }
        return null;
    }

    private static string BuildCharacterVariablesBlock(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, CharacterProfile> characters,
        IReadOnlyDictionary<string, string> imageTagByKey,
        bool useImageTags)
    {
        if (keys.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("CHARACTER VARIABLES (use these identities consistently; do not redesign faces or wardrobe):");
        var any = false;
        foreach (var key in keys)
        {
            characters.TryGetValue(key, out var p);
            var display = !string.IsNullOrWhiteSpace(p?.DisplayName)
                ? p!.DisplayName
                : key.Replace("Character_", "").Replace('_', ' ');
            var tag = useImageTags && imageTagByKey.TryGetValue(key, out var t) ? $" {t}" : "";
            var desc = p?.Description?.Trim() ?? "";
            var vlock = p?.VisualLock?.Trim() ?? "";
            var voice = p?.VoiceProfile?.Trim() ?? "";
            if (p?.VoiceOnly == true || IsVoiceOnlyKey(key, characters))
            {
                sb.AppendLine(
                    $"- {key}{tag} [{display}] VOICE ONLY — not on screen." +
                    (voice.Length > 0 ? $" Voice: {voice}" : ""));
                any = true;
                continue;
            }

            var line = $"- {key}{tag} [{display}]:";
            if (desc.Length > 0) line += $" {desc}";
            if (vlock.Length > 0) line += $" Visual lock: {vlock}";
            if (voice.Length > 0) line += $" Voice: {voice}";
            if (useImageTags && tag.Length > 0)
                line += $" Match appearance of reference {tag.Trim()} exactly.";
            sb.AppendLine(line);
            any = true;
        }
        return any ? sb.ToString().TrimEnd() : "";
    }

    private static string BuildAudioBlock(
        JsonElement clipEl,
        IReadOnlyDictionary<string, CharacterProfile>? characters)
    {
        if (!clipEl.TryGetProperty("audio_payload", out var audio) ||
            audio.ValueKind != JsonValueKind.Object)
            return "";

        var speaker = audio.TryGetProperty("speaker", out var sp) ? sp.GetString() ?? "" : "";
        var dialogue = audio.TryGetProperty("dialogue", out var dlg) ? dlg.GetString() ?? "" : "";
        var delivery = (audio.TryGetProperty("delivery", out var del) ? del.GetString() ?? "none" : "none")
            .ToLowerInvariant();
        var sfx = audio.TryGetProperty("sfx", out var sx) ? sx.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(dialogue) && string.IsNullOrWhiteSpace(sfx))
            return "";

        var voiceLock = "";
        if (!string.IsNullOrWhiteSpace(speaker) &&
            characters is not null &&
            characters.TryGetValue(speaker, out var prof) &&
            !string.IsNullOrWhiteSpace(prof.VoiceProfile))
        {
            voiceLock = $" VOICE LOCK {speaker}: {prof.VoiceProfile}";
        }

        if (!string.IsNullOrWhiteSpace(dialogue))
        {
            var who = string.IsNullOrWhiteSpace(speaker) ? "SPEAKER" : speaker.Trim();
            var isNarrator = who.Contains("narrator", StringComparison.OrdinalIgnoreCase) ||
                             delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought";
            // Keep full dialogue — long context is fine for evaluation
            var quote = dialogue.Trim();
            if (isNarrator)
            {
                return
                    $"AUDIO: REQUIRED native Grok off-camera voiceover. {who} narrates " +
                    $"exactly: \"{quote}\". Do not lip-sync on-screen cast to this VO. " +
                    $"Secondary layer = soft room tone / Foley.{voiceLock}";
            }
            return
                $"AUDIO: REQUIRED native Grok dialogue. {who} ON CAMERA lip-syncs " +
                $"exactly: \"{quote}\". Other mouths closed. Speech intelligible; never silent.{voiceLock}";
        }

        if (!string.IsNullOrWhiteSpace(sfx))
            return $"AUDIO: ambient/Foley only — {sfx}. No dialogue.";
        return "";
    }

    private static string SimplifyVisual(string visual)
    {
        visual = Regex.Replace(visual, @"\s+", " ").Trim();
        return visual;
    }

    private static int CharacterRefPriority(string key)
    {
        var k = key.ToLowerInvariant();
        if (Regex.IsMatch(k, @"(^|_)(dog|cat|bear|fox|rabbit|bunny|mouse|bird|horse|pig|wolf|owl)(s|es)?($|_)"))
            return 0;
        if (Regex.IsMatch(k, @"(mom|mum|mother|dad|daddy|father|parent|human)"))
            return 2;
        return 1;
    }


    /// <summary>
    /// When API cannot attach locked refs (video-extend), reinforce identity from CHARACTER VARIABLES text.
    /// </summary>
    private static string IdentityReinforceBlock(IReadOnlyList<string> onScreenKeys, bool refsAttached)
    {
        if (refsAttached || onScreenKeys.Count == 0) return "";
        return " IDENTITY: Match locked plate descriptions in CHARACTER VARIABLES exactly — " +
               "do not drift to illustration, anime, cartoon, or a different face/wardrobe. " +
               "On-screen: " + string.Join(", ", onScreenKeys) + ".";
    }

    private static bool IsVoiceOnlyKey(string key, IReadOnlyDictionary<string, CharacterProfile>? characters)
    {
        // Prefer explicit profile / cast seed flag. Do NOT force VOICE ONLY merely because
        // the key contains "Narrator" — confessor roles are often on camera (e.g. Tell-Tale Heart).
        if (characters is not null &&
            characters.TryGetValue(key, out var p))
            return p.VoiceOnly;
        return false;
    }

    /// <summary>
    /// True when an API/error message indicates the prompt exceeded context or length limits.
    /// Used to decide shorten-and-retry (not permanent fail).
    /// </summary>
    public static bool IsPromptTooLongError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message;
        // Common xAI / OpenAI-style and HTTP body phrases
        if (m.Contains("prompt too long", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("prompt length exceeds", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("exceeds the maximum allowed length", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("context length", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("maximum context", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("max context", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("token limit", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("too many tokens", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("maximum length", StringComparison.OrdinalIgnoreCase) &&
            m.Contains("prompt", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("maximum allowed length", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("payload too large", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("request entity too large", StringComparison.OrdinalIgnoreCase)) return true;
        // xAI video often returns 4096 char hard cap in the error body
        if (m.Contains("4096", StringComparison.Ordinal) &&
            m.Contains("length", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(m, @"\b413\b") &&
            (m.Contains("large", StringComparison.OrdinalIgnoreCase) ||
             m.Contains("size", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    /// <summary>
    /// Progressive shorten for API length retries. Prefer dropping house-rule addenda first,
    /// then cap total length while keeping the head (character locks + framing).
    /// <paramref name="attempt"/> is 1-based (first retry = 1).
    /// </summary>
    public static string ShortenPromptForRetry(string prompt, int attempt)
    {
        if (string.IsNullOrEmpty(prompt)) return prompt;
        attempt = Math.Max(1, attempt);
        var p = prompt;

        // Step 1+: drop gen pack / project house rules (appended at gen time)
        p = StripLearningAddenda(p);

        // xAI video often hard-caps ~4096 characters — hit that early on retry
        const int videoHardCap = 4000;

        if (attempt == 1)
        {
            if (p.Length < prompt.Length && p.Length <= videoHardCap)
                return p;
            return HeadCap(p, Math.Min(videoHardCap, (int)(prompt.Length * 0.75)));
        }

        // Later attempts: tighter caps (chars), keep head where identity/action live
        var caps = new[] { 0, 0, videoHardCap, 3200, 2400, 1800, 1200 };
        var cap = attempt < caps.Length ? caps[attempt] : 1000;
        if (p.Length <= cap)
            return p;
        return HeadCap(p, cap);
    }

    private static string StripLearningAddenda(string prompt)
    {
        var markers = new[]
        {
            "\n# Film Studio gen pack",
            "\n# Film Studio gen pack (active addendum)",
            "\nPROJECT HOUSE RULES",
            "\nApply these house rules when building clip video prompts:",
        };
        var cut = -1;
        foreach (var m in markers)
        {
            var i = prompt.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (cut < 0 || i < cut))
                cut = i;
        }
        if (cut < 0) return prompt.TrimEnd();
        return prompt[..cut].TrimEnd();
    }

    private static string HeadCap(string prompt, int maxChars)
    {
        if (prompt.Length <= maxChars) return prompt;
        if (maxChars < 64) maxChars = 64;
        var head = prompt[..maxChars];
        // Prefer break at paragraph / sentence
        var nl = head.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (nl > maxChars * 2 / 3) head = head[..nl];
        else
        {
            var sp = head.LastIndexOf(' ');
            if (sp > maxChars * 2 / 3) head = head[..sp];
        }
        return head.TrimEnd() + "\n[prompt shortened after API length limit — retry]";
    }
}
