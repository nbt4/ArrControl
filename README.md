# ArrControl

ArrControl is a self-hosted control centre for Arr services and Prowlarr. One web interface combines missing items, queues, history, health, and connected services.

The web service selector currently offers Sonarr, Radarr, Lidarr, Readarr,
Whisparr, and Prowlarr. Download clients, media servers, and request managers
are not selectable in the UI until they have their own operational workflow.

## Run it online with Docker

ArrControl needs exactly two running containers: `arrcontrol` and PostgreSQL. Compose also runs a short-lived initialization container before ArrControl starts; it restricts ownership and permissions on the persistent Data Protection key ring. The browser interface must be served through HTTPS because secure login cookies intentionally do not work over plain HTTP.

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
key_owner="$(docker run --rm --entrypoint /bin/sh nobentie/arrcontrol:1.0.0 -c 'printf "%s:%s" "$(id -u)" "$(id -g)"')"
sudo chown "$key_owner" /opt/docker/arrcontrol/secrets/master-key
sudo chmod 600 /opt/docker/arrcontrol/secrets/master-key
```

ArrControl runs as an unprivileged user. On local Docker Compose installations,
file-backed secrets may retain the source file owner and permissions when mounted
into the container. The key must therefore be owned by the UID/GID used by the
image as above; mode `0600` alone is not sufficient when the host owner is
different. Do not make the key world-readable. If you pin a different ArrControl
image version, substitute that same image reference in the `docker run` command.

### 3. Attach it to your existing Arr/proxy networks

The supplied Compose file expects Docker networks named `proxy` and `starr`, as in a typical Arr stack. Create them only when they do not already exist:

```bash
docker network create proxy
docker network create starr
```

If your networks use different names, change only the two external network names at the bottom of `compose.yaml`.

### 4. Start ArrControl

```bash
docker compose up -d
```

ArrControl applies pending database migrations during startup, after PostgreSQL is
healthy. If a migration fails, ArrControl stays stopped; inspect its logs with
`docker compose logs arrcontrol`.

Use the pinned version in `.env` for normal operation. To test the newest build, set `ARRCONTROL_IMAGE=nobentie/arrcontrol:latest` and run `docker compose pull && docker compose up -d`.

### 5. Point your reverse proxy at ArrControl

Your proxy must reach `http://arrcontrol:8080` over the shared `proxy` network by
default. Set `ARRCONTROL_HTTP_PORT` in `.env` to use another container port, and
use the same port in the proxy configuration. Example Caddy site:

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
docker compose up -d
```

Back up both `${ARRCONTROL_DATA_DIR}/postgres` and `${ARRCONTROL_DATA_DIR}/data-protection` before an update. See [backup and recovery](docs/BACKUP_RESTORE.md) for the full procedure.

## Image tags

| Tag | Intended use |
| --- | --- |
| `nobentie/arrcontrol:1.0.8` | Pinned production release |
| `nobentie/arrcontrol:1.0` | Current 1.0 release line |
| `nobentie/arrcontrol:latest` | Evaluation / newest release |

The current manually published image targets `linux/amd64`.

## Documentation

- [Using ArrControl](docs/USER_GUIDE.md)
- [Administration and accounts](docs/ADMIN_GUIDE.md)
- [Provider troubleshooting](docs/PROVIDER_TROUBLESHOOTING.md)
- [Backup and recovery](docs/BACKUP_RESTORE.md)
- [Development](docs/DEVELOPMENT.md) and [architecture](docs/SDD.md)

## License

MIT — see [LICENSE](LICENSE).
