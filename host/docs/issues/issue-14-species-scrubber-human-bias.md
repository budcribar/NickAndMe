# Issue 14 — Cross-species style scrub rewrites toward human adult

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | **fixed** |
| Branch | `fix/issue-14-species-scrubber-human-bias` |
| Related files | `host/PageToMovie.Engine/CharacterVisualTextScrubber.cs`; `CharacterDesignService.cs` |

## Problem

Cross-species "matching X CG look" rewrites always used human adult medium language. For animal-to-animal style matching this forced "human adult — not an animal" into animal seed prose (wrong medium/species).

## Fix implemented

1. **Default** replacement is neutral: `same stylized picture-book soft-3D medium as the film` (no species/age).
2. **Optional** human disambiguation phrase for known human seeds only: `(human — not an animal)` — not "human adult".
3. **`ScrubVisualProse` / `SoftenCrossSpeciesStyleLanguage`** take `disambiguateCrossSpeciesAsHuman` (default false).
4. **Portrait prompts** (`CharacterDesignService`) pass the flag only when `isHumanAdult && !isAnimal`.
5. Generic seed scrubbing (ProjectStore, etc.) stays neutral so animals are never rewritten as human.

## Suggested fix (original)

Rewrite to neutral shared-medium phrasing without assuming human unless age_band/description already indicates human.
