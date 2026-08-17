#!/usr/bin/env bash
#
# Run a CI build on the local self-hosted runner, and take the runner down again afterwards.
#
#   tools/ci-build.sh                 # WebGL, no deploy
#   tools/ci-build.sh macos           # macOS build
#   tools/ci-build.sh all deploy      # everything, and publish to Pages
#
# The runner exists only for the length of one build. It is not a service and it is not left
# listening: a machine that is quietly available to run jobs is exactly what should not be sitting
# there on a public repository, even with the triggers locked to workflow_dispatch.
#
# Requires: gh (authenticated), a runner configured in ~/actions-runner (see README).

set -euo pipefail

repo=$(gh repo view --json nameWithOwner -q .nameWithOwner)
runner_dir="${RUNNER_DIR:-$HOME/actions-runner}"

targets="${1:-webgl}"
deploy=false
[ "${2:-}" = "deploy" ] && deploy=true

[ -x "$runner_dir/run.sh" ] || { echo "No runner in $runner_dir - see the CI section of README.md" >&2; exit 1; }

# Start the runner first, so the job is picked up the moment it is queued rather than sitting in a
# queue while the runner boots.
"$runner_dir/run.sh" > "$runner_dir/run.log" 2>&1 &
runner_pid=$!

# Whatever happens after this - a failed build, a Ctrl-C, an error in this script - the runner goes
# down. That is the whole point of the script, so it is a trap rather than a line at the end.
cleanup() {
  kill "$runner_pid" 2>/dev/null || true
  wait "$runner_pid" 2>/dev/null || true
  echo "Runner stopped."
}
trap cleanup EXIT

echo "Runner up (pid $runner_pid). Dispatching $targets, deploy=$deploy ..."
gh workflow run build.yml -f "targets=$targets" -f "deploy_pages=$deploy"

# The run id is not returned by `workflow run`, so wait for the newest run to appear. Matching on
# status alone would pick up an unrelated run someone started from the web UI a moment earlier.
run_id=""
for _ in $(seq 1 30); do
  run_id=$(gh run list --workflow=build.yml --event=workflow_dispatch --limit 1 \
    --json databaseId,status -q '.[] | select(.status != "completed") | .databaseId' || true)
  [ -n "$run_id" ] && break
  sleep 2
done
[ -n "$run_id" ] || { echo "No run appeared - dispatch may have failed." >&2; exit 1; }

echo "Watching run $run_id: https://github.com/$repo/actions/runs/$run_id"
gh run watch "$run_id" --exit-status
