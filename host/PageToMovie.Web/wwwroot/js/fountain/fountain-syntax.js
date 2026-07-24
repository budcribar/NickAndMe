/**
 * Lightweight Fountain line classifier for preview, scene list, and editor highlighting.
 * Spec-aligned enough for live UX; server C# parser remains authoritative on sign-off.
 */
(function (global) {
  "use strict";

  var SCENE_START =
    /^(INT\.?\/EXT\.?|INT\/EXT|I\.?\/E\.?|INT\.?|EXT\.?|EST\.?)(\s|\.|$)/i;
  var SCENE_NUM = /\s+#([^#]+)#\s*$/;

  function classifyLines(text) {
    // Keep original line numbers for editor jump; mask boneyard lines as blank.
    var raw = (text || "").replace(/\r\n/g, "\n").replace(/\r/g, "\n");
    // Remove boneyard content but preserve newlines so line indexes stay aligned
    raw = raw.replace(/\/\*[\s\S]*?\*\//g, function (m) {
      return m.replace(/[^\n]/g, " ");
    });
    var lines = raw.split("\n");
    var result = [];
    var i = 0;
    var inTitle = true;
    var titleBlankSeen = false;
    var pendingDual = false;

    function prevBlank(idx) {
      return idx <= 0 || !String(lines[idx - 1] || "").trim();
    }
    function nextBlank(idx) {
      return idx + 1 >= lines.length || !String(lines[idx + 1] || "").trim();
    }
    function isAllCaps(s) {
      var letters = s.replace(/[^A-Za-zÀ-ÖØ-öø-ÿ]/g, "");
      return letters.length > 0 && letters === letters.toUpperCase();
    }
    function isCharacterName(s) {
      var core = s.replace(/\s*\^$/, "").trim();
      var name = core.split("(")[0].trim();
      if (!name || !/[A-Za-zÀ-ÖØ-öø-ÿ]/.test(name)) return false;
      if (SCENE_START.test(name)) return false;
      return isAllCaps(name);
    }

    // Skip title page roughly
    while (i < lines.length && inTitle) {
      var t = lines[i].trim();
      if (!t) {
        if (titleBlankSeen || result.some(function (r) { return r.type === "title"; })) {
          inTitle = false;
          break;
        }
        titleBlankSeen = true;
        result.push({ type: "blank", text: lines[i], line: i + 1 });
        i++;
        continue;
      }
      if (/^[A-Za-z][A-Za-z0-9 ]*:\s*/.test(t) && !/TO:\s*$/i.test(t) && !/^CUT TO/i.test(t)) {
        result.push({ type: "title", text: lines[i], line: i + 1 });
        i++;
        continue;
      }
      // indented continuation of title value
      if (/^\s{3,}/.test(lines[i]) || /^\t/.test(lines[i])) {
        result.push({ type: "title", text: lines[i], line: i + 1 });
        i++;
        continue;
      }
      inTitle = false;
      break;
    }

    while (i < lines.length) {
      var line = lines[i];
      var trimmed = line.trim();
      var lineNo = i + 1;

      if (!trimmed) {
        result.push({ type: "blank", text: line, line: lineNo });
        i++;
        continue;
      }
      if (trimmed === "^") {
        pendingDual = true;
        result.push({ type: "dual-marker", text: line, line: lineNo });
        i++;
        continue;
      }
      if (/^={3,}\s*$/.test(trimmed)) {
        result.push({ type: "pagebreak", text: line, line: lineNo });
        i++;
        continue;
      }
      if (/^#+/.test(trimmed)) {
        result.push({ type: "section", text: line, line: lineNo, depth: (trimmed.match(/^#+/) || [""])[0].length });
        i++;
        continue;
      }
      if (trimmed.charAt(0) === "=" && !trimmed.startsWith("===")) {
        result.push({ type: "synopsis", text: line, line: lineNo });
        i++;
        continue;
      }
      // Whole-line or inline notes [[...]]
      if (/\[\[/.test(trimmed) && /\]\]/.test(trimmed) && trimmed.replace(/\[\[[\s\S]*?\]\]/g, "").trim().length === 0) {
        result.push({ type: "note", text: line, line: lineNo });
        i++;
        continue;
      }
      if (trimmed.charAt(0) === "~") {
        result.push({ type: "lyric", text: line, line: lineNo });
        i++;
        continue;
      }
      if (trimmed.charAt(0) === "!") {
        result.push({ type: "action", text: line, line: lineNo, forced: true });
        i++;
        continue;
      }
      if (trimmed.charAt(0) === "." && trimmed.length > 1 && /[A-Za-z0-9]/.test(trimmed.charAt(1))) {
        var sh = trimmed.slice(1).replace(SCENE_NUM, "").trim();
        result.push({ type: "scene", text: line, line: lineNo, heading: sh, forced: true });
        i++;
        continue;
      }
      if (trimmed.charAt(0) === "@") {
        var dual = pendingDual || /\^\s*$/.test(trimmed);
        pendingDual = false;
        result.push({
          type: "character",
          text: line,
          line: lineNo,
          name: trimmed.slice(1).replace(/\s*\^$/, "").trim(),
          dual: dual,
          forced: true,
        });
        i++;
        // dialogue block
        i = consumeDialogue(lines, i, result);
        continue;
      }
      if (/^>.*<$/.test(trimmed)) {
        result.push({ type: "centered", text: line, line: lineNo });
        i++;
        continue;
      }
      if (trimmed.charAt(0) === ">" && !/^>.*<$/.test(trimmed)) {
        result.push({ type: "transition", text: line, line: lineNo, forced: true });
        i++;
        continue;
      }

      var pb = prevBlank(i);
      var nb = nextBlank(i);

      if (pb && nb && SCENE_START.test(trimmed)) {
        var heading = trimmed.replace(SCENE_NUM, "").trim();
        result.push({ type: "scene", text: line, line: lineNo, heading: heading });
        i++;
        continue;
      }
      // Transition: all-caps, blank before/after, ends with TO: (no trailing spaces after colon)
      var leftTrim = line.replace(/^\s+/, "");
      if (pb && nb && isAllCaps(leftTrim.trim()) && /TO:$/i.test(leftTrim)) {
        result.push({ type: "transition", text: line, line: lineNo });
        i++;
        continue;
      }
      if ((pb || pendingDual) && !nb && isCharacterName(trimmed)) {
        var dual2 = pendingDual || /\^\s*$/.test(trimmed);
        pendingDual = false;
        var name = trimmed.replace(/\s*\^$/, "").trim();
        result.push({ type: "character", text: line, line: lineNo, name: name.split("(")[0].trim(), dual: dual2 });
        i++;
        i = consumeDialogue(lines, i, result);
        continue;
      }

      pendingDual = false;
      result.push({ type: "action", text: line, line: lineNo });
      i++;
    }

    return result;

    function consumeDialogue(lines, start, out) {
      var j = start;
      while (j < lines.length) {
        var L = lines[j];
        var tr = L.trim();
        if (!tr) {
          // two spaces continue dialogue
          if (/^\s{2,}$/.test(L) || (L.length >= 2 && !L.trim() && L.indexOf("  ") >= 0)) {
            out.push({ type: "dialogue", text: L, line: j + 1, empty: true });
            j++;
            continue;
          }
          break;
        }
        if (tr.charAt(0) === "(" && tr.indexOf(")") >= 0) {
          out.push({ type: "parenthetical", text: L, line: j + 1 });
          j++;
          continue;
        }
        // stop at new structural
        if (tr.charAt(0) === "#" || tr.charAt(0) === "=" || tr.charAt(0) === "~" ||
            tr.charAt(0) === "!" || tr.charAt(0) === "@" || tr === "^" ||
            /^={3,}\s*$/.test(tr) ||
            (tr.charAt(0) === "." && tr.length > 1 && /[A-Za-z0-9]/.test(tr.charAt(1))) ||
            (tr.charAt(0) === ">" && /^>.*<$/.test(tr)) ||
            (tr.charAt(0) === ">" && !/^>.*<$/.test(tr))) {
          break;
        }
        var pBlank = j <= 0 || !String(lines[j - 1] || "").trim();
        var nBlank = j + 1 >= lines.length || !String(lines[j + 1] || "").trim();
        if (pBlank && nBlank && SCENE_START.test(tr)) break;
        if (pBlank && nBlank && isAllCaps(tr) && /TO:$/i.test(tr.replace(/^\s+/, ""))) break;
        if (pBlank && !nBlank && isCharacterName(tr)) break;

        out.push({ type: "dialogue", text: L, line: j + 1 });
        j++;
      }
      return j;
    }
  }

  function extractScenes(classified) {
    var scenes = [];
    for (var i = 0; i < classified.length; i++) {
      var c = classified[i];
      if (c.type === "scene") {
        scenes.push({
          line: c.line,
          heading: c.heading || c.text.replace(/^\./, "").replace(SCENE_NUM, "").trim(),
          index: scenes.length + 1,
        });
      }
    }
    return scenes;
  }

  function stripEmphasis(s) {
    if (!s) return s;
    s = s.replace(/\\\*/g, "\u0001").replace(/\\_/g, "\u0002");
    s = s.replace(/\*\*\*([^*\n]+)\*\*\*/g, "$1");
    s = s.replace(/\*\*([^*\n]+)\*\*/g, "$1");
    s = s.replace(/\*([^*\n]+)\*/g, "$1");
    s = s.replace(/_([^_\n]+)_/g, "$1");
    return s.replace(/\u0001/g, "*").replace(/\u0002/g, "_");
  }

  function displayText(c) {
    var t = c.text || "";
    var tr = t.trim();
    switch (c.type) {
      case "scene":
        return stripEmphasis((c.heading || tr.replace(/^\./, "").replace(SCENE_NUM, "")).trim());
      case "character":
        return stripEmphasis(tr.replace(/^@/, "").replace(/\s*\^$/, "").trim());
      case "parenthetical":
        return stripEmphasis(tr);
      case "dialogue":
      case "action":
      case "lyric":
        return stripEmphasis(tr.replace(/^!/, "").replace(/^~/, "").trim());
      case "transition":
        return stripEmphasis(tr.replace(/^>/, "").trim());
      case "centered":
        return stripEmphasis(tr.replace(/^>/, "").replace(/<$/, "").trim());
      case "section":
        return stripEmphasis(tr.replace(/^#+\s*/, ""));
      case "synopsis":
        return stripEmphasis(tr.replace(/^=\s*/, ""));
      default:
        return stripEmphasis(tr);
    }
  }

  global.FountainSyntax = {
    classifyLines: classifyLines,
    extractScenes: extractScenes,
    stripEmphasis: stripEmphasis,
    displayText: displayText,
  };
})(typeof window !== "undefined" ? window : globalThis);
