# 03 — Extend vs hard-cut

## Status: **SHIPPED**

## Product
- Class: `ExtendCutClassifier`
- Writes `cut_decision` = hard_cut|extend; `ForceNone` honors it first
- Config: `FilmStudio__ClassifyExtendCutWithChat`

## Baseline
First clip / location change / big_action / establishing / verb regex (same spirit as ForceNone).

## Holdout TellTaleHeartV4
| Metric | Baseline | AI |
|--------|----------|-----|
| accuracy | 24/24 (100%) | 24/24 (100%) |

**Winner: tie** (gold aligned with baseline on this sample).

## Ship decision
Ship AI for edge cases (VO after on-camera, soft scene breaks). ForceNone still applies if cut_decision missing.
