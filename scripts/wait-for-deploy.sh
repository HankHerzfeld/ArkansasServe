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

# LOAD-BEARING: `gh run list --commit` matches only the FULL 40-char SHA. Given a short
# one it returns an empty list with exit 0 — indistinguishable from "no run exists" — so
# this script would sit in phase 1 and then wrongly report that nothing was ever built.
# Do not "simplify" this rev-parse away, and do not pass "$1" straight through.
SHA="$(git rev-parse "${1:-HEAD}")"
if [ ${#SHA} -ne 40 ]; then
  echo "ERROR: could not resolve '${1:-HEAD}' to a full 40-char SHA (got '$SHA')." >&2
  exit 1
fi
SHORT="${SHA:0:7}"

# Deploy workflows this repo runs on push to main. A frontend-only change still starts
# both, so both are waited on; adjust here if the pipeline gains another.
WORKFLOWS=("Azure Static Web Apps CI/CD" "Build and deploy dotnet core app to Azure Function App - func-arkansas-serve-arksrv")

# ⏱ HARD OVERALL CAP — 30 minutes, covering BOTH phases together.
#
# Waiting is not free: every polling loop is billed session time, and an uncapped wait on a
# deploy that will never finish burns it for nothing. A deploy that has not completed in half
# an hour is not slow, it is broken, and the right response is to fail and say so rather than
# keep watching. Azure SWA has already produced exactly this: a run that polled "InProgress"
# for ten minutes and then admitted it did not know whether it had succeeded.
#
# Raise it deliberately per invocation if a specific job genuinely needs longer:
#   DEPLOY_WAIT_TIMEOUT=2700 scripts/wait-for-deploy.sh
DEPLOY_WAIT_TIMEOUT=${DEPLOY_WAIT_TIMEOUT:-1800}

# Phase budgets. APPEAR is short because a run that has not been CREATED within a few minutes
# almost certainly never will be (a merge that fires no workflow has happened here before).
# Neither phase may exceed the overall cap, and their sum is enforced against it below.
APPEAR_TIMEOUT=${APPEAR_TIMEOUT:-300}
FINISH_TIMEOUT=${FINISH_TIMEOUT:-$DEPLOY_WAIT_TIMEOUT}

START_TS=$(date +%s)
# Fails the whole script the moment the overall budget is spent, whichever phase is running.
check_overall_budget() {
  local elapsed=$(( $(date +%s) - START_TS ))
  if [ "$elapsed" -ge "$DEPLOY_WAIT_TIMEOUT" ]; then
    echo "ERROR: gave up waiting on $SHORT after ${elapsed}s (cap ${DEPLOY_WAIT_TIMEOUT}s)." >&2
    echo "       A deploy this slow is stuck, not slow. Check the run, then re-trigger." >&2
    exit 1
  fi
}

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
  check_overall_budget
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
  check_overall_budget
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
