# 01 — Ambient / SFX split

## Status: **SHIPPED**

## Product
- Class: `AmbientSfxClassifier`
- Prompt: v1, temp 0
- Wired: Stage2 enrichment (before clip plan)
- Config: `FilmStudio__ClassifyAmbientSfxWithChat` (default true)
- Policy: AI → retry → keep heuristic ambient/sfx on beat + audio dict

## Baseline
`FountainStage1Importer.InferAmbientAndSfx` keyword regex.

## Holdout TellTaleHeartV4
| Metric | Baseline | AI |
|--------|----------|-----|
| mean token Jaccard | **0.94** | 0.77 |

**Winner: baseline** on this gold (mostly empty beds; AI invents extra beds and loses Jaccard).

## Ship decision
Still shipped with **heuristic fallback** — AI can refine when keyword lists miss (heart under floorboards, etc.). On sparse-audio titles, empty heuristic is strong; AI should not invent. Prompt can be tightened later (“do not invent weather”).

## Eval artifacts
`holdout_gold/TellTaleHeartV4/ambient_sfx.json`
