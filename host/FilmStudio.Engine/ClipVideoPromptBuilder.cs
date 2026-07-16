using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilmStudio.Engine;

/// <summary>
/// Builds Grok video prompts (visual + audio) and resolves character ref image paths for multi-ref gen.
/// </summary>
public static class ClipVideoPromptBuilder
{
    public static string BuildPrompt(
        JsonElement clipEl,
        string projectDir,
        Dictionary<string, Dictionary<string, string>>? characterVoiceByKey = null,
        string mode = "fresh")
    {
        var visual = clipEl.TryGetProperty("visual_prompt", out var vp)
            ? (vp.GetString() ?? "").Trim()
            : "";
        visual = SimplifyVisual(visual);

        // Style lock for humans
        if ((visual.Contains("Character_Mom", StringComparison.OrdinalIgnoreCase) ||
             visual.Contains("Character_Daddy", StringComparison.OrdinalIgnoreCase) ||
             visual.Contains("Character_Dad", StringComparison.OrdinalIgnoreCase)) &&
            !visual.Contains("STYLE LOCK", StringComparison.OrdinalIgnoreCase))
        {
            visual =
                "STYLE LOCK: stylized 3D animated children's picture-book CG " +
                "(same render family as the cartoon dog) -- not photoreal, not live-action. " +
                visual;
        }

        var framing = mode == "continue"
            ? "Continue seamlessly from the provided starting frame with the same character identity, " +
              "wardrobe, and location. Natural camera motion only — do not invent a new establishing shot. " +
              "Show clear progressive motion for the primary action (not a frozen pose)."
            : "Follow the camera framing and location in this prompt exactly. " +
              "Prioritize the PRIMARY subject and ONE clear action with visible motion; " +
              "background characters may stay mostly still.";

        var audioBlock = BuildAudioBlock(clipEl, characterVoiceByKey);
        var idLock = IdentityLockClause(visual, projectDir);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(audioBlock))
            parts.Add(audioBlock);
        if (!string.IsNullOrWhiteSpace(idLock) && !visual.Contains("IDENTITY LOCK", StringComparison.OrdinalIgnoreCase))
            parts.Add(idLock);
        parts.Add(framing);
        parts.Add(visual);

        var prompt = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (prompt.Length > 4000)
            prompt = prompt[..3990] + "…";
        return prompt;
    }

    public static List<string> FindCharacterRefPaths(
        JsonElement clipEl,
        string projectDir,
        int maxRefs = 3)
    {
        var keys = ClipCharacterKeys(clipEl);
        var charDir = Path.Combine(projectDir, "assets", "characters");
        var paths = new List<string>();

        // Prefer animal/hero refs first (Buster before Mom/Dad)
        keys = keys
            .OrderBy(k => CharacterRefPriority(k))
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in keys)
        {
            if (paths.Count >= maxRefs) break;
            if (IsVoiceOnlyKey(key)) continue;
            var name = ProjectStore.CharacterRefFileName(key);
            var full = Path.Combine(charDir, name);
            if (File.Exists(full) && new FileInfo(full).Length >= 256)
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

    private static string BuildAudioBlock(
        JsonElement clipEl,
        Dictionary<string, Dictionary<string, string>>? voices)
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
            voices is not null &&
            voices.TryGetValue(speaker, out var vinfo) &&
            vinfo.TryGetValue("voice_profile", out var profile) &&
            !string.IsNullOrWhiteSpace(profile))
        {
            voiceLock = $" VOICE LOCK {speaker}: {profile}";
        }

        if (!string.IsNullOrWhiteSpace(dialogue) && !string.IsNullOrWhiteSpace(speaker))
        {
            var isNarrator = speaker.Contains("narrator", StringComparison.OrdinalIgnoreCase) ||
                             delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought";
            var quote = dialogue.Length > 120 ? dialogue[..117] + "…" : dialogue;
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

    private static string IdentityLockClause(string visual, string projectDir)
    {
        var keys = new List<string>();
        foreach (Match m in Regex.Matches(visual, @"Character_[A-Za-z0-9_]+"))
        {
            if (!keys.Contains(m.Value) && !IsVoiceOnlyKey(m.Value))
                keys.Add(m.Value);
        }
        if (keys.Count == 0) return "";
        var bits = keys.Take(3).Select(k => $"match locked ref for {k}");
        return "IDENTITY LOCK: " + string.Join("; ", bits) + ".";
    }

    private static string SimplifyVisual(string visual)
    {
        // Drop ultra-long tech suffixes already present; keep body
        visual = Regex.Replace(visual, @"\s+", " ").Trim();
        return visual;
    }

    private static int CharacterRefPriority(string key)
    {
        var k = key.ToLowerInvariant();
        if (k.Contains("buster") || k.Contains("dog") || k.Contains("noodle")) return 0;
        if (k.Contains("mom") || k.Contains("dad") || k.Contains("human")) return 2;
        return 1;
    }

    private static bool IsVoiceOnlyKey(string key) =>
        key.Contains("narrator", StringComparison.OrdinalIgnoreCase);
}
