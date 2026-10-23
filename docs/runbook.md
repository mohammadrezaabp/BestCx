# Operations runbook

## Start

Follow the root README: generate `.env`, then run `docker compose up -d --build`. Migrations require healthy PostgreSQL; the complete pipeline also requires Kafka and object storage.

Use `docker compose ps` and `docker compose logs --tail 100 cx-platform` to inspect startup. `/health/live` reports liveness and `/health/ready` reports dependency readiness. The console uses port 18080 and the customer portal uses 18081.

Read the generated administrator password from your local `.env`. Never paste that file into an issue or commit it. There are no default customer credentials; manage each customer's portal account from the operator console.

## Configuration and code changes

Edit `.env` locally and run `docker compose up -d cx-platform`. Restart alone does not replace environment values. After application source changes, build with `docker compose build cx-platform` and recreate with `docker compose up -d --no-deps cx-platform`. Rebuild the customer portal after portal changes.

`CX_ROLES` selects `all` roles or a subset such as `query,admin,web`, `ingestion,persistence,outbox`, or `ai`. Narrower roles still need their dependencies and configured service addresses.

## Processing

Events enter Kafka and are persisted atomically with outbox records before persisted-event consumers process them. Repeated deliveries are idempotent. Analysis results are versioned. Failed work is retried or sent to a dead-letter topic.

Without a provider key, uncertain stub analysis requires human review. Real transcription requires an audio-capable provider. Generation, decisions, and transcription have separate model settings.

## Data and isolation

The `bestcx` Compose project owns separate volumes and a separate network. Never reuse another installation's database or volumes. These translated migrations are intended for a new database, not for an existing installation's migration history.

`docker compose down` retains data. Removing the volumes permanently deletes this installation's database and recordings. Keep backups outside the repository and verify restores before changing storage or deployment settings.

Local ports bind to loopback. Read SECURITY.md before remote access or production deployment.
