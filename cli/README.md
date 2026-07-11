# CLI — command-line frontend

Thin interactive frontend over `renderer/`.

```bash
# from workspace root
python -m cli
```

Uses the active project from `projects/workspace.json`, or:

```bash
# Windows
set NICKANDME_PROJECT=projects/NickAndMe

# bash
export NICKANDME_PROJECT=projects/NickAndMe
```

Scene / clip selectors support e.g. `2 clip 3 regen`.
