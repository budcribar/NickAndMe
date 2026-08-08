#!/usr/bin/env bash
# Local SonarCloud / SonarQube analysis for PageToMovie (duplication focus).
#
# Prerequisites:
#   - .NET 10 SDK
#   - Java 17+ (21 recommended; already present on many agents)
#   - SONAR_TOKEN environment variable (SonarCloud user token or project token)
#   - Optional: SONAR_HOST_URL (default https://sonarcloud.io)
#   - Optional: SONAR_ORGANIZATION / SONAR_PROJECT_KEY overrides
#
# Usage (from repo root or host/):
#   export SONAR_TOKEN=squ_...
#   ./host/scripts/run-sonar-local.sh
#
# The script mirrors the GitHub Action thresholds so local results match CI.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HOST="${ROOT}/host"
cd "${HOST}"

if [[ -z "${SONAR_TOKEN:-}" ]]; then
  echo "error: SONAR_TOKEN is required (create a token at https://sonarcloud.io and export it)." >&2
  exit 1
fi

ORG="${SONAR_ORGANIZATION:-budcribar}"
KEY="${SONAR_PROJECT_KEY:-budcribar_PageToMovie}"
NAME="${SONAR_PROJECT_NAME:-PageToMovie}"
HOST_URL="${SONAR_HOST_URL:-https://sonarcloud.io}"

echo "==> Installing / updating SonarScanner for .NET"
dotnet tool install --global dotnet-sonarscanner >/dev/null 2>&1 || \
  dotnet tool update --global dotnet-sonarscanner >/dev/null 2>&1 || true
export PATH="${PATH}:${HOME}/.dotnet/tools"

echo "==> Begin analysis (project ${KEY})"
dotnet sonarscanner begin \
  /k:"${KEY}" \
  /o:"${ORG}" \
  /n:"${NAME}" \
  /d:sonar.token="${SONAR_TOKEN}" \
  /d:sonar.host.url="${HOST_URL}" \
  /d:sonar.cs.analyzeGeneratedCode=false \
  /d:sonar.exclusions="**/bin/**,**/obj/**,**/node_modules/**,**/playwright/**,**/*.min.js,**/wwwroot/lib/**,**/PageToMovie.Fakes/**/Generated/**" \
  /d:sonar.cpd.exclusions="**/PageToMovie.Tests/**,**/PageToMovie.UiTests/**,**/PageToMovie.LoadSim/**,**/tools/**" \
  /d:sonar.cpd.cs.minimumTokens=50 \
  /d:sonar.cpd.cs.minimumLines=4 \
  /d:sonar.sourceEncoding=UTF-8

echo "==> Restore + Release build"
dotnet restore PageToMovie.slnx
dotnet build PageToMovie.slnx -c Release --no-restore -p:RunAnalyzersDuringBuild=true

echo "==> End analysis / upload"
dotnet sonarscanner end /d:sonar.token="${SONAR_TOKEN}"

echo "==> Done. Open the project on ${HOST_URL} → Measures → Duplications"
echo "    (and Issues filtered by rule key containing 'duplicated' / CPD)."
