#!/bin/sh
# Runs once, on a new PostgreSQL volume: PaperDotNet connects as an ordinary role
# that owns its database, so row-level security applies (superusers bypass it).
set -e
psql -v ON_ERROR_STOP=1 -v app_password="$APP_DB_PASSWORD" --username "$POSTGRES_USER" --dbname postgres <<'EOSQL'
CREATE ROLE paperdotnet LOGIN PASSWORD :'app_password' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE DATABASE paperdotnet OWNER paperdotnet;
EOSQL
