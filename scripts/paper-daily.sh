#!/usr/bin/env bash
# Daily post-close job: refresh market data, advance all active paper sessions, log the result.
# Run after 18:00 KST on trading days. Paper only — this system has no broker connection.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p reports/paper
log="reports/paper/daily-$(date +%Y%m%d).log"
{
  echo "=== $(date -Is) paper daily"
  scripts/db.sh start
  dotnet run --project src/Investment.Cli -c Release -- paper daily --ingest
  dotnet run --project src/Investment.Cli -c Release -- paper status
} >> "$log" 2>&1
echo "logged to $log"
