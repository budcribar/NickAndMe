# 02 — On-screen cast keys

## Status: **SHIPPED**

## Product
- Class: `OnScreenCastClassifier`
- Writes `characters_on_screen` on beats; Stage2 `ClipCastTokens` prefers that list
- Config: `FilmStudio__ClassifyOnScreenCastWithChat`

## Baseline
`ClipVideoPromptBuilder.InferKeysFromProse` substring match.

## Holdout TellTaleHeartV4
| Metric | Baseline | AI |
|--------|----------|-----|
| mean set F1 | 1.00 | 1.00 |

**Winner: tie** (TellTale names appear literally in visuals; both hit curated keys).

## Ship decision
Ship AI path for harder books (epithets, plurals, pronouns). Fallback remains prose match.
