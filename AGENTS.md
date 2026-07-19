# Agent instructions — NickAndMe / Film Studio

Durable project rules for coding agents (including after session restart).  
Follow these unless the user explicitly overrides them for a task.

---

## General solutions only (any book / any cast)

**Film Studio is a product for arbitrary picture books and casts — not a Buster app.**

When debugging or implementing against a sample project (e.g. Buster / Buster2 / NickAndMe):

1. **Ship general mechanisms**, not title-specific branches.
   - Prefer prompts, cast metadata (`source_image_pages`, `description`, `visual_lock`, `wardrobe_always`),
     manifest `relevance`, and book reference images.
   - **Do not** hardcode character names (`Buster`, `Momma`, …), book titles, page numbers,
     outfit beats (pajamas), or epithets (`noodle head`) in Engine / Web / API product code.
2. **Sample data fixes are data, not product code.**
   - Editing `projects/Buster2/...` (or any one project) to re-seed plates or clean descriptions is fine
     for the user’s current project.
   - The **code path** that attaches plates, builds cast, generates portraits, etc. must work for the next book without edits.
3. **No growing special-case lists.**
   - Avoid regex / if-ladders of story-specific anti-patterns.
   - Prefer AI prompt scrubbing, style locks, and image refs over one-off string rules.
4. **Comments and examples** in code should say “hero animal”, “supporting cast”, “text-only page” —
   not a specific character name — unless documenting a unit-test fixture.
5. **Before finishing a task**, ask: *Would this still work for a different book with different cast names?*
   If not, generalize.

Buster (and other fixtures) are **eval / demo projects**, not product requirements.

---

## UI copy principles (operator-facing Blazor / product UI)

Apply to **workflow pages** users operate day to day: Adaptation, Characters, Scenes, Review, Home, Cost, and similar.  
**Configuration** (and a dedicated connection/settings area) may name providers and models when the user is *choosing* them.

### 0. No commentary or decision process on the UI

- **Do not** put agent/dev thinking on the page: why a control exists, what the backend does next, ranking rules, caps (“top 3 go to the API”), scrubbing notes, “after X, do Y” tutorials.
- **Do not** duplicate status (same error in a banner *and* a job card; same lock state on list *and* detail strip).
- Labels = short outcomes (**Save look**, **Generate → compare**, **Find characters**). Tooltips only when a label is ambiguous.
- Project selection lives on **Home** — do not re-add project pickers on workflow pages unless the user asked for multi-project on that screen.

### 0b. Technical job details are Admin-only

Operators see **short outcome status** only (e.g. “Creating portrait…”, “Portrait generation failed. Try again.”).

**Admin only** (collapsible “Details (admin)” or admin badge views):

- Job log lines such as `Character design (C# / Grok image API)…`, seed paths, `Seed mode=explicit · refs=3/3`, model names, HTTP bodies, file names under `assets/…`.
- Stacking the same error three times (list row + Current message + red alert) is forbidden — **one** operator-facing error surface.

Never show raw engine progress dumps to non-admin users on Home, Characters, Scenes, or Adaptation.

### 1. Outcome only — not mechanism

- Describe **what the user gets** (imported source, screenplay, cast, portraits, clips, movie draft).
- Adaptation flow: **Import** (screenplay file / PDF / TXT) → **Screenplay** (edit + **approve**) → **Shot plan**.
- The editable screenplay draft is the source of truth; Stage 1 / cast / shots unlock after **Looks good — continue** (sign-off).
- **Do not** explain *how* it is done: no “AI”, “vision”, “OCR”, “LLM”, “model”, “chat”, “API”, or “the system uses …”.
- Users do not care whether a step is AI, deterministic, or ffmpeg under the hood.

### 2. No provider branding on workflow UI

- Do **not** hardcode **Grok**, **Veo**, **Gemini**, **xAI** on workflow pages.
- The user may have selected **VEO** (or another provider) in Configuration — UI must stay neutral.
- Provider names belong on **Configuration** (or Settings) when selecting video/portrait services.

### 3. No project filenames or paths

- Do **not** show `scenes.json`, `blueprint*.json`, `book_full.txt`, `pipeline_config.json`, asset paths, etc. in operator copy.
- Say “this project”, “screenplay”, “shot plan”, “saved” instead.

### 4. No pipeline jargon

| Avoid | Prefer |
|-------|--------|
| plates / book plates | book pictures |
| seeds | reference images / pictures |
| scene bible / Stage 1 | screenplay (or Step 2 — Screenplay) |
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

*Last updated: 2026-07-19 — no UI commentary/decision process; general solutions only; UI outcome-only principles.*
