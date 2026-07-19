using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilmStudio.Engine;

/// <summary>
/// Builds Grok video prompts (character variables + visual + audio) and resolves
/// character ref image paths for reference-to-video / image-to-video.
/// </summary>
public static class ClipVideoPromptBuilder
{
    /// <summary>
    /// Soft cap for video prompt length. Video Imagine does not document a 1M-token window
    /// (that is chat); we remove the old 4k hard truncate and allow large structured prompts.
    /// </summary>
    public const int MaxPromptChars = 100_000;

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
        public string Prompt { get; init; } = "";
        /// <summary>Ordered character ref images for reference_images / &lt;IMAGE_n&gt; tags.</summary>
        public IReadOnlyList<string> ReferenceImagePaths { get; init; } = Array.Empty<string>();
        /// <summary>When set, image-to-video start frame (e.g. last frame of previous clip).</summary>
        public string? StartFrameImagePath { get; init; }
        public string Mode { get; init; } = "fresh";
        public IReadOnlyList<string> CharacterKeys { get; init; } = Array.Empty<string>();
        public string PromptLogSummary { get; init; } = "";
    }

    public static PromptBuildResult Build(
        JsonElement clipEl,
        string projectDir,
        IReadOnlyDictionary<string, CharacterProfile>? characters = null,
        string? previousClipVisualPrompt = null,
        string? previousClipVideoPath = null,
        string? startFrameImagePath = null,
        int maxRefs = 5)
    {
        var visual = clipEl.TryGetProperty("visual_prompt", out var vp)
            ? (vp.GetString() ?? "").Trim()
            : "";
        visual = SimplifyVisual(visual);

        var cont = clipEl.TryGetProperty("veo_continuation_source", out var ce)
            ? (ce.GetString() ?? "none")
            : "none";
        var hasPrevVideo = !string.IsNullOrWhiteSpace(previousClipVideoPath) &&
                           File.Exists(previousClipVideoPath);
        var continueFromPrev =
            string.Equals(cont, "extend_previous", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(startFrameImagePath) ||
            hasPrevVideo;

        // video-extend = official Imagine continue; continue = start-frame fallback; fresh = new
        var mode = hasPrevVideo ? "video-extend"
            : continueFromPrev ? "continue"
            : "fresh";
        var keys = ClipCharacterKeys(clipEl);
        characters ??= new Dictionary<string, CharacterProfile>(StringComparer.OrdinalIgnoreCase);

        // Resolve character ref paths (skip voice-only)
        var refPaths = FindCharacterRefPaths(clipEl, projectDir, maxRefs);
        // Map path order to IMAGE tags only when NOT using start-frame or video-extend
        // (API forbids image/video-continue + reference_images together).
        var useReferenceImages =
            string.IsNullOrWhiteSpace(startFrameImagePath) &&
            !hasPrevVideo &&
            refPaths.Count > 0;

        var imageTagByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (useReferenceImages)
        {
            var refKeys = ClipCharacterKeys(clipEl)
                .Where(k => !IsVoiceOnlyKey(k, characters))
                .OrderBy(CharacterRefPriority)
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var orderedPaths = new List<string>();
            var n = 0;
            foreach (var key in refKeys)
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

        var framing = mode switch
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

        var sb = new StringBuilder();

        // --- Character variables (descriptions the model can bind to IMAGE tags / names) ---
        var varBlock = BuildCharacterVariablesBlock(keys, characters, imageTagByKey, useReferenceImages);
        if (!string.IsNullOrWhiteSpace(varBlock))
        {
            sb.AppendLine(varBlock);
            sb.AppendLine();
        }

        if ((mode is "continue" or "video-extend") &&
            !string.IsNullOrWhiteSpace(previousClipVisualPrompt))
        {
            sb.AppendLine(
                mode == "video-extend"
                    ? "PREVIOUS CLIP (already provided as video input — continue from its last frame):"
                    : "PREVIOUS CLIP (context — match look & continue motion from its end):");
            sb.AppendLine(SimplifyVisual(previousClipVisualPrompt!));
            sb.AppendLine();
        }

        var audioBlock = BuildAudioBlock(clipEl, characters);
        if (!string.IsNullOrWhiteSpace(audioBlock))
        {
            sb.AppendLine(audioBlock);
            sb.AppendLine();
        }

        sb.AppendLine(framing);
        sb.AppendLine();

        // Rewrite Character_* tokens to include IMAGE tags where available
        var visualTagged = visual;
        foreach (var (key, tag) in imageTagByKey)
        {
            // "the dog from <IMAGE_1> (Character_Buster)" style — keep key for seed traceability
            visualTagged = Regex.Replace(
                visualTagged,
                Regex.Escape(key),
                $"{key} {tag}",
                RegexOptions.IgnoreCase);
        }

        sb.AppendLine("THIS CLIP:");
        sb.Append(visualTagged);

        var prompt = sb.ToString().Trim();
        if (prompt.Length > MaxPromptChars)
            prompt = prompt[..(MaxPromptChars - 1)] + "…";

        var summary =
            $"mode={mode} chars={keys.Count} refs={refPaths.Count} " +
            $"startFrame={(startFrameImagePath is null ? "no" : "yes")} " +
            $"promptLen={prompt.Length}" +
            (previousClipVideoPath is { Length: > 0 }
                ? $" prevVideo={Path.GetFileName(previousClipVideoPath)}"
                : "");

        return new PromptBuildResult
        {
            Prompt = prompt,
            ReferenceImagePaths = useReferenceImages ? refPaths : Array.Empty<string>(),
            StartFrameImagePath = startFrameImagePath,
            Mode = mode,
            CharacterKeys = keys,
            PromptLogSummary = summary,
        };
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
                    VoiceOnly = k.Contains("narrator", StringComparison.OrdinalIgnoreCase),
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
        var keys = ClipCharacterKeys(clipEl);
        var paths = new List<string>();
        keys = keys
            .OrderBy(CharacterRefPriority)
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in keys)
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
        return found.ToList();
    }

    private static string? ResolveCharacterRefPath(string projectDir, string key)
    {
        var charDir = Path.Combine(projectDir, "assets", "characters");
        var name = ProjectStore.CharacterRefFileName(key);
        var full = Path.Combine(charDir, name);
        if (File.Exists(full) && new FileInfo(full).Length >= 256)
            return full;
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

        if (!string.IsNullOrWhiteSpace(dialogue) && !string.IsNullOrWhiteSpace(speaker))
        {
            var isNarrator = speaker.Contains("narrator", StringComparison.OrdinalIgnoreCase) ||
                             delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought";
            // Keep full dialogue — long context is fine for evaluation
            var quote = dialogue.Trim();
            if (isNarrator)
            {
                return
                    $"AUDIO: REQUIRED native Grok off-camera voiceover. {speaker} narrates " +
                    $"exactly: \"{quote}\". Do not lip-sync on-screen cast to this VO. " +
                    $"Secondary layer = soft room tone / Foley.{voiceLock}";
            }
            return
                $"AUDIO: REQUIRED native Grok dialogue. {speaker} ON CAMERA lip-syncs " +
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

    private static bool IsVoiceOnlyKey(string key, IReadOnlyDictionary<string, CharacterProfile>? characters)
    {
        if (key.Contains("narrator", StringComparison.OrdinalIgnoreCase))
            return true;
        if (characters is not null &&
            characters.TryGetValue(key, out var p) &&
            p.VoiceOnly)
            return true;
        return false;
    }
}
