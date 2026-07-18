/**
 * CodeMirror 5 Fountain editor + preview/scene-nav interop for Blazor.
 * Expects global CodeMirror, FountainSyntax, FountainPreview.
 */
(function (global) {
  "use strict";

  var instances = {};

  function defineFountainMode() {
    if (!global.CodeMirror || global.CodeMirror.modes.fountain) return;
    global.CodeMirror.defineMode("fountain", function () {
      var SCENE =
        /^(INT\.?\/EXT\.?|INT\/EXT|I\.?\/E\.?|INT\.?|EXT\.?|EST\.?)(\s|\.|$)/i;
      return {
        startState: function () {
          return { inDialogue: false };
        },
        token: function (stream, state) {
          if (stream.sol()) {
            var line = stream.string;
            var t = line.trim();
            if (!t) {
              state.inDialogue = false;
              stream.skipToEnd();
              return null;
            }
            if (/^#+/.test(t)) {
              stream.skipToEnd();
              state.inDialogue = false;
              return "header";
            }
            if (t.charAt(0) === "=" && !t.startsWith("===")) {
              stream.skipToEnd();
              state.inDialogue = false;
              return "comment";
            }
            if (/^={3,}\s*$/.test(t)) {
              stream.skipToEnd();
              state.inDialogue = false;
              return "hr";
            }
            if (t.charAt(0) === "~") {
              stream.skipToEnd();
              return "string";
            }
            if (t.charAt(0) === "!") {
              stream.skipToEnd();
              state.inDialogue = false;
              return "variable-2";
            }
            if (t.charAt(0) === "." && t.length > 1 && /[A-Za-z0-9]/.test(t.charAt(1))) {
              stream.skipToEnd();
              state.inDialogue = false;
              return "keyword";
            }
            if (t.charAt(0) === "@") {
              stream.skipToEnd();
              state.inDialogue = true;
              return "def";
            }
            if (/^>.*<$/.test(t)) {
              stream.skipToEnd();
              state.inDialogue = false;
              return "atom";
            }
            if (t.charAt(0) === ">") {
              stream.skipToEnd();
              state.inDialogue = false;
              return "atom";
            }
            if (SCENE.test(t)) {
              stream.skipToEnd();
              state.inDialogue = false;
              return "keyword";
            }
            if (/TO:\s*$/i.test(t) && t === t.toUpperCase()) {
              stream.skipToEnd();
              state.inDialogue = false;
              return "atom";
            }
            // Character: all caps, likely
            var name = t.replace(/\s*\^$/, "").split("(")[0].trim();
            var letters = name.replace(/[^A-Za-z]/g, "");
            if (
              letters.length > 0 &&
              letters === letters.toUpperCase() &&
              !state.inDialogue &&
              name.length < 50
            ) {
              // Heuristic: if next line exists and not blank-ish, treat as character
              stream.skipToEnd();
              state.inDialogue = true;
              return "def";
            }
            if (t.charAt(0) === "(" && t.indexOf(")") >= 0 && state.inDialogue) {
              stream.skipToEnd();
              return "comment";
            }
            if (state.inDialogue) {
              stream.skipToEnd();
              return "string";
            }
            stream.skipToEnd();
            return "variable-2";
          }
          stream.skipToEnd();
          return null;
        },
      };
    });
    global.CodeMirror.defineMIME("text/x-fountain", "fountain");
  }

  function refreshSidePanels(id) {
    var inst = instances[id];
    if (!inst) return;
    var text = inst.cm.getValue();
    var FS = global.FountainSyntax;
    var FP = global.FountainPreview;

    if (inst.previewEl && FP) {
      inst.previewEl.innerHTML = FP.render(text);
    }

    if (inst.scenesEl && FS) {
      var scenes = FS.extractScenes(FS.classifyLines(text));
      if (!scenes.length) {
        inst.scenesEl.innerHTML = '<div class="fe-scenes-empty">No scenes yet</div>';
      } else {
        var parts = ['<ul class="fe-scenes-list">'];
        for (var i = 0; i < scenes.length; i++) {
          var s = scenes[i];
          var label = s.index + ". " + (s.heading || "Scene");
          parts.push(
            '<li><button type="button" class="fe-scene-btn" data-line="' +
              s.line +
              '" title="' +
              label.replace(/"/g, "&quot;") +
              '">' +
              label.replace(/</g, "&lt;") +
              "</button></li>"
          );
        }
        parts.push("</ul>");
        inst.scenesEl.innerHTML = parts.join("");
        inst.scenesEl.querySelectorAll(".fe-scene-btn").forEach(function (btn) {
          btn.addEventListener("click", function () {
            var line = parseInt(btn.getAttribute("data-line"), 10);
            if (line > 0) gotoLine(id, line);
          });
        });
      }
      inst._scenes = scenes;
    }

    // soft validation
    var warnings = [];
    if (!text || !text.trim()) warnings.push("empty");
    else {
      var sceneCount = (inst._scenes && inst._scenes.length) || 0;
      if (sceneCount === 0) warnings.push("no_scenes");
      if (text.trim().length < 40) warnings.push("very_short");
    }
    inst._warnings = warnings;

    if (inst.dotNet && inst.dotNet.invokeMethodAsync) {
      try {
        inst.dotNet.invokeMethodAsync(
          "OnEditorChanged",
          text,
          warnings,
          (inst._scenes && inst._scenes.length) || 0
        );
      } catch (e) {
        /* circuit may be down */
      }
    }
  }

  function scheduleRefresh(id) {
    var inst = instances[id];
    if (!inst) return;
    if (inst._timer) clearTimeout(inst._timer);
    inst._timer = setTimeout(function () {
      refreshSidePanels(id);
    }, 200);
  }

  /**
   * @param {string} id unique instance id
   * @param {HTMLElement} hostEl element that will contain CM
   * @param {string} initialText
   * @param {any} dotNetRef DotNetObjectReference
   * @param {HTMLElement|null} previewEl
   * @param {HTMLElement|null} scenesEl
   * @param {boolean} readOnly
   */
  function init(id, hostEl, initialText, dotNetRef, previewEl, scenesEl, readOnly) {
    if (!global.CodeMirror) {
      console.error("CodeMirror not loaded");
      return false;
    }
    defineFountainMode();
    dispose(id);

    hostEl.innerHTML = "";
    var ta = document.createElement("textarea");
    ta.value = initialText || "";
    hostEl.appendChild(ta);

    var cm = global.CodeMirror.fromTextArea(ta, {
      mode: "fountain",
      lineNumbers: true,
      lineWrapping: true,
      indentWithTabs: false,
      smartIndent: false,
      tabSize: 4,
      indentUnit: 4,
      readOnly: !!readOnly,
      extraKeys: {
        Tab: function (cm) {
          cm.replaceSelection("    ", "end");
        },
      },
    });

    cm.setSize("100%", "100%");
    cm.on("change", function () {
      scheduleRefresh(id);
    });

    instances[id] = {
      cm: cm,
      hostEl: hostEl,
      previewEl: previewEl || null,
      scenesEl: scenesEl || null,
      dotNet: dotNetRef,
      _timer: null,
      _scenes: [],
      _warnings: [],
    };

    // Initial paint after layout
    setTimeout(function () {
      cm.refresh();
      refreshSidePanels(id);
    }, 50);

    return true;
  }

  function getValue(id) {
    var inst = instances[id];
    return inst ? inst.cm.getValue() : "";
  }

  function setValue(id, text) {
    var inst = instances[id];
    if (!inst) return;
    var cur = inst.cm.getValue();
    if (cur === text) return;
    var scroll = inst.cm.getScrollInfo();
    inst.cm.setValue(text || "");
    inst.cm.scrollTo(scroll.left, scroll.top);
    refreshSidePanels(id);
  }

  function insertAtCursor(id, snippet) {
    var inst = instances[id];
    if (!inst) return;
    var cm = inst.cm;
    var doc = cm.getDoc();
    var cur = doc.getCursor();
    // Ensure surrounding blank lines for structure inserts
    var text = snippet || "";
    doc.replaceRange(text, cur);
    cm.focus();
    scheduleRefresh(id);
  }

  function gotoLine(id, line1Based) {
    var inst = instances[id];
    if (!inst) return;
    var line = Math.max(0, (line1Based | 0) - 1);
    inst.cm.setCursor({ line: line, ch: 0 });
    inst.cm.focus();
    inst.cm.scrollIntoView({ line: line, ch: 0 }, 80);
    // Highlight preview
    if (inst.previewEl) {
      var el = inst.previewEl.querySelector('[data-line="' + line1Based + '"]');
      if (el) {
        el.scrollIntoView({ block: "center", behavior: "smooth" });
        el.classList.add("fp-flash");
        setTimeout(function () {
          el.classList.remove("fp-flash");
        }, 1200);
      }
    }
  }

  function setReadOnly(id, ro) {
    var inst = instances[id];
    if (!inst) return;
    inst.cm.setOption("readOnly", !!ro);
  }

  function refresh(id) {
    var inst = instances[id];
    if (!inst) return;
    inst.cm.refresh();
    refreshSidePanels(id);
  }

  function getWarnings(id) {
    var inst = instances[id];
    return inst ? inst._warnings || [] : [];
  }

  function dispose(id) {
    var inst = instances[id];
    if (!inst) return;
    if (inst._timer) clearTimeout(inst._timer);
    try {
      inst.cm.toTextArea();
    } catch (e) {
      /* ignore */
    }
    delete instances[id];
  }

  global.fountainEditor = {
    init: init,
    getValue: getValue,
    setValue: setValue,
    insertAtCursor: insertAtCursor,
    gotoLine: gotoLine,
    setReadOnly: setReadOnly,
    refresh: refresh,
    getWarnings: getWarnings,
    dispose: dispose,
  };
})(typeof window !== "undefined" ? window : globalThis);
