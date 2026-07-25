# Azure Storage Backup

A single-user application that backs up local files to an Azure Storage account (Blob only). Access is open by default; set `Auth__Password` to put a password in front of the UI.

## Stack

- **Backend**: .NET 10 Minimal API + EF Core (SQLite) + Azure.Storage.Blobs
- **Frontend**: Vite + React + TypeScript
- **Packaging**: a single multi-arch Docker image (`linux/amd64` + `linux/arm64`) in which the backend serves both the API and the built frontend. Compression uses 7-Zip (`p7zip-full`) bundled in the image.

## Repository layout

```
.
├── backend/                          # .NET 10 Minimal API
│   └── src/AzureStorageBackup.Api/
│       ├── Endpoints/                # Minimal API endpoint groups (all under /api)
│       ├── Services/                 # Business logic (Azure storage / backup engine)
│       ├── Data/                     # EF Core DbContext + migrations
│       ├── Models/                   # Entities and DTOs
│       └── Program.cs                # Composition root
├── frontend/                         # Vite + React + TS  (src/{api,components,pages})
├── Dockerfile                        # Multi-stage, multi-arch image
├── docker-compose.yml
└── .github/workflows/docker-publish.yml
```

## Local development

**Backend** (port 5122):

```bash
cd backend
dotnet run --project src/AzureStorageBackup.Api
```

**Frontend** (port 5173; `/api` is proxied to the backend by Vite):

```bash
cd frontend
npm install
npm run dev
```

## Tests

```bash
cd backend && dotnet test
```

Integration tests that talk to Azure use [Azurite](https://github.com/Azure/Azurite); they are skipped automatically when Azurite is not reachable on `127.0.0.1:10000`.

## Docker

The image is self-contained: the backend hosts the API and the compiled SPA on **one** HTTP port (`8080`). Rehydration of Archive-tier blobs, 7-Zip compression, restore and repair all run inside this container, so the host directories you want to back up (and restore into) must be mounted.

Build and run locally:

```bash
docker build -t azurestoragebackup .
docker run -d --name asb -p 8080:8080 \
  -v asb-data:/data -v asb-keys:/keys -v asb-temp:/temp \
  -v /path/to/files:/backup-source \
  azurestoragebackup
```

Then open <http://localhost:8080>. Inside the app, set a backup's *local root* (and any restore target) to a path that exists **inside the container**, e.g. `/backup-source`.

Or with Docker Compose (`docker compose up --build`), after copying `.env.example` to `.env`.

### Environment variables

ASP.NET Core maps nested config keys with a double underscore (`Section__Key`). All are optional; defaults shown are the in-container defaults set by the image.

| Variable | Purpose | Default (image) |
| --- | --- | --- |
| `ConnectionStrings__Sqlite` | SQLite connection string (app database). | `Data Source=/data/app.db` |
| `DataProtection__KeysPath` | Directory for the Data Protection key ring used to encrypt secrets at rest (account keys, backup passwords). **Must be persisted** — losing it makes stored secrets undecryptable. | `/keys` |
| `Backup__TempPath` | Working area root: compression, staging, restore, check, dead-weight compaction, and verbose logs live under here. Can grow large during a backup/restore. | `/temp` |
| `Backup__Root` | Confines every local path — backup source, restore target, and the folder picker — to this directory. Unset = no limit. | *(unset)* |
| `Scheduler__Enabled` | Enable the cron scheduler for scheduled backup/check/cleanup tasks. | `true` |
| `Scheduler__TimeZone` | IANA time-zone id used to evaluate cron expressions. | `UTC` |
| `Auth__Password` | Password required to open the UI. Unset or empty = no authentication (the app logs a warning at startup). There is no username. | *(unset)* |
| `Cors__AllowedOrigins__0` | Allowed browser origin. Not needed for the single-image deployment (frontend is same-origin); relevant only when hosting the SPA separately, in which case every origin must be listed explicitly. `*` is ignored (it cannot be combined with cookie credentials) and logs a warning at startup. | *(none — no cross-origin request is allowed)* |
| `ASPNETCORE_URLS` | Listen address. | `http://+:8080` |
| `ASPNETCORE_ENVIRONMENT` | ASP.NET environment. | `Production` |

> The image runs with `ASPNETCORE_ENVIRONMENT=Production`, where **no** cross-origin browser request is allowed unless you list origins yourself. The Vite dev-server origin `http://localhost:5173` is preconfigured in `appsettings.Development.json` only, so it applies to `dotnet run` during development and never to the image.

> Azure credentials are **not** configured through environment variables — each storage account is added in the UI and its key is encrypted at rest with the Data Protection key ring in `/keys`. If that directory is lost, the app starts in recovery mode and asks you to re-enter each credential; see [keyring-loss-recovery-design.md](docs/keyring-loss-recovery-design.md).

> Tuning values such as the staging-area limit, retention defaults and the dead-weight compaction threshold live in the database, not in environment variables — change them on the **Settings** page and they take effect immediately, without a restart.

> Setting `Auth__Password` puts a single password in front of the whole UI — there is no username, and changing the password means changing the variable and restarting. The session cookie is signed with the Data Protection key ring in `/keys`, so losing that directory signs you out and you will have to log in again; the password itself is read straight from the environment, so a lost key ring never locks you out.
>
> **Serve this behind an HTTPS reverse proxy in production.** Over plain HTTP both the password and the session cookie travel in the clear — the password gate keeps out people who do not know it, not anyone who can watch the traffic.

> `Backup__Root` is a **safety filter only**: it never rewrites or shortens a path, and it is not a base for relative paths. Paths are stored and displayed in full — with a root of `/nas`, a backup source still reads `/nas/photos/2024`. It constrains paths **inside the container**, so use it together with your volume mounts: mount everything you want to back up beneath that one directory.
>
> Symbolic links are resolved before the check, so a link inside the root that points outside it is rejected — including when the link is a middle segment of the path. Backup configurations whose local root falls outside the root are kept but refuse to run, so setting this on an existing install tells you which ones need attention instead of silently dropping them.

### Volumes / mounts

| Container path | Purpose | Persist? |
| --- | --- | --- |
| `/data` | SQLite database (`app.db`). | **Yes** |
| `/keys` | Data Protection key ring. Losing it makes stored account keys/passwords undecryptable. | **Yes** |
| `/temp` | Backup/restore working area (compress, staged, restore, check, compact, verbose logs). Safe to discard, but needs free space. | Optional (needs disk space) |
| *(your choice, e.g. `/backup-source`)* | Host directories to back up. Mount **read-only** if you only back up. A backup's *local root* is set to this in-container path. | Bind mount |
| *(your choice, e.g. `/restore-target`)* | Where restores write. Mount read-write. | Bind mount |

`GET /api/system/paths` returns the resolved absolute paths at runtime (PRD §6 "Directories"), useful when configuring Docker volume mappings.

## Published image (GHCR)

Multi-arch images are published to the GitHub Container Registry:

```
ghcr.io/secretnest/azurestoragebackup:latest
```

Publishing is a **manual** GitHub Action (`.github/workflows/docker-publish.yml`, `workflow_dispatch`) that builds for `linux/amd64` and `linux/arm64` with Buildx and pushes to GHCR.

## Documentation

Roadmap: `docs/roadmap.md`. Full requirements: `docs/product-requirements.md`. Backup engine design: `docs/m4-backup-engine-design.md`.
