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

async function apiGet(p) {
  const r = await fetch(`${API_URL}${p}`);
  const t = await r.text();
  try {
    return { ok: r.ok, status: r.status, json: JSON.parse(t), text: t };
  } catch {
    return { ok: r.ok, status: r.status, json: null, text: t };
  }
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
        // Hang: same message > 90s (silence-trim / ffmpeg sample stalls are common in fakes)
        if (Date.now() - lastMsgAt > 90_000) {
          log("WARN job hung (no progress 90s) — cancelling", active.jobId, (active.message || "").slice(0, 80));
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

async function step(name, fn) {
  log("=== STEP", name, "===");
  const t0 = Date.now();
  try {
    await fn();
    log("=== OK", name, `${Date.now() - t0}ms ===`);
  } catch (e) {
    log("=== FAIL", name, String(e));
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

    await step("04_characters_extract", async () => {
      await page.goto(`${WEB_URL}/characters?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(1500);
      const extract = page.getByTestId("characters-extract-cast");
      if (await extract.isVisible().catch(() => false)) {
        await extract.click();
        await waitJobIdle(page, 600_000);
      }
      // Skip plate matching when no illustrated book pages (text-only Poe)
      const find = page.getByTestId("characters-find-plates");
      if (await find.isVisible().catch(() => false)) {
        log("skip find-plates (no book art for Poe text/fountain import)");
      }
      await dumpJob(page, "characters");
      await screenshot(page, "04-characters");

      // Go to shots if cast complete enough
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

    await step("06_gen_scene1_480p", async () => {
      await page.goto(`${WEB_URL}/scenes?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(2000);
      await waitJobIdle(page, 120_000).catch(() => {}); // clear any leftover job
      const res = page.getByTestId("scenes-resolution");
      if (await res.count()) await res.selectOption("480p");

      const gen1 = page.getByTestId("scenes-gen-1");
      if (await gen1.count()) {
        await gen1.click();
      } else {
        // Select first incomplete row checkbox
        const rowCb = page.locator("table tbody tr").first().locator('input[type="checkbox"]');
        if (await rowCb.count()) await rowCb.check().catch(() => {});
        await page.getByTestId("scenes-generate-batch").click();
      }
      log("waiting for scene 1 gen to finish…");
      await waitJobIdle(page, 900_000);
      // Extra settle + reload list
      await page.waitForTimeout(2000);
      const reload = page.getByTestId("scenes-reload");
      if (await reload.count()) await reload.click();
      await page.waitForTimeout(1000);
      await dumpJob(page, "gen-s1");
      await screenshot(page, "06-gen-s1");

      const vids = findProjectVideos(PROJECT_NAME).filter((v) => !path.basename(v).startsWith("_"));
      fs.writeFileSync(path.join(ARTIFACTS, "videos-after-gen.json"), JSON.stringify(vids, null, 2));
      log("videos on disk", String(vids.length));
      for (const v of vids.slice(0, 8)) {
        await extractKeyframes(v, path.join(ARTIFACTS, "frames"), path.basename(v, ".mp4"));
      }
    });

    await step("07_review_auto_and_human", async () => {
      await page.goto(`${WEB_URL}/review?admin=1`, { waitUntil: "networkidle" });
      await page.waitForTimeout(2000);
      await waitJobIdle(page, 120_000).catch(() => {});
      await screenshot(page, "07-review-start");

      // Open S01 clip detail (Clips button)
      const clipsBtn = page.locator("tr", { hasText: "S01" }).getByRole("button", { name: "Clips" });
      if (await clipsBtn.count()) {
        await clipsBtn.click();
        await page.waitForTimeout(1000);
      } else {
        // Fallback: click first Clips
        const any = page.getByRole("button", { name: "Clips" }).first();
        if (await any.count()) await any.click();
        await page.waitForTimeout(1000);
      }
      await screenshot(page, "07-review-s1-open");

      const autoBtns = page.locator('[data-testid^="review-auto-1-"]');
      const n = await autoBtns.count();
      log("scene1 auto-review buttons", String(n));
      const failEvery = Math.max(1, Math.round(1 / Math.max(0.01, FAIL_RATE)));

      // Only review clips that exist on disk (gen may hang mid-scene)
      for (let i = 0; i < n; i++) {
        const btn = autoBtns.nth(i);
        const testId = await btn.getAttribute("data-testid");
        const clip = await btn.getAttribute("data-clip");
        const onDisk = findProjectVideos(PROJECT_NAME).some((v) =>
          path.basename(v).match(new RegExp(`scene_0*1_clip_0*${clip}\\.mp4$`, "i"))
        );
        log("auto-review", testId, onDisk ? "on-disk" : "missing");
        if (!onDisk || (await btn.isDisabled())) {
          log("skip", testId);
          // Still human-mark missing as fail for learning signal
          const failMissing = page.getByTestId(`review-fail-1-${clip}`);
          if (await failMissing.isVisible().catch(() => false) && !(await failMissing.isDisabled().catch(() => true))) {
            await failMissing.click().catch(() => {});
          }
          continue;
        }
        await btn.click();
        await waitJobIdle(page, 180_000);
        await dumpJob(page, `auto-${clip}`);
        await screenshot(page, `07-auto-s1c${clip}`);

        const applyOpen = page.getByTestId(`review-apply-open-1-${clip}`);
        if (await applyOpen.isVisible().catch(() => false) && i % failEvery === 0) {
          await applyOpen.click();
          await page.waitForTimeout(500);
          const regen = page.getByTestId(`review-apply-regen-1-${clip}`);
          if (await regen.isVisible().catch(() => false) && !(await regen.isDisabled())) {
            // Skip apply+regen in fakes if silence-trim hangs; pass note instead
            log("apply panel open — human edits without regen (fakes hang risk)");
            const cancel = page.locator('button:has-text("Cancel")').first();
            if (await cancel.isVisible().catch(() => false)) await cancel.click().catch(() => {});
          }
        }

        if (i % failEvery === 0) {
          const fail = page.getByTestId(`review-fail-1-${clip}`);
          if (await fail.isVisible().catch(() => false)) {
            await fail.click();
            log("human FAIL", `S01C${clip}`);
          }
        } else {
          const pass = page.getByTestId(`review-pass-1-${clip}`);
          if (await pass.isVisible().catch(() => false)) {
            await pass.click();
            log("human PASS", `S01C${clip}`);
          }
        }
        await page.waitForTimeout(400);
      }

      const rebuild = page.getByTestId("review-rebuild-wip");
      if (await rebuild.isVisible().catch(() => false) && !(await rebuild.isDisabled())) {
        await rebuild.click();
        await waitJobIdle(page, 300_000);
      }
      await screenshot(page, "07-review-done");

      const vids = findProjectVideos(PROJECT_NAME).filter((v) => !path.basename(v).startsWith("_"));
      for (const v of vids.slice(0, 6)) {
        await extractKeyframes(v, path.join(ARTIFACTS, "frames"), "post_" + path.basename(v, ".mp4"));
      }
      const wip = path.join(WORKSPACE, "projects", PROJECT_NAME, "assets", "movie_wip.mp4");
      if (fs.existsSync(wip)) {
        await extractKeyframes(wip, path.join(ARTIFACTS, "frames"), "wip");
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

      // Auto-approve all pending safe rules
      const approveBtns = page.locator('[data-testid^="learning-approve-"]');
      const ac = await approveBtns.count();
      log("pending rules to approve", String(ac));
      for (let i = 0; i < ac; i++) {
        // always re-query first button (list shrinks)
        const b = page.locator('[data-testid^="learning-approve-"]').first();
        if (!(await b.count())) break;
        await b.click();
        await page.waitForTimeout(600);
      }
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
