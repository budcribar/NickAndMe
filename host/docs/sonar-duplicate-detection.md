# Automated duplicate detection (SonarCloud)

PageToMovie already went through several manual `fix/dedupe-clones` and shared-helper extraction passes. Most residual near-duplicates from the earlier manual passes have been cleaned (AdaptationDensity `NormalizeBookText` and AdaptationService private `StripFences` now call the shared BookToFountainConverter helpers). CPD remains continuous so new clones are caught on every PR and on `master`. This setup makes **Copy-Paste Detection (CPD)** continuous so new clones are caught on every PR and on `master`.

## What you get

| Surface | What it shows |
|---------|----------------|
| SonarCloud **Measures → Duplications** | % duplicated lines, duplicated blocks, density |
| Issues (rule family CPD / duplicated blocks) | Exact locations of clone pairs |
| PR decoration | New/removed duplication on the changed files (when the GitHub app is installed) |
| Quality Gate | Default SonarCloud gate already fails on high duplication; tighten later if desired |

Thresholds used in CI and the local script are intentionally a bit more sensitive than Sonar defaults:

- `sonar.cpd.cs.minimumTokens=50` (default 100)
- `sonar.cpd.cs.minimumLines=4`

so small helper clones surface early. Raise them again if the noise is annoying.

## One-time SonarCloud setup

1. Sign in at [sonarcloud.io](https://sonarcloud.io) with the GitHub account that owns `budcribar/PageToMovie`.
2. **Analyze new project** → pick the GitHub repo → follow the wizard.
3. Prefer the organization key that matches the GitHub org/user (`budcribar`).
4. Set:
   - **Project key**: `budcribar_PageToMovie` (must match the workflow)
   - **Project name**: `PageToMovie`
5. Generate a **token** (My Account → Security → Generate Tokens). Scope can be “Analyze”.
6. In the GitHub repo: **Settings → Secrets and variables → Actions → New repository secret**
   - Name: `SONAR_TOKEN`
   - Value: the token from step 5
7. (Recommended) Install the **SonarCloud GitHub App** on the repo so PRs get decoration and checks.

If the organization or project key differs, edit both:

- `.github/workflows/sonarcloud.yml` (`/k:` and `/o:`)
- `sonar-project.properties`
- `host/scripts/run-sonar-local.sh` defaults (or override with env vars)

## CI

Workflow: `.github/workflows/sonarcloud.yml`

- Triggers on push/PR that touch `host/**` (and the workflow itself).
- Uses SonarScanner for .NET + Java 21 + .NET 10.
- Builds `PageToMovie.slnx` in Release.
- Uploads analysis when `SONAR_TOKEN` is present; otherwise prints a warning and exits 0 (so fork PRs stay green).

Unit tests run with `continue-on-error` so a flaky test does not block the quality upload. Coverage is **not** required for CPD; Coverlet can be added later if you also want the Coverage measure.

## Local run

```bash
export SONAR_TOKEN=squ_...          # or your token
# optional overrides:
# export SONAR_ORGANIZATION=budcribar
# export SONAR_PROJECT_KEY=budcribar_PageToMovie
# export SONAR_HOST_URL=https://sonarcloud.io

./host/scripts/run-sonar-local.sh
```

Requires the same .NET 10 SDK and a Java 17+ runtime (21 preferred). Results appear on the SonarCloud project page under **Measures → Duplications** and **Issues**.

## Self-hosted SonarQube (optional)

If you prefer an on-prem instance instead of SonarCloud:

1. Run SonarQube Community Build (or Developer+) via Docker.
2. Create a project with the same key.
3. Point the local script or a variant of the workflow at it:

   ```bash
   export SONAR_HOST_URL=http://localhost:9000
   export SONAR_TOKEN=...   # token from the self-hosted instance
   ./host/scripts/run-sonar-local.sh
   ```

The CPD sensor and the properties above work the same way.

## What to do when CPD reports a clone

1. Prefer **extract shared helper** into Core / Adaptation / Engine shared static class (pattern already used by `ClassifierJsonParser`, `ClassifierSharedHelpers`, `GutenbergCleaner`).
2. For intentional thin façades (e.g. Engine `BookToFountainConverter` mapping only), leave them; they are not true clones.
3. For test-only or tool-only copies, keep them under the existing `sonar.cpd.exclusions` paths.
4. After a cleanup PR, re-run analysis and confirm the duplicated-lines measure dropped.

## Files added for this automation

| Path | Role |
|------|------|
| `.github/workflows/sonarcloud.yml` | CI analysis + upload |
| `sonar-project.properties` | Documented settings (kept in sync with the workflow) |
| `host/scripts/run-sonar-local.sh` | One-command local analysis |
| `host/docs/sonar-duplicate-detection.md` | This guide |

No product code changes are required to start receiving reports.
