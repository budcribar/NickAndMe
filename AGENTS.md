# Agent instructions — NickAndMe / Film Studio

Durable project rules for coding agents (including after session restart).  
Follow these unless the user explicitly overrides them for a task.

---

## UI copy principles (operator-facing Blazor / product UI)

Apply to **workflow pages** users operate day to day: Adaptation, Characters, Scenes, Review, Home, Cost, and similar.  
**Configuration** (and a dedicated connection/settings area) may name providers and models when the user is *choosing* them.

### 1. Outcome only — not mechanism

- Describe **what the user gets** (story outline, cast, portraits, clips, movie draft).
- **Do not** explain *how* it is done: no “AI”, “vision”, “OCR”, “LLM”, “model”, “chat”, “API”, or “the system uses …”.
- Users do not care whether a step is AI, deterministic, or ffmpeg under the hood.

### 2. No provider branding on workflow UI

- Do **not** hardcode **Grok**, **Veo**, **Gemini**, **xAI** on workflow pages.
- The user may have selected **VEO** (or another provider) in Configuration — UI must stay neutral.
- Provider names belong on **Configuration** (or Settings) when selecting video/portrait services.

### 3. No project filenames or paths

- Do **not** show `scenes.json`, `blueprint*.json`, `book_full.txt`, `pipeline_config.json`, asset paths, etc. in operator copy.
- Say “this project”, “story outline”, “shot plan”, “saved” instead.

### 4. No pipeline jargon

| Avoid | Prefer |
|-------|--------|
| plates / book plates | book pictures |
| seeds | reference images / pictures |
| scene bible / Stage 1 | story outline (or Step 2 — Story outline) |
| clip plan / blueprint / Stage 2 | shot plan (or Step 3 — Shot plan) |
| VOICE LOCK | voice style |
| Sort plates with Grok | **Find characters** |
| Re-sort with Grok | **Find characters again** |
| Generate with Grok | **Generate portraits** |
| ffmpeg / concat / composite path | combine clips / rebuild scene video / movie draft |

### 5. Keep it short

- One plain sentence of help is enough.
- Prefer button labels that are verbs + outcome (**Find characters**, **Generate portraits**, **Build shot plan**).
- Connection failures: one place (“Connect service” / Settings) — not “XAI_API_KEY” on every page.

### Phrases banned on workflow pages

`Grok`, `Veo`, `Gemini`, `xAI` (except Configuration pickers),  
`AI`, `vision`, `OCR`, `LLM`, `model`, `chat`, `API key`,  
`plates`, `seeds`, `bible`, `blueprint`, `pipeline`, `VOICE LOCK`,  
`*.json`, `book_full.txt`, `ffmpeg`, `PdfPig`, `C#`, service class names.

### Configuration exception

On **Configuration** / admin runtime settings it is OK to:

- Label providers (Grok / Veo / …) for selection.
- Show model IDs as field *values*.
- Still avoid dumping raw filenames in primary labels when a friendly name works (“Shot plan file” under Advanced is OK if needed).

### About / developer docs

Slightly more technical language is OK on **About** or a collapsible “For developers” section — not on Adaptation / Characters / Scenes / Review.

---

## Related docs

| Doc | Purpose |
|-----|---------|
| `host/docs/perf-findings-2026-07.md` | Multi-user perf soak findings; optimization paused; files→DB notes |
| `host/docs/async-io-pass-plan.md` | Async I/O multipass status |
| `host/docs/loadsim-soak.md` | How to run LoadSim |

---

## Perf / soak (short)

- Prefer **Release**, fakes on, **one** Api + LoadSim process for soaks (not three Visual Studios).
- Canonical good mixed artifact: `host/loadsim-async-mixed-100x10m.json`.
- Further file caches deferred; may be moot if storage moves to DB.

---

*Last updated: 2026-07-18 — UI outcome-only principles.*
