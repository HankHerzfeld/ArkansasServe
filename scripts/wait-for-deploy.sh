#!/usr/bin/env bash
#
# wait-for-deploy.sh — wait for the deploy workflows of ONE commit, pinned by SHA.
#
# WHY THIS EXISTS
# ---------------
# The obvious way to wait after a merge is:
#
#     gh run list --workflow=... --event=push --limit 1     # <-- WRONG
#
# That asks "what is the newest run?", not "what happened to MY commit". GitHub does
# not create the run the instant the merge lands, so between the merge and the run
# appearing, "newest" is still the PREVIOUS commit's run — which is already green.
# You read success, conclude your change is live, and verify against a build that does
# not contain it. That happened twice in one session; the second time it produced a
# confident "deployed and verified" report about a fix that was still sitting in the
# queue.
#
# The fix is to pin on the merge commit's SHA and, crucially, to WAIT FOR THE RUN TO
# EXIST before waiting for it to finish. Absence of a run is not success.
#
# USAGE
#   scripts/wait-for-deploy.sh [SHA]        # defaults to HEAD
#
# Exits 0 only if every deploy workflow found for that SHA concluded "success".
#
set -euo pipefail

SHA="$(git rev-parse "${1:-HEAD}")"
SHORT="${SHA:0:7}"

# Deploy workflows this repo runs on push to main. A frontend-only change still starts
# both, so both are waited on; adjust here if the pipeline gains another.
WORKFLOWS=("Azure Static Web Apps CI/CD" "Build and deploy dotnet core app to Azure Function App - func-arkansas-serve-arksrv")

APPEAR_TIMEOUT=${APPEAR_TIMEOUT:-300}   # how long to wait for runs to be CREATED
FINISH_TIMEOUT=${FINISH_TIMEOUT:-1800}  # how long to wait for them to finish

runs_for_sha() {
  gh run list --commit "$SHA" --event push --limit 20 \
    --json databaseId,workflowName,status,conclusion 2>/dev/null || echo '[]'
}

echo "Waiting for deploys of $SHORT ..."

# ── Phase 1: wait for the runs to EXIST ─────────────────────────────────────────────
# This is the step whose absence caused the bug. Do not skip it.
waited=0
while :; do
  count="$(runs_for_sha | jq 'length')"
  [ "$count" -gt 0 ] && break
  if [ "$waited" -ge "$APPEAR_TIMEOUT" ]; then
    echo "ERROR: no workflow run appeared for $SHORT after ${APPEAR_TIMEOUT}s." >&2
    echo "       Do NOT treat this as deployed — a merge that triggers no run has happened here before." >&2
    exit 1
  fi
  sleep 10; waited=$((waited + 10))
done

# ── Phase 2: wait for them to FINISH ────────────────────────────────────────────────
waited=0
while :; do
  json="$(runs_for_sha)"
  pending="$(jq '[.[] | select(.status != "completed")] | length' <<<"$json")"
  [ "$pending" -eq 0 ] && break
  if [ "$waited" -ge "$FINISH_TIMEOUT" ]; then
    echo "ERROR: deploys of $SHORT still running after ${FINISH_TIMEOUT}s." >&2
    jq -r '.[] | "  \(.workflowName): \(.status)"' <<<"$json" >&2
    exit 1
  fi
  sleep 15; waited=$((waited + 15))
done

json="$(runs_for_sha)"
jq -r '.[] | "  \(.workflowName): \(.conclusion)"' <<<"$json"

failed="$(jq '[.[] | select(.conclusion != "success")] | length' <<<"$json")"
if [ "$failed" -gt 0 ]; then
  echo "FAILED: $failed deploy run(s) for $SHORT did not succeed." >&2
  exit 1
fi

# Warn if a workflow we expect never ran for this commit — green-for-what-did-run is
# not the same as everything-deployed.
for w in "${WORKFLOWS[@]}"; do
  if [ "$(jq --arg w "$w" '[.[] | select(.workflowName == $w)] | length' <<<"$json")" -eq 0 ]; then
    echo "NOTE: no '$w' run for $SHORT (skipped, or never triggered)." >&2
  fi
done

echo "All deploys for $SHORT succeeded."
echo
echo "Deployed != served: verify the FILE, cache-busted, before clicking through —"
echo "  curl -s \"https://arkansasserve.com/js/<file>.js?cb=\$(date +%s)\" -H 'Cache-Control: no-cache' | grep <new code>"
