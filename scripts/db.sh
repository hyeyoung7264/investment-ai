#!/usr/bin/env bash
# Local, user-owned PostgreSQL cluster for development (no root required).
# Usage: scripts/db.sh init|start|stop|status|psql
set -euo pipefail
PG_BIN="${PG_BIN:-/usr/lib/postgresql/12/bin}"
PGDATA="${INVESTMENT_PGDATA:-$HOME/.local/share/investment-ai/pgdata}"
PGPORT="${INVESTMENT_PGPORT:-55432}"
SOCKDIR="$PGDATA/run"
DB="${INVESTMENT_DB:-investment}"

case "${1:-status}" in
  init)
    if [ -f "$PGDATA/PG_VERSION" ]; then echo "already initialized: $PGDATA"; exit 0; fi
    mkdir -p "$PGDATA"
    # trust auth on localhost only: dev cluster, no secrets in repo
    "$PG_BIN/initdb" -D "$PGDATA" -U postgres --auth=trust --encoding=UTF8 --locale=C.UTF-8 >/dev/null
    mkdir -p "$SOCKDIR"
    {
      echo "port = $PGPORT"
      echo "listen_addresses = '127.0.0.1'"
      echo "unix_socket_directories = '$SOCKDIR'"
      echo "shared_buffers = 256MB"
      echo "work_mem = 32MB"
      echo "max_wal_size = 2GB"
    } >> "$PGDATA/postgresql.conf"
    "$0" start
    "$PG_BIN/createdb" -h 127.0.0.1 -p "$PGPORT" -U postgres "$DB"
    "$PG_BIN/createdb" -h 127.0.0.1 -p "$PGPORT" -U postgres "${DB}_test"
    echo "initialized $PGDATA (port $PGPORT)"
    ;;
  start)
    mkdir -p "$SOCKDIR"
    if "$PG_BIN/pg_ctl" -D "$PGDATA" status >/dev/null 2>&1; then echo "running"; exit 0; fi
    "$PG_BIN/pg_ctl" -D "$PGDATA" -l "$PGDATA/server.log" -w start >/dev/null && echo "started on port $PGPORT"
    ;;
  stop)   "$PG_BIN/pg_ctl" -D "$PGDATA" -m fast stop ;;
  status) "$PG_BIN/pg_ctl" -D "$PGDATA" status ;;
  psql)   shift; exec psql -h 127.0.0.1 -p "$PGPORT" -U postgres "$DB" "$@" ;;
  *) echo "usage: $0 init|start|stop|status|psql"; exit 1 ;;
esac
