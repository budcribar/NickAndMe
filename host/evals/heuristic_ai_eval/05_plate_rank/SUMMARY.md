# 05 — Book plate ranking fill

## Status: **SHIPPED**

## Product
- Class: `PlateRankClassifier`
- Used from `CharacterBookPlateService.HeuristicPicksRankedAsync` after filename/illustration candidates
- Config: `FilmStudio__ClassifyPlateRankWithChat`

## Baseline
`HeuristicPicks` / name hits / early pages.

## Holdout TellTaleHeartV4
| Metric | Baseline | AI |
|--------|----------|-----|
| recall@3 (Narrator assets) | 1.00 | 1.00 |

**Winner: tie** (filenames already contain character name).

## Ship decision
Ship re-rank for multi-character books without name-in-filename; no harm when names already match.
