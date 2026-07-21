# Holdout results — `JungleBook`

Generated: 2026-07-21 16:03:14Z

Gold notes:
- **Species gold is curated** for JungleBook (wolf/bear/panther/tiger… = animal; Mowgli/hunters = human).
- Ambient / on-screen cast / extend gold still start as **heuristic proxy** → baseline is advantaged on those three.
- **No plate assets** under `JungleBook/assets/characters` → plate recall@3 is N/A (both 0).
- No `cast_seeds.json`; cast keys come from fountain Stage1 import.

| Task | Metric | Baseline heuristic | AI | Winner |
|------|--------|--------------------|----|--------|
| 1 Ambient/SFX | mean token Jaccard | 1.00 | 0.83 | **baseline** |
| 2 On-screen cast | mean set F1 | 1.00 | 0.85 | **baseline** |
| 3 Extend/hard-cut | accuracy | 24/24 (100%) | 24/24 (100%) | **tie** |
| 4 Species kind | accuracy | 13/44 (30%) | 28/44 (64%) | **AI** |
| 5 Plate rank | recall@3 | 0.00 | 0.00 | **tie** |

## Product wiring
| Task | Service | Stage2 / plates |
|------|---------|-----------------|
| Ambient/SFX | `AmbientSfxClassifier` | Stage2 enrich |
| On-screen cast | `OnScreenCastClassifier` | Stage2 enrich → clip cast |
| Extend/cut | `ExtendCutClassifier` | Stage2 `cut_decision` → ForceNone |
| Species | `SpeciesKindClassifier` | Stage2 seed field `species_kind` |
| Plate rank | `PlateRankClassifier` | CharacterBookPlateService re-rank |

Policy for all: **AI preferred → retry → heuristic fallback** (not when AI merely disagrees).
