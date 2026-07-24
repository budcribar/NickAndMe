/**
 * Render classified Fountain lines to simple screenplay HTML.
 */
(function (global) {
  "use strict";

  function escapeHtml(s) {
    return String(s || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function render(text) {
    var FS = global.FountainSyntax;
    if (!FS) return "<p class='fp-empty'>Preview unavailable</p>";

    var classified = FS.classifyLines(text);
    if (!classified.length) {
      return "<p class='fp-empty'>Start writing — preview appears here.</p>";
    }

    var html = ['<div class="fp-page">'];
    for (var i = 0; i < classified.length; i++) {
      var c = classified[i];
      var disp = FS.displayText(c);
      var lineAttr = ' data-line="' + c.line + '"';

      switch (c.type) {
        case "blank":
          html.push('<div class="fp-blank"' + lineAttr + "></div>");
          break;
        case "title":
          html.push('<div class="fp-title"' + lineAttr + ">" + escapeHtml(disp) + "</div>");
          break;
        case "scene":
          html.push('<div class="fp-scene"' + lineAttr + ' id="fp-scene-' + c.line + '">' + escapeHtml(disp) + "</div>");
          break;
        case "action":
          html.push('<div class="fp-action"' + lineAttr + ">" + escapeHtml(disp) + "</div>");
          break;
        case "character":
          html.push(
            '<div class="fp-character' + (c.dual ? " fp-dual" : "") + '"' + lineAttr + ">" +
              escapeHtml(disp) +
              (c.dual ? ' <span class="fp-dual-mark">(simultaneous)</span>' : "") +
              "</div>"
          );
          break;
        case "parenthetical":
          html.push('<div class="fp-paren"' + lineAttr + ">" + escapeHtml(disp) + "</div>");
          break;
        case "dialogue":
          if (c.empty) html.push('<div class="fp-dialogue fp-dialogue-gap"' + lineAttr + ">&nbsp;</div>");
          else html.push('<div class="fp-dialogue"' + lineAttr + ">" + escapeHtml(disp) + "</div>");
          break;
        case "transition":
          html.push('<div class="fp-transition"' + lineAttr + ">" + escapeHtml(disp) + "</div>");
          break;
        case "centered":
          html.push('<div class="fp-centered"' + lineAttr + ">" + escapeHtml(disp) + "</div>");
          break;
        case "lyric":
          html.push('<div class="fp-lyric"' + lineAttr + ">" + escapeHtml(disp) + "</div>");
          break;
        case "note":
          html.push(
            '<div class="fp-note"' +
              lineAttr +
              ">" +
              escapeHtml(disp.replace(/^\[\[NOTE\]\]\s*/i, "Note: ")) +
              "</div>"
          );
          break;
        case "section":
          html.push('<div class="fp-section"' + lineAttr + ">" + escapeHtml(disp) + "</div>");
          break;
        case "synopsis":
          html.push('<div class="fp-synopsis"' + lineAttr + ">" + escapeHtml(disp) + "</div>");
          break;
        case "pagebreak":
          html.push('<hr class="fp-pagebreak"' + lineAttr + " />");
          break;
        case "dual-marker":
          break;
        default:
          html.push('<div class="fp-action"' + lineAttr + ">" + escapeHtml(disp) + "</div>");
      }
    }
    html.push("</div>");
    return html.join("");
  }

  global.FountainPreview = { render: render };
})(typeof window !== "undefined" ? window : globalThis);
