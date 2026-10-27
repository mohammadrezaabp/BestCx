# BestCx

An English, left-to-right customer experience platform with an operator console, customer portal, complaint case management, surveys, customer journey maps, voice transcription, and optional AI analysis.

BestCx starts with an empty workspace. This repository contains source code, schema migrations, and optional synthetic fixtures. No customer database, provider credentials, or previous Git history is included.

## Run locally

Install Docker with Compose and Python 3.10 or newer. On Windows, start Docker Desktop in Linux container mode.

```sh
git clone https://github.com/mohammadrezaabp/BestCx.git
cd BestCx
python scripts/setup.py
docker compose up -d --build
```

Setup creates a private, ignored `.env` with unique random administrator, database, and object storage credentials. It refuses to overwrite an existing file and never prints credentials. Open your local `.env` to read `CX_ADMIN_PASSWORD`; sign in as `admin`.

| Service | Local address |
| --- | --- |
| Operator console | http://localhost:18080 |
| Customer portal | http://localhost:18081 |
| API reference | http://localhost:18080/api-docs |
| Readiness check | http://localhost:18080/health/ready |
| Object storage console | http://localhost:29001 |

Ports, Compose resources, images, volumes, and network are separate from other installations. All published ports bind to loopback. First startup applies migrations automatically; infrastructure may need a few minutes to become healthy.

Manage customers and create their portal accounts from customer profiles in the operator console. There is no default customer password or public registration. Demo data is disabled.

```sh
docker compose ps
docker compose logs --tail 100 cx-platform
docker compose down
```

Stopping with `down` keeps database and recording volumes. Adding the `--volumes` option permanently deletes this installation's data.

## Optional AI and voice providers

The application builds and runs without external provider keys. Without an OpenRouter key, deterministic stub analysis is marked for review and real audio transcription is unavailable.

Set optional provider credentials only in your ignored `.env`:

- `OPENROUTER_API_KEY`: text generation, decision models, and audio-capable transcription.
- `DEEPGRAM_API_KEY`: optional Deepgram transcription; also set `CX_STT_ENGINE=deepgram` and `CX_STT_MODEL=nova-3`.
- Text generation, decisions, and transcription use independent model settings in `.env.example`. Model availability depends on your provider account.

After changing configuration, run `docker compose up -d cx-platform`. A restart alone does not apply new environment values. AI summaries, suggested actions, decision reasons, and engineering tasks are requested in English. Audio is transcribed faithfully in the language spoken.

## Product behavior

- `/complaints` contains records whose `feedback_type` is `complaint`, with case handling, shared problems, follow-up customers, service outcomes, and case settings.
- `/experience` contains feedback, ratings, surveys, campaigns, journey maps, and independent calls.
- The API applies workspace scope before pagination, counts, and report aggregation. Negative sentiment does not change the record type.
- Complaint views omit satisfaction ratings, CSAT, CES, and NPS. Feedback shows only submitted scores.
- Journey maps distinguish current and future states, research evidence and hypotheses. Event funnels are optional.
- Dates use Gregorian calendars, English digits, and UTC report boundaries. New business calendars use Monday-Friday; policies can configure their own timezone and working days.

## Architecture and development

| Directory | Responsibility |
| --- | --- |
| `src/Cx.Host` | Single application host for APIs and workers |
| `src/Cx.Application` | Application logic and contracts |
| `src/Cx.Infrastructure` | PostgreSQL, Kafka, storage, providers, authentication |
| `src/Cx.Query.Api` | Read APIs and server-side workspace filtering |
| `src/Cx.Web` | React/TypeScript operator console |
| `src/Cx.CustomerPortal` | React customer portal and same-origin proxy |
| `deploy/postgres/migrations` | Schema and English taxonomy migrations |
| `tests` | .NET unit, contract, integration, and end-to-end tests |

The write pipeline persists events and outbox entries atomically before publishing persisted-event notifications. AI consumes persisted data, redacts personal data, validates structured output, and stores versioned results.

For host development, install the .NET SDK in `global.json` and Node.js 22.12 or newer. Use public package registries.

```sh
dotnet test BestCx.sln
cd src/Cx.Web
npm ci
npm run build
npx playwright install chromium
npm test
```

The customer portal has the same npm build and test commands. Database-dependent tests use disposable PostgreSQL containers. Never point tests at a production database.

CI also builds the Docker images and runs every end-to-end test against PostgreSQL, Kafka, and object storage. Reproduce that check with `python scripts/ci-stack.py` on a machine with Docker and the .NET SDK. It requires a fresh workspace with no `.env` or existing BestCx Compose resources, generates temporary credentials, and removes its containers and volumes afterward. CI fails if any test is skipped. MinIO is built from its pinned original source because its community container images are no longer publicly distributed.

After application code changes:

```sh
docker compose build cx-platform
docker compose up -d --no-deps cx-platform
```

Read the [API contracts](docs/api-contracts.md), [runbook](docs/runbook.md), and [security guidance](SECURITY.md). The included Compose stack is for local development. Production requires your own TLS, network, identity, backup, and secret management configuration.
