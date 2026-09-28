#!/usr/bin/env bash
# Daily paper job: refresh market data, advance all active paper sessions, log the result.
# Run on trading-day mornings before the open (e.g. 08:10 KST): KRX official records of the previous session are
# published T+1, DART filings of the previous day are complete; orders are for that morning's open. Paper only — this system has no broker connection.
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
