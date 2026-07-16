# Backup, Restore, and Upgrade

PostgreSQL is authoritative. A complete recoverable set contains a custom-format PostgreSQL dump, every credential-encryption master-key version still referenced by `credentials.key_version`, and the data-protection key ring. Store these parts together under separate access control from the running host; a database dump alone cannot decrypt provider credentials.

## Backup

1. Stop the `app` service so no mutation or worker checkpoint races the dump; leave `db` running.
2. Record the exact ArrControl image digest, PostgreSQL major version, UTC time, and mounted master-key versions without recording key contents.
3. Run `docker compose exec -T db pg_dump --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --format=custom --no-owner --no-acl > arrcontrol.dump` from a protected shell. Do not pass the database password on the command line.
4. Copy the credential master-key files and the `data-protection-keys` volume through a secret-aware backup mechanism. Preserve ownership/mode metadata.
5. Validate the dump with `pg_restore --list arrcontrol.dump`, encrypt the backup at rest, calculate a checksum, and test a restore on an isolated host. A successful dump command alone is not restore evidence.

Never write the dump, keys, or database password into the repository, CI artifacts, application logs, or a shared diagnostics bundle.

## Restore

1. Provision an empty PostgreSQL 17 database and keep ArrControl stopped.
2. Restore with `pg_restore --exit-on-error --no-owner --no-acl --dbname "$DATABASE_URL" arrcontrol.dump` using a protected environment/secret file, not a URL committed to shell history.
3. Restore every referenced master-key version at its configured absolute path and restore the data-protection key ring with owner-only access.
4. Run the target image's `database migrate` command exactly once; rerun is safe and proves idempotency.
5. Start ArrControl, verify `/health/ready`, log in, inspect instance/credential configured flags, projection freshness, jobs, and audit continuity, then execute only read-only provider probes.
6. Keep the old deployment and backup immutable until the observation window has passed.

If a key version is missing, do not delete or overwrite its credential rows: restore the correct key. If the data-protection ring is missing, local database sessions remain represented but OIDC in-flight state/logout hints cannot be recovered; require fresh browser/OIDC authentication.

## Upgrade and rollback

Before an upgrade, stop `app`, take and restore-test the backup, pull the pinned target digest, run the target `migrate` one-shot, then start the target. Verify readiness and the compatibility report before enabling broad operations. Database migrations are forward-only. Rollback means stop the target, discard its upgraded database, restore the pre-upgrade dump and matching keys, and restart the previous pinned image; never run EF down migrations against the only copy.

`tests/data-lifecycle/run.sh` automates an isolated PostgreSQL 17 custom dump, listing, restore, current migration/idempotency, encrypted-credential-row preservation, and upgrade to the current schema. CI selects the latest earlier stable `vX.Y.Z` image when one exists. Before the first tagged stable image, it starts at the initial released schema migration instead, so the upgrade path remains exercised without inventing a previous image. The test uses generated ephemeral credentials and deletes its database/network; it never reads a deployment `.env` or persistent volume.
