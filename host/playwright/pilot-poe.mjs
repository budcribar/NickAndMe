/**
 * Film Studio Playwright pilot — Poe "Tell-Tale Heart"
 *
 * Phase A (default): fakes ON — create project → import → fountain → cast → shots
 *                    → gen scene 1 → human-like review + auto-review → Admin learning
 * Phase B (optional REAL_SCENE1=1): restart API without fakes and regen scene 1 at 480p
 *
 * Env:
 *   WEB_URL=http://localhost:5079
 *   API_URL=http://127.0.0.1:5088
 *   PROJECT_NAME=PoePilot
 *   HEADED=1
 *   REAL_SCENE1=0|1
 *   FAIL_RATE=0.25          fraction of clips to fail (human QC)
 *   ARTIFACTS=./artifacts
 */
import { chromium } from "playwright";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { spawn } from "child_process";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const WEB_URL = (process.env.WEB_URL || "http://localhost:5079").replace(/\/$/, "");
const API_URL = (process.env.API_URL || "http://127.0.0.1:5088").replace(/\/$/, "");
const PROJECT_NAME = process.env.PROJECT_NAME || `PoePilot_${new Date().toISOString().slice(0, 10).replace(/-/g, "")}`;
const HEADED = process.env.HEADED === "1" || process.env.HEADED === "true";
const REAL_SCENE1 = process.env.REAL_SCENE1 === "1" || process.env.REAL_SCENE1 === "true";
const FAIL_RATE = Math.min(0.9, Math.max(0, Number(process.env.FAIL_RATE || "0.25")));
const ARTIFACTS = path.resolve(process.env.ARTIFACTS || path.join(__dirname, "artifacts", runId()));
const BOOK_FIXTURE = path.join(__dirname, "fixtures", "tell_tale_heart.txt");
const FOUNTAIN_FIXTURE = path.join(__dirname, "fixtures", "tell_tale_heart.fountain");
// Prefer Fountain import for deterministic cinematic structure (book→fountain is free-form).
const IMPORT_FIXTURE =
  process.env.IMPORT_MODE === "book" ? BOOK_FIXTURE : FOUNTAIN_FIXTURE;
const WORKSPACE = path.resolve(__dirname, "..", ".."); // NickAndMe root
const HOST = path.resolve(__dirname, "..");

function runId() {
  return new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
}

function log(...args) {
  const line = `[${new Date().toISOString()}] ${args.join(" ")}`;
  console.log(line);
  fs.appendFileSync(path.join(ARTIFACTS, "pilot.log"), line + "\n");
}

function ensureDir(d) {
  fs.mkdirSync(d, { recursive: true });
}

const API_HEADERS = {
  "Content-Type": "application/json",
  "X-FilmStudio-User": "pilot",
  "X-FilmStudio-Role": "admin",
};

async function apiGet(p) {
  const r = await fetch(`${API_URL}${p}`, { headers: API_HEADERS });
  const t = await r.text();
  try {
    return { ok: r.ok, status: r.status, json: JSON.parse(t), text: t };
  } catch {
    return { ok: r.ok, status: r.status, json: null, text: t };
  }
}

async function apiPost(p, body) {
  const r = await fetch(`${API_URL}${p}`, {
    method: "POST",
    headers: API_HEADERS,
    body: body === undefined ? undefined : JSON.stringify(body ?? {}),
  });
  const t = await r.text();
  try {
    return { ok: r.ok, status: r.status, json: JSON.parse(t), text: t };
  } catch {
    return { ok: r.ok, status: r.status, json: null, text: t };
  }
}

/** Wait until no queued/running jobs for project (API). */
async function waitApiJobsIdle(projectId, timeoutMs = 600_000) {
  const start = Date.now();
  let lastMsg = "";
  let lastMsgAt = Date.now();
  while (Date.now() - start < timeoutMs) {
    const j = await apiGet(`/api/jobs?projectId=${encodeURIComponent(projectId)}`);
    const jobs = j.json?.jobs || [];
    const active = jobs.find((x) => /queued|running/i.test(x.status || ""));
    if (!active) {
      await new Promise((r) => setTimeout(r, 800));
      const j2 = await apiGet(`/api/jobs?projectId=${encodeURIComponent(projectId)}`);
      const active2 = (j2.json?.jobs || []).find((x) => /queued|running/i.test(x.status || ""));
      if (!active2) return;
      continue;
    }
    const msg = `${active.kind}|${active.index}|${active.message || ""}`;
    if (msg !== lastMsg) {
      lastMsg = msg;
      lastMsgAt = Date.now();
      log("api-job", active.status, active.kind, (active.message || "").slice(0, 140));
    }
    const msgLower = (active.message || "").toLowerCase();
    const isRemoteWait =
      msgLower.includes("pending") ||
      msgLower.includes("poll") ||
      msgLower.includes("image") ||
      msgLower.includes("generat") ||
      msgLower.includes("%");
    const hangMs = isRemoteWait
      ? Number(process.env.REMOTE_HANG_MS || 25 * 60_000)
      : Number(process.env.LOCAL_HANG_MS || 5 * 60_000);
    if (Date.now() - lastMsgAt > hangMs) {
      log("WARN api job hung — cancel", active.jobId, (active.message || "").slice(0, 100));
      if (active.jobId)
        await fetch(`${API_URL}/api/jobs/${active.jobId}/cancel`, {
          method: "POST",
          headers: API_HEADERS,
        }).catch(() => {});
      await fetch(`${API_URL}/api/jobs/cancel`, { method: "POST", headers: API_HEADERS }).catch(
        () => {}
      );
      await new Promise((r) => setTimeout(r, 1500));
      return;
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
  throw new Error("API jobs did not become idle in time");
}

async function waitForApi(timeoutMs = 120_000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    try {
      const r = await fetch(`${API_URL}/health`);
      if (r.ok) return;
    } catch {
      /* retry */
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`API not healthy at ${API_URL}`);
}

async function waitForWeb(timeoutMs = 120_000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    try {
      const r = await fetch(WEB_URL);
      if (r.ok || r.status === 200) return;
    } catch {
      /* retry */
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`Web not reachable at ${WEB_URL}`);
}

async function screenshot(page, name) {
  const p = path.join(ARTIFACTS, "screens", `${name}.png`);
  ensureDir(path.dirname(p));
  await page.screenshot({ path: p, fullPage: true });
  log("screenshot", p);
  return p;
}

async function dumpJob(page, step) {
  const panel = page.locator('[data-testid="job-panel"]');
  if (await panel.count()) {
    const status = await panel.getAttribute("data-job-status");
    const kind = await panel.getAttribute("data-job-kind");
    const running = await panel.getAttribute("data-job-running");
    log(`job@${step}`, `status=${status} kind=${kind} running=${running}`);
  }
  // API jobs (mine)
  try {
    const j = await apiGet("/api/jobs?mine=1");
    if (j.json) {
      fs.writeFileSync(
        path.join(ARTIFACTS, `jobs-mine-${step}.json`),
        JSON.stringify(j.json, null, 2)
      );
      const jobs = j.json.jobs || [];
      const top = jobs[0];
      log(
        `api-job@${step}`,
        top
          ? `${top.status} ${top.kind || ""} ${top.message || ""}`.trim()
          : `count=${jobs.length}`
      );
    }
  } catch (e) {
    log("api-job dump failed", String(e));
  }
}

async function waitJobIdle(page, timeoutMs = 600_000, projectId = PROJECT_NAME) {
  const start = Date.now();
  let lastMsg = "";
  let lastMsgAt = Date.now();
  while (Date.now() - start < timeoutMs) {
    // API by projectId works without browser auth cookies
    try {
      const j = await apiGet(`/api/jobs?projectId=${encodeURIComponent(projectId)}`);
      const jobs = j.json?.jobs || [];
      const active = jobs.find((x) => /queued|running/i.test(x.status || ""));
      if (!active) {
        await page.waitForTimeout(800);
        const j2 = await apiGet(`/api/jobs?projectId=${encodeURIComponent(projectId)}`);
        const active2 = (j2.json?.jobs || []).find((x) => /queued|running/i.test(x.status || ""));
        if (!active2) return;
      } else {
        const msg = `${active.kind}|${active.index}|${active.message || ""}`;
        if (msg !== lastMsg) {
          lastMsg = msg;
          lastMsgAt = Date.now();
          log("job", active.status, active.kind, (active.message || "").slice(0, 140));
        }
        // Hang detection: real Grok can sit on "pending" for many minutes — only treat
        // local ffmpeg/post steps as hung. Pending/polling video is allowed longer.
        const msgLower = (active.message || "").toLowerCase();
        const isRemoteWait =
          msgLower.includes("pending") ||
          msgLower.includes("poll") ||
          msgLower.includes("submit") ||
          msgLower.includes("waiting") ||
          msgLower.includes("%") ||
          msgLower.includes("generating s");
        const hangMs = isRemoteWait
          ? Number(process.env.REMOTE_HANG_MS || 15 * 60_000) // 15 min for real video
          : Number(process.env.LOCAL_HANG_MS || 3 * 60_000);  // 3 min for ffmpeg local
        if (Date.now() - lastMsgAt > hangMs) {
          log(
            "WARN job hung — cancelling",
            active.jobId,
            `idle=${Math.round((Date.now() - lastMsgAt) / 1000)}s`,
            (active.message || "").slice(0, 100)
          );
          if (active.jobId) {
            await fetch(`${API_URL}/api/jobs/${active.jobId}/cancel`, { method: "POST" }).catch(() => {});
          }
          await fetch(`${API_URL}/api/jobs/cancel`, { method: "POST" }).catch(() => {});
          await page.waitForTimeout(1500);
          return;
        }
      }
    } catch {
      /* fall through */
    }
    await page.waitForTimeout(1500);
  }
  throw new Error("Job did not become idle in time");
}

async function extractKeyframes(videoPath, outDir, label) {
  ensureDir(outDir);
  if (!videoPath || !fs.existsSync(videoPath)) {
    log("keyframes skip — missing", videoPath);
    return [];
  }
  // Prefer bundled ffmpeg from engine
  const ffmpegCandidates = [
    path.join(HOST, "FilmStudio.Engine", "bin", "Debug", "net10.0", "Resources", "ffmpeg.exe"),
    path.join(HOST, "FilmStudio.Api", "bin", "Debug", "net10.0", "Resources", "ffmpeg.exe"),
    "ffmpeg",
  ];
  let ffmpeg = "ffmpeg";
  for (const c of ffmpegCandidates) {
    if (c === "ffmpeg" || fs.existsSync(c)) {
      ffmpeg = c;
      break;
    }
  }
  const pattern = path.join(outDir, `${label}_%02d.jpg`);
  await new Promise((resolve, reject) => {
    const args = [
      "-y",
      "-i",
      videoPath,
      "-vf",
      "fps=1/2,scale=480:-1",
      "-frames:v",
      "4",
      pattern,
    ];
    const p = spawn(ffmpeg, args, { stdio: ["ignore", "pipe", "pipe"] });
    let err = "";
    p.stderr.on("data", (d) => (err += d.toString()));
    p.on("close", (code) => (code === 0 ? resolve() : reject(new Error(err.slice(-500)))));
  }).catch((e) => log("ffmpeg keyframes warn", String(e).slice(0, 300)));
  const frames = fs.readdirSync(outDir).filter((f) => f.startsWith(label) && f.endsWith(".jpg"));
  log("keyframes", label, frames.join(", ") || "(none)");
  return frames.map((f) => path.join(outDir, f));
}

function findProjectVideos(projectId) {
  const videoDir = path.join(WORKSPACE, "projects", projectId, "assets", "video");
  if (!fs.existsSync(videoDir)) return [];
  return fs
    .readdirSync(videoDir)
    .filter((f) => f.endsWith(".mp4"))
    .map((f) => path.join(videoDir, f));
}

/** Project character asset dir under workspace. */
function characterAssetDir(projectId) {
  return path.join(WORKSPACE, "projects", projectId, "assets", "characters");
}

/**
 * Copy portrait variants into pilot artifacts so a human (or agent) can inspect
 * style before any video spend. Returns paths that exist (1..3).
 */
function snapshotCharacterVariants(projectId, charKey) {
  const srcDir = characterAssetDir(projectId);
  const destDir = path.join(ARTIFACTS, "cast", charKey);
  ensureDir(destDir);
  const stem = String(charKey).toLowerCase();
  const found = [];
  for (let i = 1; i <= 3; i++) {
    const name = `${stem}_variant_0${i}.png`;
    const src = path.join(srcDir, name);
    if (!fs.existsSync(src)) continue;
    const dest = path.join(destDir, name);
    fs.copyFileSync(src, dest);
    found.push({ index: i, path: dest, bytes: fs.statSync(src).size });
  }
  // also copy locked ref if present
  const refName = `${stem}_ref.png`;
  const refSrc = path.join(srcDir, refName);
  if (fs.existsSync(refSrc)) {
    fs.copyFileSync(refSrc, path.join(destDir, refName));
  }
  return found;
}

/**
 * Try lock each variant (1..3). Server portrait style gate rejects sketch vs photoreal.
 * First successful lock wins. If all fail for style/other reasons → hard stop (no movie).
 */
async function pickBestVariantAndLock(charKey) {
  const variants = snapshotCharacterVariants(PROJECT_NAME, charKey);
  log(
    "cast QA variants for",
    charKey,
    variants.length
      ? variants.map((v) => `v${v.index}=${v.bytes}B`).join(", ")
      : "(none on disk)"
  );
  if (variants.length === 0) {
    throw new Error(
      `STOP movie — ${charKey}: no portrait variants on disk after generate. Fix cast gen before shots/video.`
    );
  }

  const failures = [];
  for (const v of variants) {
    log("cast QA try lock", charKey, `variant ${v.index}`);
    const lock = await apiPost(
      `/api/projects/${encodeURIComponent(PROJECT_NAME)}/characters/${encodeURIComponent(charKey)}/lock-variant`,
      { index: v.index }
    );
    if (lock.ok) {
      log("cast QA LOCKED", charKey, `variant ${v.index}`, "(style gate passed)");
      // re-snapshot ref for review folder
      snapshotCharacterVariants(PROJECT_NAME, charKey);
      return v.index;
    }
    const detail = (lock.text || "").slice(0, 400);
    log("cast QA reject", charKey, `variant ${v.index}`, String(lock.status), detail);
    failures.push(`v${v.index}: ${detail}`);
    // Style mismatch → try next variant; do not proceed to video
    if (/style|sketch|illustration|photoreal|medium/i.test(detail)) {
      log("cast QA style mismatch — will try next variant, will NOT start video gen");
      continue;
    }
  }

  throw new Error(
    `STOP movie — ${charKey}: no portrait variant passed style/lock QA. ` +
      `Inspect artifacts/cast/${charKey}/ and re-gen with project render_style_lock. ` +
      `Failures: ${failures.join(" | ").slice(0, 800)}`
  );
}

async function step(name, fn) {
  log("=== STEP", name, "===");
  const t0 = Date.now();
  try {
    await fn();
    log("=== OK", name, `${Date.now() - t0}ms ===`);
  } catch (e) {
    log("=== FAIL", name, String(e));
    // Cast/style failures must never be mistaken for a partial movie success
    if (String(e).includes("STOP movie")) {
      log(
        "ABORT: movie generation halted at cast QA — fix character style/locks before shots or video spend."
      );
    }
    throw e;
  }
}

async function main() {
  ensureDir(ARTIFACTS);
  ensureDir(path.join(ARTIFACTS, "screens"));
  ensureDir(path.join(ARTIFACTS, "frames"));
  fs.writeFileSync(
    path.join(ARTIFACTS, "run-meta.json"),
    JSON.stringify(
      {
        PROJECT_NAME,
        WEB_URL,
        API_URL,
        HEADED,
        REAL_SCENE1,
        FAIL_RATE,
        FULL_MOVIE: process.env.FULL_MOVIE || "0",
        IMPORT_MODE: process.env.IMPORT_MODE || "fountain",
        startedAt: new Date().toISOString(),
      },
      null,
      2
    )
  );

  log("waiting for API + Web…");
  await waitForApi();
  await waitForWeb();
  const health = await apiGet("/health");
  log("health", JSON.stringify(health.json || health.text).slice(0, 300));

  const browser = await chromium.launch({ headless: !HEADED });
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await context.newPage();
  page.on("console", (msg) => {
    if (msg.type() === "error") log("browser-console", msg.text());
  });

  let projectId = PROJECT_NAME;

  try {
    await step("01_home_create_project", async () => {
      await page.goto(`${WEB_URL}/?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(1500); // admin bootstrap
      await screenshot(page, "01-home");

      // If project already exists, select it; else create (UI navigates to /adaptation)
      const existing = page.locator(`[data-testid="home-project-${PROJECT_NAME}"]`);
      if (await existing.count()) {
        await existing.click();
        await page.waitForTimeout(800);
        const open = page.getByTestId("home-open-adaptation");
        if (await open.count()) await open.click();
        log("selected existing project", PROJECT_NAME);
      } else {
        await page.getByTestId("home-new-project").click();
        await page.getByTestId("home-new-project-name").fill(PROJECT_NAME);
        await page.getByTestId("home-create-project").click();
        // Create navigates to adaptation/import
        await page.waitForURL(/adaptation/i, { timeout: 60_000 });
        log("created project", PROJECT_NAME, "→", page.url());
      }
      await screenshot(page, "01-home-created");
      // Confirm via API
      const projs = await apiGet("/api/projects");
      const ids = (projs.json?.projects || []).map((p) => p.id);
      if (!ids.some((id) => String(id).toLowerCase() === PROJECT_NAME.toLowerCase())) {
        throw new Error(`Project ${PROJECT_NAME} not in API list: ${ids.join(",")}`);
      }
    });

    await step("02_import_source", async () => {
      await page.goto(`${WEB_URL}/adaptation/import?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(1000);
      if (!fs.existsSync(IMPORT_FIXTURE)) throw new Error(`Missing fixture ${IMPORT_FIXTURE}`);
      log("importing", IMPORT_FIXTURE);
      await page.getByTestId("import-file-input").setInputFiles(IMPORT_FIXTURE);
      // Fountain import navigates to screenplay; book path runs a job
      await page.waitForTimeout(2500);
      if (page.url().includes("screenplay")) {
        log("import navigated to screenplay");
      } else {
        await waitJobIdle(page, 300_000);
        const cont = page.getByTestId("import-continue-screenplay");
        try {
          await cont.waitFor({ timeout: 60_000 });
          await cont.click();
        } catch {
          await page.goto(`${WEB_URL}/adaptation/screenplay?admin=1`);
        }
      }
      await dumpJob(page, "import");
      await screenshot(page, "02-import-done");
    });

    await step("03_screenplay_signoff", async () => {
      await page.goto(`${WEB_URL}/adaptation/screenplay?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(1500);
      // Only use "from book" when we imported a book and have no draft yet
      const fromBook = page.getByTestId("screenplay-from-book");
      if (await fromBook.isVisible().catch(() => false)) {
        await fromBook.click();
        await waitJobIdle(page, 600_000);
      }
      const status = page.getByTestId("screenplay-status");
      await status.waitFor({ timeout: 60_000 });
      for (let i = 0; i < 60; i++) {
        const draft = await status.getAttribute("data-draft");
        if (draft === "true") break;
        await page.waitForTimeout(1000);
      }
      await dumpJob(page, "screenplay");
      await screenshot(page, "03-screenplay-draft");

      const draftPath = path.join(WORKSPACE, "projects", PROJECT_NAME, "source", "screenplay.fountain");
      if (fs.existsSync(draftPath)) {
        const text = fs.readFileSync(draftPath, "utf8");
        fs.writeFileSync(path.join(ARTIFACTS, "screenplay.fountain"), text);
        log("screenplay chars", String(text.length), "cinematic?", /CHAMBER|OLD MAN|OFFICER|vulture/i.test(text));
      }

      // Wait for any background job (e.g. leftover scene gen) before sign-off enables
      await waitJobIdle(page, 900_000);
      // Cancel stuck job if Cancel is still visible
      const cancel = page.locator('button:has-text("Cancel job"), button:has-text("Cancel")').first();
      if (await cancel.isVisible().catch(() => false) && !(await cancel.isDisabled().catch(() => true))) {
        await cancel.click().catch(() => {});
        await page.waitForTimeout(1000);
        await waitJobIdle(page, 60_000).catch(() => {});
      }
      const sign = page.getByTestId("screenplay-signoff");
      await sign.waitFor({ state: "visible", timeout: 30_000 });
      // Poll until enabled
      for (let i = 0; i < 120; i++) {
        if (await sign.isEnabled()) break;
        await page.waitForTimeout(1000);
      }
      if (!(await sign.isEnabled())) {
        throw new Error("screenplay-signoff still disabled after waiting for jobs");
      }
      await sign.click();
      await waitJobIdle(page, 300_000);
      await page.waitForTimeout(1500);
      await screenshot(page, "03-screenplay-signed");
    });

    await step("04_characters_extract_and_lock", async () => {
      // Ensure book text is present for book-aware looks (fountain import alone may not copy it)
      const sourceDir = path.join(WORKSPACE, "projects", PROJECT_NAME, "source");
      ensureDir(sourceDir);
      const bookDest = path.join(sourceDir, "book_full.txt");
      if (!fs.existsSync(bookDest) && fs.existsSync(BOOK_FIXTURE)) {
        fs.copyFileSync(BOOK_FIXTURE, bookDest);
        log("copied book_full.txt for cast looks");
      }

      // Extract is a long sync API call (not a background job). Drive it via API so we
      // never race the UI and list a partial cast before fountain_to_cast finishes.
      log("API extract-cast (force)…");
      const extractRes = await apiPost(
        `/api/projects/${encodeURIComponent(PROJECT_NAME)}/characters/extract-cast`,
        { force: true, model: "grok-4.5" }
      );
      if (!extractRes.ok) {
        log("WARN extract-cast API", String(extractRes.status), (extractRes.text || "").slice(0, 300));
        // Fall back to UI Build cast / Rebuild cast
        await page.goto(`${WEB_URL}/characters?admin=1`, { waitUntil: "networkidle" });
        await page.waitForTimeout(1000);
        const extractBtn = page.getByTestId("characters-extract-cast");
        if (await extractBtn.count()) {
          await extractBtn.first().click({ force: true });
          // Poll until cast seeds grow or timeout (UI path is also sync-ish)
          for (let i = 0; i < 180; i++) {
            await page.waitForTimeout(2000);
            const mid = await apiGet(`/api/projects/${encodeURIComponent(PROJECT_NAME)}/characters`);
            const n = (mid.json?.characters || mid.json?.Characters || []).length;
            if (n >= 3) {
              log("UI extract appears done, cast count", String(n));
              break;
            }
          }
        }
      } else {
        log(
          "extract-cast ok",
          `count=${extractRes.json?.characterCount}`,
          (extractRes.json?.characters || []).join?.(", ") || ""
        );
      }

      await page.goto(`${WEB_URL}/characters?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(1000);

      // Skip plate matching when no illustrated book pages (text-only Poe)
      const find = page.getByTestId("characters-find-plates");
      if (await find.isVisible().catch(() => false)) {
        log("skip find-plates (no book art for Poe text/fountain import)");
      }

      // --- Generate + lock portraits for every on-screen character (API) ---
      // Loop until cast gate is ready (handles late seeds / missing officers).
      for (let pass = 1; pass <= 4; pass++) {
        const charsRes = await apiGet(`/api/projects/${encodeURIComponent(PROJECT_NAME)}/characters`);
        const rows = charsRes.json?.characters || charsRes.json?.Characters || [];
        log("cast list pass", String(pass), String(rows.length), rows.map((c) => c.key || c.Key).join(", "));
        if (rows.length === 0) throw new Error("No characters after cast extract");

        for (const c of rows) {
          const key = c.key || c.Key;
          const voiceOnly = !!(c.voiceOnly ?? c.VoiceOnly);
          const locked = !!(c.locked ?? c.Locked);
          const voice = (c.voiceProfile || c.VoiceProfile || "").trim();
          log(
            "character",
            key,
            voiceOnly ? "voice-only" : "on-screen",
            locked ? "locked" : "unlocked",
            `voiceLen=${voice.length}`
          );

          if (voiceOnly) {
            if (!voice) {
              await apiPost(
                `/api/projects/${encodeURIComponent(PROJECT_NAME)}/characters/${encodeURIComponent(key)}/voice`,
                {
                  voiceProfile: "Consistent character voice for this production.",
                  voiceLabel: key.replace(/^Character_/, ""),
                }
              );
              log("set default voice for", key);
            }
            continue;
          }

          // Even if already locked, re-snapshot for QA. Force re-gen when RELOCK_ALL=1
          // or when lock looks like a style miss from a prior bad run.
          const forceRelock =
            process.env.RELOCK_ALL === "1" || process.env.RELOCK_ALL === "true";
          if (locked && !forceRelock) {
            log("already locked", key, "— snapshot for QA (set RELOCK_ALL=1 to re-verify style)");
            snapshotCharacterVariants(PROJECT_NAME, key);
            continue;
          }

          if (locked && forceRelock) {
            log("RELOCK_ALL: unlock", key);
            await apiPost(
              `/api/projects/${encodeURIComponent(PROJECT_NAME)}/characters/${encodeURIComponent(key)}/unlock`,
              {}
            );
          }

          log("generate portraits for", key);
          const gen = await apiPost("/api/jobs/character-variants", {
            projectId: PROJECT_NAME,
            charKey: key,
            count: 3,
            seedMode: "none",
            includePreferred: false,
            includeLockedRef: false,
            maxRefs: 0,
            persistDescription: true,
          });
          if (!gen.ok) {
            log("WARN character-variants failed", key, String(gen.status), (gen.text || "").slice(0, 200));
            const gen2 = await apiPost("/api/jobs/character-variants", {
              projectId: PROJECT_NAME,
              charKey: key,
              count: 3,
              seedMode: "auto",
              persistDescription: true,
            });
            if (!gen2.ok) throw new Error(`character-variants ${key}: ${gen2.status} ${gen2.text}`);
          }
          await waitApiJobsIdle(PROJECT_NAME, Number(process.env.CHAR_GEN_TIMEOUT_MS || 20 * 60_000));

          // Inspect variants + lock first that passes server style gate.
          // On total failure: STOP movie — never build shots or spend on video.
          await pickBestVariantAndLock(key);
          await page.waitForTimeout(300);
        }

        const adapt = await apiGet(`/api/projects/${encodeURIComponent(PROJECT_NAME)}/adaptation`);
        const cast = adapt.json?.adaptation?.cast || adapt.json?.Adaptation?.Cast || {};
        log("cast readiness", JSON.stringify(cast).slice(0, 280));
        const ready =
          cast.readyForShots === true ||
          cast.ReadyForShots === true ||
          (Number(cast.ready ?? cast.Ready ?? 0) > 0 &&
            Number(cast.ready ?? cast.Ready ?? 0) === Number(cast.total ?? cast.Total ?? -1));
        if (ready) {
          log("cast gate OPEN", `ready=${cast.ready ?? cast.Ready}/${cast.total ?? cast.Total}`);
          break;
        }
        const missing = cast.missing || cast.Missing || [];
        if (pass === 4) {
          throw new Error(
            `Cast not ready after ${pass} passes: ${JSON.stringify(missing || cast).slice(0, 400)}`
          );
        }
        log("cast gate still closed; retry missing", JSON.stringify(missing).slice(0, 200));
      }

      await dumpJob(page, "characters");
      await page.goto(`${WEB_URL}/characters?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(1000);
      await screenshot(page, "04-characters-locked");

      const toShots = page.getByTestId("characters-to-shots");
      if (await toShots.isVisible().catch(() => false)) {
        await toShots.click();
      } else {
        await page.goto(`${WEB_URL}/adaptation/shots?admin=1`);
      }
    });

    await step("05_build_shot_plan", async () => {
      await page.goto(`${WEB_URL}/adaptation/shots?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(1000);
      await page.getByTestId("shots-build").click();
      await waitJobIdle(page, 600_000);
      await dumpJob(page, "shots");
      // Wait ready
      const st = page.getByTestId("shots-status");
      for (let i = 0; i < 90; i++) {
        if ((await st.getAttribute("data-ready")) === "true") break;
        await page.waitForTimeout(1000);
      }
      const scenes = await st.getAttribute("data-scenes");
      const clips = await st.getAttribute("data-clips");
      log("shot plan", `scenes=${scenes} clips=${clips}`);
      await screenshot(page, "05-shots");
      const toScenes = page.getByTestId("shots-to-scenes");
      if (await toScenes.isVisible().catch(() => false)) await toScenes.click();
      else await page.goto(`${WEB_URL}/scenes?admin=1`);
    });

    await step("06_gen_scenes_480p", async () => {
      const fullMovie =
        process.env.FULL_MOVIE === "1" || process.env.FULL_MOVIE === "true";
      const maxScene = fullMovie
        ? Number(process.env.MAX_SCENE || 99)
        : Number(process.env.MAX_SCENE || 1);
      await page.goto(`${WEB_URL}/scenes?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(2000);
      await waitJobIdle(page, 120_000).catch(() => {});
      const res = page.getByTestId("scenes-resolution");
      if (await res.count()) await res.selectOption("480p");

      // Discover scene gen buttons
      const genBtns = page.locator('[data-testid^="scenes-gen-"]');
      const btnCount = await genBtns.count();
      const sceneNums = [];
      for (let i = 0; i < btnCount; i++) {
        const sn = Number(await genBtns.nth(i).getAttribute("data-scene"));
        if (sn >= 1 && sn <= maxScene) sceneNums.push(sn);
      }
      if (sceneNums.length === 0) sceneNums.push(1);
      log("will generate scenes", sceneNums.join(", "), fullMovie ? "(FULL_MOVIE)" : "(pilot)");

      for (const sn of sceneNums) {
        await page.goto(`${WEB_URL}/scenes?admin=1`, { waitUntil: "networkidle" });
        await page.waitForTimeout(1500);
        await waitJobIdle(page, 60_000).catch(() => {});
        if (await res.count()) await res.selectOption("480p");
        const gen = page.getByTestId(`scenes-gen-${sn}`);
        if (!(await gen.count())) {
          log("no gen button for scene", String(sn));
          continue;
        }
        // Skip if already complete on disk
        const existing = findProjectVideos(PROJECT_NAME).filter((v) =>
          path.basename(v).match(new RegExp(`scene_0*${sn}_clip_`, "i"))
        );
        log(`scene ${sn}: ${existing.length} clip(s) already on disk`);
        // Cast gate may leave the button disabled — fail fast with a clear message.
        if (await gen.isDisabled().catch(() => false)) {
          const title = (await gen.getAttribute("title")) || "";
          throw new Error(
            `scenes-gen-${sn} disabled (cast not ready for video). ${title}`.trim()
          );
        }
        await gen.click();
        log(`waiting for scene ${sn} gen…`);
        await waitJobIdle(page, Number(process.env.GEN_TIMEOUT_MS || 45 * 60_000));
        await page.waitForTimeout(1500);
        const reload = page.getByTestId("scenes-reload");
        if (await reload.count()) await reload.click();
        await page.waitForTimeout(800);
        await dumpJob(page, `gen-s${sn}`);
        await screenshot(page, `06-gen-s${sn}`);

        const vids = findProjectVideos(PROJECT_NAME).filter(
          (v) =>
            !path.basename(v).startsWith("_") &&
            path.basename(v).match(new RegExp(`scene_0*${sn}_clip_`, "i"))
        );
        log(`scene ${sn} videos`, String(vids.length));
        for (const v of vids) {
          await extractKeyframes(v, path.join(ARTIFACTS, "frames"), path.basename(v, ".mp4"));
          // Human review note: dump prompt if present
          const base = path.basename(v, ".mp4");
          const m = base.match(/scene_(\d+)_clip_(\d+)/i);
          if (m) {
            const promptPath = path.join(
              WORKSPACE,
              "projects",
              PROJECT_NAME,
              "assets",
              "video",
              "prompts",
              `S${m[1]}C${m[2]}.txt`
            );
            // also try S01C01 style
            const p2 = path.join(
              WORKSPACE,
              "projects",
              PROJECT_NAME,
              "assets",
              "video",
              "prompts",
              `S${m[1].padStart(2, "0")}C${m[2].padStart(2, "0")}.txt`
            );
            for (const pp of [promptPath, p2]) {
              if (fs.existsSync(pp)) {
                fs.copyFileSync(pp, path.join(ARTIFACTS, path.basename(pp)));
                const text = fs.readFileSync(pp, "utf8");
                log("prompt", path.basename(pp), `len=${text.length}`, text.slice(0, 160).replace(/\s+/g, " "));
              }
            }
          }
        }
        fs.writeFileSync(
          path.join(ARTIFACTS, `videos-after-s${sn}.json`),
          JSON.stringify(findProjectVideos(PROJECT_NAME), null, 2)
        );
      }
    });

    await step("07_review_auto_and_human", async () => {
      const fullMovie =
        process.env.FULL_MOVIE === "1" || process.env.FULL_MOVIE === "true";
      await page.goto(`${WEB_URL}/review?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(2000);
      await waitJobIdle(page, 120_000).catch(() => {});
      await screenshot(page, "07-review-start");

      // Discover scene rows S01, S02, …
      const sceneLabels = fullMovie
        ? ["S01", "S02", "S03", "S04", "S05", "S06", "S07", "S08"]
        : ["S01"];
      const failEvery = Math.max(1, Math.round(1 / Math.max(0.01, FAIL_RATE)));
      let reviewIndex = 0;

      for (const label of sceneLabels) {
        const sceneNum = Number(label.slice(1));
        const hasClips = findProjectVideos(PROJECT_NAME).some((v) =>
          path.basename(v).match(new RegExp(`scene_0*${sceneNum}_clip_`, "i"))
        );
        if (!hasClips) {
          log("review skip scene — no clips", label);
          continue;
        }
        await page.goto(`${WEB_URL}/review?admin=1`, { waitUntil: "networkidle" });
        await page.waitForTimeout(1200);
        const clipsBtn = page.locator("tr", { hasText: label }).getByRole("button", { name: "Clips" });
        if (await clipsBtn.count()) {
          await clipsBtn.click();
          await page.waitForTimeout(1000);
        } else {
          log("no Clips button for", label);
          continue;
        }
        await screenshot(page, `07-review-${label}-open`);

        const autoBtns = page.locator(`[data-testid^="review-auto-${sceneNum}-"]`);
        const n = await autoBtns.count();
        log(`${label} auto-review buttons`, String(n));

        for (let i = 0; i < n; i++) {
          const btn = autoBtns.nth(i);
          const testId = await btn.getAttribute("data-testid");
          const clip = await btn.getAttribute("data-clip");
          const scene = await btn.getAttribute("data-scene");
          const onDisk = findProjectVideos(PROJECT_NAME).some((v) =>
            path
              .basename(v)
              .match(new RegExp(`scene_0*${scene}_clip_0*${clip}\\.mp4$`, "i"))
          );
          log("auto-review", testId, onDisk ? "on-disk" : "missing");
          if (!onDisk) continue;
          // Re-query in case DOM refreshed
          const live = page.getByTestId(testId);
          if (!(await live.count()) || (await live.isDisabled().catch(() => true))) {
            log("skip disabled", testId);
            continue;
          }
          await live.click();
          await waitJobIdle(page, 300_000);
          await dumpJob(page, `auto-s${scene}c${clip}`);
          await screenshot(page, `07-auto-s${scene}c${clip}`);

          // Human: fail ~FAIL_RATE for learning; otherwise pass if looks ok.
          // Assembly gate: after auto-fail, Pass needs a real override note (API enforces).
          const failThis = reviewIndex % failEvery === 0;
          reviewIndex++;
          if (failThis) {
            const fail = page.getByTestId(`review-fail-${scene}-${clip}`);
            if (await fail.isVisible().catch(() => false) && !(await fail.isDisabled().catch(() => true))) {
              await fail.click();
              log("human FAIL", `S${scene}C${clip}`);
            }
          } else {
            // Prefer API pass with override-capable note so assembly gate can accept after auto-fail
            const note = `Pilot accept S${scene}C${clip} after auto-review for learning run`;
            const rev = await apiPost(
              `/api/projects/${encodeURIComponent(PROJECT_NAME)}/clips/review`,
              { scene: Number(scene), clip: Number(clip), status: "pass", note }
            );
            if (rev.ok) {
              log("human PASS (API)", `S${scene}C${clip}`, "with override note");
            } else {
              log(
                "human PASS blocked by assembly rules",
                `S${scene}C${clip}`,
                String(rev.status),
                (rev.text || "").slice(0, 200)
              );
              // Leave as auto-fail / unpassed — WIP rebuild must exclude it
            }
          }
          await page.waitForTimeout(500);
        }
      }

      const rebuild = page.getByTestId("review-rebuild-wip");
      if (await rebuild.isVisible().catch(() => false) && !(await rebuild.isDisabled())) {
        await rebuild.click();
        // May fail if all clips blocked — that is correct assembly-gate behavior
        try {
          await waitJobIdle(page, 600_000);
        } catch (e) {
          log("WIP rebuild after review:", String(e).slice(0, 300));
        }
      }
      await screenshot(page, "07-review-done");

      const wip = path.join(WORKSPACE, "projects", PROJECT_NAME, "assets", "movie_wip.mp4");
      if (fs.existsSync(wip)) {
        await extractKeyframes(wip, path.join(ARTIFACTS, "frames"), "wip");
        try {
          log("WIP movie", wip, String(fs.statSync(wip).size));
        } catch {
          log("WIP movie", wip);
        }
      }
    });

    await step("08_admin_learning", async () => {
      await page.goto(`${WEB_URL}/admin/learning?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(1500);
      await page.getByTestId("learning-rules-project").fill(PROJECT_NAME);
      await page.getByTestId("learning-rules-load").click();
      await page.waitForTimeout(800);
      await page.getByTestId("learning-rules-suggest").click();
      await page.waitForTimeout(1500);
      await page.getByTestId("learning-propose").click();
      await page.waitForTimeout(3000);
      await screenshot(page, "08-learning");

      // Auto-approve all pending safe rules (API also marks matching checklist items accepted)
      const approveBtns = page.locator('[data-testid^="learning-approve-"]');
      const ac = await approveBtns.count();
      log("pending rules to approve", String(ac));
      const approvedTexts = [];
      for (let i = 0; i < ac; i++) {
        const b = page.locator('[data-testid^="learning-approve-"]').first();
        if (!(await b.count())) break;
        // Capture suggestion text from the row before it disappears
        const row = b.locator("xpath=ancestor::*[contains(@class,'list-group-item') or contains(@class,'card') or self::tr][1]");
        const rowText = ((await row.count()) ? await row.innerText() : "").replace(/\s+/g, " ").trim();
        if (rowText.length > 20) approvedTexts.push(rowText.slice(0, 400));
        await b.click();
        await page.waitForTimeout(600);
      }

      // Explicit checklist sync from active project rules (belt + suspenders with approve hook)
      const rulesRes = await apiGet(
        `/api/admin/learning/project-rules/${encodeURIComponent(PROJECT_NAME)}`
      );
      const active = rulesRes.json?.rules?.active || rulesRes.json?.rules?.Active || [];
      const texts = active.map((r) => r.text || r.Text).filter(Boolean);
      if (texts.length > 0) {
        const sync = await apiPost("/api/admin/learning/proposal-checklist/accept-matching", {
          texts,
          disposition: "accepted",
          note: `Pilot approved project rules for ${PROJECT_NAME}`,
        });
        log(
          "checklist accept-matching",
          sync.ok ? "ok" : "fail",
          `activeRules=${texts.length}`,
          String(sync.status || "")
        );
      } else if (approvedTexts.length > 0) {
        await apiPost("/api/admin/learning/proposal-checklist/accept-matching", {
          texts: approvedTexts,
          disposition: "accepted",
          note: `Pilot approve click texts for ${PROJECT_NAME}`,
        });
      }

      await page.getByTestId("learning-checklist-reload").click().catch(() => {});
      await page.waitForTimeout(800);
      await screenshot(page, "08-learning-approved");
    });

    if (REAL_SCENE1) {
      await step("09_real_scene1_note", async () => {
        log(
          "REAL_SCENE1=1 requested: restart API without fakes, then re-run with PROJECT_NAME=" +
            PROJECT_NAME +
            " and only gen step. This pilot run used fakes for gen."
        );
        fs.writeFileSync(
          path.join(ARTIFACTS, "REAL_SCENE1_NEXT.md"),
          [
            "# Real Scene 1",
            "",
            "1. Stop API fakes process.",
            "2. Start API with XAI_API_KEY, FilmStudio__UseFakes=false",
            "3. Run: `REAL_SCENE1_ONLY=1 PROJECT_NAME=" + PROJECT_NAME + " npm run pilot`",
            "",
          ].join("\n")
        );
      });
    }

    await step("10_summary", async () => {
      const summary = {
        projectId: PROJECT_NAME,
        artifacts: ARTIFACTS,
        videos: findProjectVideos(PROJECT_NAME),
        wip: fs.existsSync(path.join(WORKSPACE, "projects", PROJECT_NAME, "assets", "movie_wip.mp4")),
        finishedAt: new Date().toISOString(),
      };
      fs.writeFileSync(path.join(ARTIFACTS, "summary.json"), JSON.stringify(summary, null, 2));
      log("SUMMARY", JSON.stringify(summary, null, 2));
      await screenshot(page, "10-final");
    });

    log("PILOT COMPLETE — review artifacts at", ARTIFACTS);
  } finally {
    await browser.close();
  }
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
