# Learning loop (review → smarter prompts)

## Phases

| Phase | What |
|--------|------|
| **P0** | Host `_learning/review_events.jsonl` + project edit log; before/after on apply; `regen_after_review` on single-clip gen |
| **P1** | Admin **Learning** page + `/api/admin/learning/*` insights/events |
| **P2** | Versioned prompt packs in `prompts/packs/` + activate gen / auto_review |
| **P3** | Propose rules from last N fails (chat or offline fallback) |
| **P4** | Project `project_rules.json` suggest from categories → admin approve → inject into gen/auto-review |

## Paths

- Host events: `{WorkspaceRoot}/_learning/review_events.jsonl`
- Packs: `{WorkspaceRoot}/prompts/packs/manifest.json` + `*.txt`
- Project rules: `{project}/project_rules.json`

## Operator flow

1. Users Fail / Auto Review / Apply / Regen on Review.
2. Admin opens **Learning**, filters by project, reads fail categories.
3. Optionally **Propose from fails**, paste into a new gen pack version, **Activate**.
4. For a project: **Suggest from fails** → **Approve** house rules (project-scoped).
