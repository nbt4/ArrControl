# ArrControl

ArrControl is a self-hosted control centre for Arr services, download clients, and media servers. One web interface combines missing items, queues, history, health, and connected services.

## Run it online with Docker

ArrControl needs exactly two running containers: `arrcontrol` and PostgreSQL. The browser interface must be served through HTTPS because secure login cookies intentionally do not work over plain HTTP.

### 1. Download the two configuration files

```bash
mkdir -p /opt/docker/arrcontrol
cd /opt/docker/arrcontrol
curl -fsSLO https://raw.githubusercontent.com/nbt4/ArrControl/main/compose.yaml
curl -fsSLo .env https://raw.githubusercontent.com/nbt4/ArrControl/main/.env.example
```

### 2. Set passwords and create the encryption key

Edit `.env` and replace both `CHANGE_ME` values with long random passwords. Set `ARRCONTROL_PUBLIC_URL` to the HTTPS URL you will use.

```bash
mkdir -p /opt/docker/arrcontrol/secrets
openssl rand -base64 32 > /opt/docker/arrcontrol/secrets/master-key
chmod 600 /opt/docker/arrcontrol/secrets/master-key
```

### 3. Attach it to your existing Arr/proxy networks

The supplied Compose file expects Docker networks named `proxy` and `starr`, as in a typical Arr stack. Create them only when they do not already exist:

```bash
docker network create proxy
docker network create starr
```

If your networks use different names, change only the two external network names at the bottom of `compose.yaml`.

### 4. Start the database, migrate once, then start ArrControl

```bash
docker compose up -d arrcontrol-db
docker compose run --rm --no-deps arrcontrol database migrate
docker compose up -d
```

Use the pinned version in `.env` for normal operation. To test the newest build, set `ARRCONTROL_IMAGE=nobentie/arrcontrol:latest` and run `docker compose pull && docker compose up -d`.

### 5. Point your reverse proxy at ArrControl

Your proxy must reach `http://arrcontrol:8080` over the shared `proxy` network. Example Caddy site:

```caddy
arrcontrol.example.com {
    reverse_proxy arrcontrol:8080
}
```

Open `https://arrcontrol.example.com`, then sign in with `ARRCONTROL_ADMIN_EMAIL` and `ARRCONTROL_ADMIN_PASSWORD` from `.env`.

For Arr containers behind Gluetun, use the Gluetun service name and its internal port when adding a service, for example `http://arr_vpn:8989` for Sonarr. Enable private-network access for that service in ArrControl.

## Updates

```bash
cd /opt/docker/arrcontrol
docker compose pull
docker compose run --rm --no-deps arrcontrol database migrate
docker compose up -d
```

Back up both `${ARRCONTROL_DATA_DIR}/postgres` and `${ARRCONTROL_DATA_DIR}/data-protection` before an update. See [backup and recovery](docs/BACKUP_RESTORE.md) for the full procedure.

## Image tags

| Tag | Intended use |
| --- | --- |
| `nobentie/arrcontrol:1.0.0` | Pinned production release |
| `nobentie/arrcontrol:1.0` | Current 1.0 release line |
| `nobentie/arrcontrol:latest` | Evaluation / newest release |

Images support `linux/amd64` and `linux/arm64`.

## Documentation

- [Using ArrControl](docs/USER_GUIDE.md)
- [Administration and accounts](docs/ADMIN_GUIDE.md)
- [Provider troubleshooting](docs/PROVIDER_TROUBLESHOOTING.md)
- [Backup and recovery](docs/BACKUP_RESTORE.md)
- [Developer and architecture reference](docs/README.md)

## License

MIT — see [LICENSE](LICENSE).
