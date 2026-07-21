# 04 — Animal vs human seed flags

## Status: **SHIPPED**

## Product
- Class: `SpeciesKindClassifier`
- Writes `species_kind` on character seeds (animal|human|other)
- Config: `FilmStudio__ClassifySpeciesKindWithChat`

## Baseline
`CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter` / `IsHumanAdultCharacter`.

## Holdout TellTaleHeartV4 (curated gold: all human)
| Metric | Baseline | AI |
|--------|----------|-----|
| accuracy | 4/6 (67%) | **5/6 (83%)** |

**Winner: AI**

## Ship decision
Clear win on human cast with relational keys; keep AI + heuristic fallback.
