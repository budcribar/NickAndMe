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

  function refreshSidePanels(id, opts) {
    opts = opts || {};
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
      var activeLine = inst._activeSceneLine || 0;
      if (!scenes.length) {
        inst.scenesEl.innerHTML = '<div class="fe-scenes-empty">No scenes yet</div>';
      } else {
        var parts = ['<ul class="fe-scenes-list">'];
        for (var i = 0; i < scenes.length; i++) {
          var s = scenes[i];
          var label = s.index + ". " + (s.heading || "Scene");
          var active = s.line === activeLine ? " is-active" : "";
          var hEnc = encodeURIComponent(s.heading || "");
          parts.push(
            '<li class="fe-scene-row' +
              active +
              '">' +
              '<button type="button" class="fe-scene-btn' +
              active +
              '" data-line="' +
              s.line +
              '" data-index="' +
              s.index +
              '" data-heading="' +
              hEnc +
              '" title="' +
              label.replace(/"/g, "&quot;") +
              '">' +
              label.replace(/</g, "&lt;") +
              "</button>" +
              '<button type="button" class="fe-scene-book" data-line="' +
              s.line +
              '" data-index="' +
              s.index +
              '" data-heading="' +
              hEnc +
              '" title="Show book text for this scene">Book</button>' +
              "</li>"
          );
        }
        parts.push("</ul>");
        inst.scenesEl.innerHTML = parts.join("");

        function decodeHeading(btn) {
          try {
            return decodeURIComponent(btn.getAttribute("data-heading") || "");
          } catch (e) {
            return btn.getAttribute("data-heading") || "";
          }
        }

        function markActive(line) {
          inst.scenesEl.querySelectorAll(".fe-scene-row").forEach(function (row) {
            var btn = row.querySelector(".fe-scene-btn");
            var on = btn && parseInt(btn.getAttribute("data-line"), 10) === line;
            row.classList.toggle("is-active", !!on);
            if (btn) btn.classList.toggle("is-active", !!on);
          });
        }

        inst.scenesEl.querySelectorAll(".fe-scene-btn").forEach(function (btn) {
          btn.addEventListener("click", function () {
            var line = parseInt(btn.getAttribute("data-line"), 10);
            var index = parseInt(btn.getAttribute("data-index"), 10);
            var heading = decodeHeading(btn);
            if (line > 0) {
              inst._activeSceneLine = line;
              gotoLine(id, line);
              markActive(line);
              // Jump only — book text is via the Book link (popup)
              if (inst.dotNet && inst.dotNet.invokeMethodAsync) {
                try {
                  inst.dotNet.invokeMethodAsync("OnSceneSelected", line, index, heading, false);
                } catch (e) {
                  /* ignore */
                }
              }
            }
          });
        });

        inst.scenesEl.querySelectorAll(".fe-scene-book").forEach(function (btn) {
          btn.addEventListener("click", function (ev) {
            ev.preventDefault();
            ev.stopPropagation();
            var line = parseInt(btn.getAttribute("data-line"), 10);
            var index = parseInt(btn.getAttribute("data-index"), 10);
            var heading = decodeHeading(btn);
            if (line > 0) {
              inst._activeSceneLine = line;
              gotoLine(id, line);
              markActive(line);
              if (inst.dotNet && inst.dotNet.invokeMethodAsync) {
                try {
                  inst.dotNet.invokeMethodAsync("OnSceneSelected", line, index, heading, true);
                } catch (e) {
                  /* ignore */
                }
              }
            }
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

    // Throttle Blazor notifications more than local preview (perf)
    if (!opts.skipDotNet && inst.dotNet && inst.dotNet.invokeMethodAsync) {
      if (inst._dotNetTimer) clearTimeout(inst._dotNetTimer);
      inst._dotNetTimer = setTimeout(function () {
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
      }, 450);
    }
  }

  function scheduleRefresh(id) {
    var inst = instances[id];
    if (!inst) return;
    if (inst._timer) clearTimeout(inst._timer);
    inst._timer = setTimeout(function () {
      refreshSidePanels(id);
    }, 180);
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
    // Scroll page layout to matching line (read-only; no edit focus on preview)
    if (inst.previewEl) {
      var el = inst.previewEl.querySelector('[data-line="' + line1Based + '"]');
      if (el) {
        el.scrollIntoView({ block: "center", behavior: "smooth" });
        el.classList.add("fp-flash");
        setTimeout(function () {
          el.classList.remove("fp-flash");
        }, 900);
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

  /**
   * Make the book fidelity floating window draggable by its header.
   * Does not block the editor — no modal backdrop.
   */
  function bindBookWindowDrag(winEl) {
    if (!winEl || winEl._feDragBound) return;
    var header = winEl.querySelector("[data-drag-handle]") || winEl.querySelector(".fe-book-window-header");
    if (!header) return;
    winEl._feDragBound = true;

    var dragging = false;
    var startX = 0;
    var startY = 0;
    var origLeft = 0;
    var origTop = 0;

    function onPointerDown(e) {
      // Don't start drag from the close button
      if (e.target && e.target.closest && e.target.closest(".fe-book-window-close")) return;
      if (e.button != null && e.button !== 0) return;
      dragging = true;
      header.classList.add("is-dragging");
      startX = e.clientX;
      startY = e.clientY;
      var rect = winEl.getBoundingClientRect();
      origLeft = rect.left;
      origTop = rect.top;
      // Switch from right/top CSS defaults to explicit left/top for free move
      winEl.style.right = "auto";
      winEl.style.left = origLeft + "px";
      winEl.style.top = origTop + "px";
      try {
        header.setPointerCapture(e.pointerId);
      } catch (err) {
        /* ignore */
      }
      e.preventDefault();
    }

    function onPointerMove(e) {
      if (!dragging) return;
      var dx = e.clientX - startX;
      var dy = e.clientY - startY;
      var left = origLeft + dx;
      var top = origTop + dy;
      var maxL = Math.max(0, window.innerWidth - 80);
      var maxT = Math.max(0, window.innerHeight - 48);
      left = Math.min(Math.max(-winEl.offsetWidth + 80, left), maxL);
      top = Math.min(Math.max(0, top), maxT);
      winEl.style.left = left + "px";
      winEl.style.top = top + "px";
    }

    function onPointerUp(e) {
      if (!dragging) return;
      dragging = false;
      header.classList.remove("is-dragging");
      try {
        header.releasePointerCapture(e.pointerId);
      } catch (err) {
        /* ignore */
      }
    }

    header.addEventListener("pointerdown", onPointerDown);
    header.addEventListener("pointermove", onPointerMove);
    header.addEventListener("pointerup", onPointerUp);
    header.addEventListener("pointercancel", onPointerUp);
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
    bindBookWindowDrag: bindBookWindowDrag,
  };
})(typeof window !== "undefined" ? window : globalThis);
