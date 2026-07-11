"""Film-wide cost estimation and model/resolution what-if comparison."""
from __future__ import annotations

import streamlit as st

from review_app import pipeline_api as api

st.set_page_config(page_title="Cost", page_icon="💰", layout="wide")
st.title("💰 Cost estimator")
st.caption(
    "Planning estimates from list rates in `pipeline_config.cost_estimates` — "
    "not xAI invoices. Spent assumes on-disk clips cost what current rates would charge."
)

try:
    cfg = api.get_config()
    models = api.available_video_models()
except Exception as e:
    st.error(str(e))
    st.stop()

# ---- Scenario controls ----
st.subheader("Assumptions")
c1, c2, c3, c4 = st.columns(4)
with c1:
    draft_res = st.selectbox(
        "Draft resolution (day-to-day)",
        options=["480p", "720p", "1080p"],
        index=["480p", "720p", "1080p"].index(str(cfg.get("resolution", "720p")))
        if str(cfg.get("resolution", "720p")) in ("480p", "720p", "1080p")
        else 0,
        key="cost_draft_res",
    )
with c2:
    hero_res = st.selectbox(
        "Hero / delivery resolution",
        options=["720p", "1080p", "480p"],
        index=0,
        key="cost_hero_res",
    )
with c3:
    model_labels = [
        f"{m.get('label') or m.get('model_name')} ({m.get('provider')})"
        for m in models
    ] or [f"{cfg.get('model_name')} ({cfg.get('video_provider')})"]
    # Map back
    label_to_m = {}
    if models:
        for m, lab in zip(models, model_labels):
            label_to_m[lab] = m
    else:
        label_to_m[model_labels[0]] = {
            "provider": cfg.get("video_provider"),
            "model_name": cfg.get("model_name"),
        }
    current_lab = next(
        (
            lab
            for lab, m in label_to_m.items()
            if m.get("model_name") == cfg.get("model_name")
        ),
        model_labels[0],
    )
    pick_model = st.selectbox(
        "Primary model (for report rates)",
        options=model_labels,
        index=model_labels.index(current_lab) if current_lab in model_labels else 0,
        key="cost_model",
    )
with c4:
    retries = st.number_input(
        "Avg QA retries / clip",
        min_value=0.0,
        max_value=3.0,
        value=float((cfg.get("cost_estimates") or {}).get("assume_avg_retries") or 0),
        step=0.25,
        help="Extra full regenerations assumed in estimates",
    )

# Temporarily reflect model/retries into a report without saving config
m = label_to_m[pick_model]
# monkey via estimate: film_cost_report uses eng.config — temporarily patch
eng = api.get_engine()
_prev = {
    "model_name": eng.config.get("model_name"),
    "video_provider": eng.config.get("video_provider"),
    "cost_estimates": dict(eng.config.get("cost_estimates") or {}),
}
try:
    eng.config["model_name"] = m.get("model_name") or eng.config.get("model_name")
    eng.config["video_provider"] = m.get("provider") or eng.config.get("video_provider")
    ce = dict(eng.config.get("cost_estimates") or {})
    ce["assume_avg_retries"] = float(retries)
    eng.config["cost_estimates"] = ce

    report = api.film_cost_report(
        draft_resolution=draft_res,
        hero_resolution=hero_res,
    )
finally:
    eng.config["model_name"] = _prev["model_name"]
    eng.config["video_provider"] = _prev["video_provider"]
    eng.config["cost_estimates"] = _prev["cost_estimates"]

summary = report.get("summary") or {}
st.divider()
st.subheader("Film summary")
st.caption(
    f"Provider/model for rates: **{report.get('video_provider')}** / `{report.get('model_name')}` · "
    f"Draft ${report.get('output_rate_draft')}/sec · Hero ${report.get('output_rate_hero')}/sec · "
    f"{report.get('notes')}"
)

m1, m2, m3, m4 = st.columns(4)
m1.metric("Clips on disk", f"{summary.get('clips_on_disk')}/{summary.get('clips_total')}")
m2.metric("Est. spent (on disk)", f"${summary.get('spent_usd', 0):.2f}")
m3.metric("Remaining first pass", f"${summary.get('remaining_first_pass_usd', 0):.2f}")
m4.metric("Hero upgrades left", f"${summary.get('remaining_hero_upgrade_usd', 0):.2f}")

m5, m6, m7, m8 = st.columns(4)
m5.metric(
    "Finish draft (spent + missing)",
    f"${summary.get('finish_draft_usd', 0):.2f}",
)
m6.metric(
    "Finish draft + hero rest",
    f"${summary.get('finish_draft_plus_hero_usd', 0):.2f}",
)
m7.metric(
    f"Full film @ {draft_res} (all clips)",
    f"${summary.get('full_film_all_draft_usd', 0):.2f}",
)
m8.metric(
    f"Full film @ {hero_res} (all clips)",
    f"${summary.get('full_film_all_hero_usd', 0):.2f}",
)

st.progress(
    (summary.get("clips_on_disk") or 0) / max(1, summary.get("clips_total") or 1),
    text=(
        f"Progress: {summary.get('clips_on_disk')} clips · "
        f"{summary.get('scenes_with_media')}/{summary.get('scenes_total')} scenes with media · "
        f"{summary.get('scenes_hero')} hero"
    ),
)

# ---- What-if compare ----
st.divider()
st.subheader("Compare models & resolutions")
st.caption("Same film scope — switch assumptions and compare full / remaining / regen-on-disk totals.")

default_scenarios = []
for res in ("480p", "720p", "1080p"):
    default_scenarios.append(
        {
            "label": f"{m.get('label') or m.get('model_name')} @ {res}",
            "resolution": res,
            "model_name": m.get("model_name"),
            "video_provider": m.get("provider"),
            "assume_avg_retries": float(retries),
        }
    )
# Add other models at draft + hero if list has more than one
for other in models:
    if other.get("model_name") == m.get("model_name"):
        continue
    for res in (draft_res, hero_res):
        default_scenarios.append(
            {
                "label": f"{other.get('label') or other.get('model_name')} @ {res}",
                "resolution": res,
                "model_name": other.get("model_name"),
                "video_provider": other.get("provider"),
                "assume_avg_retries": float(retries),
            }
        )

# Deduplicate by label
seen = set()
scenarios = []
for s in default_scenarios:
    if s["label"] in seen:
        continue
    seen.add(s["label"])
    scenarios.append(s)

try:
    eng.config["cost_estimates"] = {
        **(eng.config.get("cost_estimates") or {}),
        "assume_avg_retries": float(retries),
    }
    compare_rows = api.cost_scenario_compare(scenarios)
finally:
    eng.config["cost_estimates"] = _prev["cost_estimates"]

st.dataframe(
    [
        {
            "scenario": r["label"],
            "resolution": r["resolution"],
            "model": r["model_name"],
            "$/sec": r["rate_per_sec"],
            "full film $": r["full_film_usd"],
            "remaining (missing) $": r["remaining_missing_usd"],
            "regen all on-disk $": r["regen_on_disk_usd"],
        }
        for r in compare_rows
    ],
    hide_index=True,
    use_container_width=True,
)

# Highlight cheapest full film
if compare_rows:
    cheapest = min(compare_rows, key=lambda r: r["full_film_usd"])
    st.success(
        f"Cheapest full-film estimate: **{cheapest['label']}** → "
        f"**${cheapest['full_film_usd']:.2f}**"
    )

# ---- Per-scene breakdown ----
st.divider()
st.subheader("Breakdown by scene")
scene_rows = report.get("scenes") or []
filter_mode = st.radio(
    "Show",
    options=["all", "with media", "incomplete", "not hero"],
    horizontal=True,
    key="cost_scene_filter",
)
filtered = []
for r in scene_rows:
    if filter_mode == "with media" and r["clips_on_disk"] <= 0:
        continue
    if filter_mode == "incomplete" and r["clips_missing"] <= 0:
        continue
    if filter_mode == "not hero" and r["is_hero"]:
        continue
    filtered.append(r)

st.dataframe(
    [
        {
            "scene": r["scene"],
            "setting": r["setting"],
            "clips": f"{r['clips_on_disk']}/{r['clips_total']}",
            "missing": r["clips_missing"],
            "hero": r.get("hero_resolution") or ("⭐" if r["is_hero"] else ""),
            "spent $": r["spent_usd"],
            "remaining draft $": r["remaining_draft_usd"],
            "hero upgrade $": r["hero_upgrade_usd"],
            f"all @ {draft_res} $": r["all_draft_usd"],
            f"all @ {hero_res} $": r["all_hero_usd"],
        }
        for r in filtered
    ],
    hide_index=True,
    use_container_width=True,
    height=min(600, 80 + 28 * max(1, len(filtered))),
)

# Top remaining costs
st.subheader("Highest remaining first-pass costs")
top = sorted(
    [r for r in scene_rows if r["remaining_draft_usd"] > 0],
    key=lambda r: r["remaining_draft_usd"],
    reverse=True,
)[:15]
if top:
    st.dataframe(
        [
            {
                "scene": r["scene"],
                "setting": r["setting"],
                "missing clips": r["clips_missing"],
                "remaining $": r["remaining_draft_usd"],
            }
            for r in top
        ],
        hide_index=True,
        use_container_width=True,
    )
else:
    st.info("No missing clips — first pass complete for all scenes in the blueprint.")

st.divider()
st.markdown(
    """
### How to read this
| Column | Meaning |
|--------|---------|
| **Spent** | Est. cost of clips already on disk (draft rates, or hero rate if ⭐) |
| **Remaining first pass** | Est. to generate clips **not** on disk yet at draft resolution |
| **Hero upgrades left** | Est. to re-render on-disk non-hero scenes at hero resolution |
| **Finish draft + hero** | Spent + missing @ draft + hero upgrades |
| **Full film @ res** | If you generated **every** blueprint clip once at that resolution |

Edit rates under **Configuration → Cost estimates**. This page never bills xAI — it only multiplies seconds × rates.
"""
)
