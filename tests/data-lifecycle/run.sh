#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
run_id="${RANDOM}-$$"
network="arrcontrol-data-lifecycle-${run_id}"
database="arrcontrol-data-lifecycle-db-${run_id}"
password="$(openssl rand -hex 32)"
nuget_volume="arrcontrol-data-lifecycle-nuget"

cleanup() {
  docker rm --force "${database}" >/dev/null 2>&1 || true
  docker network rm "${network}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker network create "${network}" >/dev/null
docker run --detach --name "${database}" --network "${network}" \
  --shm-size 256m \
  --env POSTGRES_DB=arrcontrol_source \
  --env POSTGRES_USER=arrcontrol \
  --env POSTGRES_PASSWORD="${password}" \
  postgres:17-alpine >/dev/null

for attempt in {1..30}; do
  if docker exec "${database}" pg_isready --username arrcontrol --dbname arrcontrol_source >/dev/null 2>&1; then
    break
  fi
  test "${attempt}" -lt 30
  sleep 1
done

connection() {
  printf 'Host=%s;Port=5432;Database=%s;Username=arrcontrol;Password=%s' \
    "${database}" "$1" "${password}"
}

migrate_current() {
  docker run --rm --network "${network}" \
    --volume "${root}:/src" \
    --volume "${nuget_volume}:/root/.nuget/packages" \
    --workdir /src \
    --env "ConnectionStrings__Database=$(connection "$1")" \
    mcr.microsoft.com/dotnet/sdk:9.0 \
    dotnet /src/src/ArrControl.Api/bin/Release/net9.0/ArrControl.Api.dll database migrate
}

psql_db() {
  local target_database="$1"
  shift
  docker exec --env PGPASSWORD="${password}" --interactive "${database}" \
    psql --username arrcontrol --dbname "${target_database}" --set ON_ERROR_STOP=1 "$@"
}

docker run --rm --volume "${root}:/src" --volume "${nuget_volume}:/root/.nuget/packages" \
  --workdir /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet build src/ArrControl.Api/ArrControl.Api.csproj --configuration Release

migrate_current arrcontrol_source
psql_db arrcontrol_source >/dev/null <<'SQL'
WITH inserted_instance AS (
  INSERT INTO service_instances
    (id, name, kind, base_url, enabled, tls_verification_enabled,
     allow_private_network_access, created_at, updated_at)
  VALUES
    (gen_random_uuid(), 'Backup sentinel', 'sonarr', 'https://backup.example.invalid/',
     true, true, false, now(), now())
  RETURNING id
)
INSERT INTO credentials
  (id, instance_id, purpose, ciphertext, nonce, tag, key_version, created_at, updated_at)
SELECT gen_random_uuid(), id, 'api-key', decode('01020304', 'hex'),
       decode('000102030405060708090a0b', 'hex'),
       decode('000102030405060708090a0b0c0d0e0f', 'hex'), 1, now(), now()
FROM inserted_instance;
SQL

docker exec --env PGPASSWORD="${password}" "${database}" \
  pg_dump --username arrcontrol --dbname arrcontrol_source --format custom \
  --no-owner --no-acl --file /tmp/arrcontrol.dump
docker exec "${database}" pg_restore --list /tmp/arrcontrol.dump >/dev/null
docker exec --env PGPASSWORD="${password}" "${database}" \
  createdb --username arrcontrol arrcontrol_restore
docker exec --env PGPASSWORD="${password}" "${database}" \
  pg_restore --username arrcontrol --dbname arrcontrol_restore \
  --no-owner --no-acl --exit-on-error /tmp/arrcontrol.dump
migrate_current arrcontrol_restore

restored_instances="$(psql_db arrcontrol_restore --tuples-only --no-align \
  --command "SELECT count(*) FROM service_instances WHERE name = 'Backup sentinel'")"
restored_credentials="$(psql_db arrcontrol_restore --tuples-only --no-align \
  --command "SELECT count(*) FROM credentials WHERE purpose = 'api-key'")"
pending_restore="$(psql_db arrcontrol_restore --tuples-only --no-align \
  --command "SELECT count(*) FROM \"__EFMigrationsHistory\"")"
test "${restored_instances}" = "1"
test "${restored_credentials}" = "1"
test "${pending_restore}" -gt 0

docker exec --env PGPASSWORD="${password}" "${database}" \
  createdb --username arrcontrol arrcontrol_upgrade

if [[ -n "${ARRCONTROL_PREVIOUS_IMAGE:-}" ]]; then
  docker pull "${ARRCONTROL_PREVIOUS_IMAGE}"
  docker run --rm --network "${network}" \
    --env "ConnectionStrings__Database=$(connection arrcontrol_upgrade)" \
    "${ARRCONTROL_PREVIOUS_IMAGE}" database migrate
else
  docker run --rm --network "${network}" \
    --volume "${root}:/src" \
    --volume "${nuget_volume}:/root/.nuget/packages" \
    --workdir /src \
    --env "ConnectionStrings__Database=$(connection arrcontrol_upgrade)" \
    mcr.microsoft.com/dotnet/sdk:9.0 sh -lc \
    'dotnet tool restore >/dev/null && dotnet ef database update 20260714221653_InitialFoundation --project src/ArrControl.Infrastructure --startup-project src/ArrControl.Api --no-build'
fi

psql_db arrcontrol_upgrade >/dev/null <<'SQL'
INSERT INTO users
  (id, email, normalized_email, locale, time_zone, state, created_at, updated_at)
VALUES
  (gen_random_uuid(), 'upgrade@example.invalid', 'UPGRADE@EXAMPLE.INVALID',
   'en', 'UTC', 'active', now(), now());
SQL
migrate_current arrcontrol_upgrade
migrate_current arrcontrol_upgrade

upgraded_users="$(psql_db arrcontrol_upgrade --tuples-only --no-align \
  --command "SELECT count(*) FROM users WHERE normalized_email = 'UPGRADE@EXAMPLE.INVALID'")"
latest_migration="$(find "${root}/src/ArrControl.Infrastructure/Persistence/Migrations" \
  -maxdepth 1 -name '*.cs' ! -name '*.Designer.cs' ! -name '*ModelSnapshot.cs' \
  -printf '%f\n' | sort | tail -1 | sed -E 's/\.cs$//')"
applied_latest="$(psql_db arrcontrol_upgrade --tuples-only --no-align \
  --command "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '${latest_migration}'")"
test "${upgraded_users}" = "1"
test "${applied_latest}" = "1"

echo "Backup/restore and upgrade validation passed."
