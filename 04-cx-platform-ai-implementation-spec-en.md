# AI-Friendly Implementation Specification — BestCx

> Implementation specification for coding agents.
> Goal: The coding agent can without interpreting the architecture from zero, phase MVP to implement in a specific order.

---

# 0. SYSTEM GOAL

Build an event-driven Customer Experience platform with:

1. atomic/reliable CX event persistence
2. Kafka-based asynchronous processing
3. Transactional Outbox
4. persisted-event-only AI processing
5. Customer 360 timeline
6. Feedback and Support ingestion
7. VoIP call + recording ingestion
8. Speech-to-Text pipeline
9. versioned AI analysis
10. CX Operations Panel APIs
11. Human review
12. auditability

DO NOT place CX processing in the critical path of product operations.

---

# 1. HARD ARCHITECTURAL RULES

The coding agent MUST follow these rules.

## R1 — Two top-level pipelines

```text
PIPELINE_A = CAPTURE -> KAFKA_RAW -> PERSIST -> OUTBOX -> KAFKA_PERSISTED

PIPELINE_B = KAFKA_PERSISTED -> CONTEXT -> AI/STT -> RESULT_PERSIST -> REVIEW/INDEX
```

## R2 — AI cannot consume raw events

AI MUST consume only `cx.persisted.events.v1` or downstream persisted voice-ready events.

## R3 — Source of Truth

PostgreSQL is the operational Source of Truth.

## R4 — Kafka delivery model

Assume at-least-once delivery.

All consumers MUST be idempotent.

## R5 — Audio storage

Never put raw audio bytes in Kafka or PostgreSQL.

Use Object Storage.

## R6 — Immutable facts

Do not mutate original `cx_event.payload`.

AI result and human review are separate versioned records.

## R7 — No Podium endpoint assumptions

`podium.ir/docs` was not reliably accessible when this spec was created.

Implement `IPodiumConnector` abstraction and leave concrete endpoint mapping in a configuration/integration module.

---

# 2. RECOMMENDED STACK

Use existing organization standards if they already exist. Otherwise default:

```yaml
backend: ASP.NET Core 8 or later
database: PostgreSQL
message_bus: Apache Kafka
cdc: Debezium
analytics: ClickHouse
search: OpenSearch
object_storage: S3-compatible
observability: OpenTelemetry
frontend: React + TypeScript
api_style: REST
schema: JSON Schema
auth: organization SSO/JWT
```

If the repository is .NET, prefer:

```text
Confluent.Kafka
Npgsql / EF Core or Dapper
FluentValidation
OpenTelemetry
Polly
```

---

# 3. REPOSITORY STRUCTURE

Target modular structure:

```text
/src
  /Cx.Contracts
  /Cx.Domain
  /Cx.Application
  /Cx.Infrastructure
  /Cx.Ingestion.Api
  /Cx.Query.Api
  /Cx.Persistence.Worker
  /Cx.Ai.Orchestrator
  /Cx.Ai.Worker
  /Cx.Voice.Worker
  /Cx.Integrations.Podium
  /Cx.Integrations.Support
  /Cx.Integrations.Voip
  /Cx.Admin.Api
  /Cx.Web

/tests
  /Cx.UnitTests
  /Cx.IntegrationTests
  /Cx.ContractTests
  /Cx.EndToEndTests

/deploy
  /kafka
  /debezium
  /postgres
  /clickhouse
  /opensearch
  /observability

/docs
  event-catalog.md
  api-contracts.md
  runbook.md
```

A monorepo or existing repo layout is acceptable, but preserve module boundaries.

---

# 4. DOMAIN ENTITIES

Implement at minimum:

```text
CxEvent
OutboxEvent
CustomerIdentity
Feedback
SupportCase
VoiceCall
VoiceRecording
VoiceTranscript
VoiceTranscriptSegment
AiAnalysis
HumanReview
Taxonomy
TaxonomyVersion
AuditLog
IntegrationCheckpoint
ProcessingJob
```

---

# 5. DATABASE MIGRATION — REQUIRED TABLES

Create migrations for:

```text
cx_event
cx_outbox
customer_identity
feedback
support_case
voice_call
voice_recording
voice_transcript
voice_transcript_segment
ai_analysis
human_review
taxonomy
taxonomy_version
audit_log
integration_checkpoint
processing_job
```

Minimum constraints:

```sql
ALTER TABLE cx_event
ADD CONSTRAINT uq_cx_event_event_id UNIQUE (event_id);
```

```sql
ALTER TABLE voice_call
ADD CONSTRAINT uq_voice_call UNIQUE (tenant_id, call_id);
```

```sql
ALTER TABLE processing_job
ADD CONSTRAINT uq_processing_job_key UNIQUE (job_key);
```

Indexes:

```text
cx_event(tenant_id, customer_id, occurred_at desc)
cx_event(event_name, occurred_at desc)
cx_event(correlation_id)
voice_call(tenant_id, customer_id, started_at desc)
ai_analysis(entity_type, entity_id, created_at desc)
human_review(status, created_at)
```

---

# 6. CANONICAL EVENT CONTRACT

Create a versioned JSON schema.

Required fields:

```yaml
event_id: string
schema_version: string
event_name: string
event_category: string
occurred_at: ISO-8601 UTC
received_at: ISO-8601 UTC
tenant_id: string
source.type: string
source.service: string
metadata: object
```

Optional but standard:

```yaml
customer.customer_id: string
customer.anonymous_id: string
customer.business_id: string
session.session_id: string
product.product_id: string
product.service_id: string
context.trace_id: string
context.span_id: string
context.request_id: string
context.correlation_id: string
privacy.contains_pii: boolean
privacy.classification: string
```

Validation behavior:

```text
unknown non-required metadata fields -> allowed
missing required field -> reject to invalid stream
unsupported major schema version -> reject
duplicate event_id -> accept as idempotent no-op after persistence stage
```

---

# 7. EVENT CATALOG — INITIAL

Implement constants/enums or generated contracts for:

```text
user.session.started
user.session.ended
user.login.succeeded
user.login.failed

page.viewed
button.clicked
form.started
form.submitted
form.failed

product.service.viewed
product.service.activated

api.request.succeeded
api.request.failed
api.timeout
api.rate_limit.hit

feedback.submitted
feedback.csat.submitted
feedback.nps.submitted
feedback.rating.submitted

support.ticket.created
support.ticket.assigned
support.ticket.replied
support.ticket.escalated
support.ticket.resolved
support.ticket.reopened

voice.call.started
voice.call.answered
voice.call.ended
voice.call.dropped
voice.recording.ready
voice.transcript.ready
voice.analysis.completed
```

Do NOT put IDs inside event names.

---

# 8. KAFKA TOPICS

Create or document:

```yaml
cx.raw.events.v1:
  key: tenant_id:customer_id
  consumers:
    - persistence-worker

cx.persisted.events.v1:
  key: tenant_id:customer_id
  producers:
    - debezium-outbox
  consumers:
    - ai-orchestrator
    - analytics-worker

cx.voice.recording.ready.v1:
  key: call_id
  consumers:
    - voice-worker

cx.ai.analysis.request.v1:
  key: entity_id
  consumers:
    - ai-worker

cx.ai.analysis.result.v1:
  key: entity_id
  consumers:
    - ai-result-persister

cx.invalid.events.v1:
  key: event_id

cx.persistence.dlq.v1:
  key: event_id

cx.ai.dlq.v1:
  key: entity_id
```

Configuration starting points MUST remain environment-configurable.

---

# 9. INGESTION API

Implement:

```http
POST /v1/events
Content-Type: application/json
```

Success:

```http
202 Accepted
```

Response:

```json
{
  "event_id": "01J...",
  "status": "accepted"
}
```

Also:

```http
POST /v1/events/batch
```

Batch behavior:

- max batch size configurable
- per-item validation result
- preserve original event_id
- return 202 when accepted to Kafka
- do not wait for DB persistence

Authentication is mandatory.

Rate limiting is mandatory.

---

# 10. PERSISTENCE WORKER

Algorithm:

```text
consume raw event
validate supported schema
normalize timestamps
derive partition/customer metadata
BEGIN DB TX
  INSERT cx_event
    if duplicate event_id:
      treat as idempotent success
  INSERT cx_outbox
COMMIT
commit Kafka offset
```

Important:

If `cx_event` already exists due to retry:

- do NOT create duplicate outbox publication blindly.
- detect original committed record.
- return success.
- ensure persisted publication is eventually guaranteed through outbox state.

Use a deterministic outbox id where useful:

```text
UUIDv5("persisted:" + event_id)
```

or equivalent uniqueness.

---

# 11. OUTBOX

Outbox payload MUST include:

```json
{
  "event_id": "...",
  "tenant_id": "...",
  "customer_id": "...",
  "event_name": "...",
  "occurred_at": "...",
  "persisted_at": "...",
  "source_record_id": 123
}
```

Debezium publishes to:

```text
cx.persisted.events.v1
```

Partition key:

```text
tenant_id:customer_id
```

---

# 12. FRONTEND TRACKING SDK

Provide minimal JS/TS client:

```ts
cx.track("page.viewed", {
  productId: "x",
  path: "/service/x"
});
```

Requirements:

- non-blocking
- batch support
- retry with bounded queue
- page unload strategy
- anonymous_id support
- session_id support
- no secret collection
- configurable allowlist for metadata
- sampling for high-volume low-value events

DO NOT block user navigation on telemetry delivery.

---

# 13. SUPPORT / FEEDBACK ADAPTERS

Implement adapters that transform source payload into Canonical Event.

Interface example:

```text
IExternalCxAdapter
  Parse(input)
  Validate()
  MapToCanonicalEvent()
  GetCheckpoint()
```

Store provider/source external ID for idempotency.

---

# 14. PODIUM ADAPTER

Create:

```text
IPodiumConnector
```

Capabilities:

```text
HealthCheck
PullChanges(cursor)
HandleWebhook(payload)
ResolveCustomer(sourceRef)
MapProduct(sourceRef)
```

Do not hard-code undocumented Podium routes.

All actual routes, auth method and mappings MUST be added after verifying `podium.ir/docs`.

---

# 15. VOICE INGESTION

Implement:

```text
IVoipConnector
```

Capabilities:

```text
HandleCallStarted
HandleCallEnded
HandleRecordingReady
FetchCall
FetchRecording
```

When recording is available:

```text
1. create/update VoiceCall
2. set VoiceRecording state=STAGING
3. stream recording to Object Storage
4. calculate SHA-256 checksum
5. verify size/media metadata
6. set state=READY
7. create persisted/outbox event voice.recording.ready
```

Do not load large recordings fully into application memory.

Use streaming.

---

# 16. VOICE OBJECT KEY

Use deterministic path:

```text
cx/{tenant}/{yyyy}/{mm}/{dd}/voice/{call_id}/{recording_id}.{ext}
```

Do not include customer PII in object key.

---

# 17. TRANSCRIPTION JOB

Job key:

```text
{recording_id}:transcribe:{model_version}
```

State:

```text
PENDING
RUNNING
SUCCEEDED
FAILED_RETRYABLE
FAILED_FINAL
```

Output must include segment timestamps.

Preferred segment contract:

```json
{
  "index": 0,
  "speaker": "customer",
  "start_ms": 1230,
  "end_ms": 5420,
  "text": "...",
  "confidence": 0.91
}
```

---

# 18. TRANSCRIPT STORAGE

Store:

1. normalized text in DB for fast query/detail
2. full structured transcript artifact in Object Storage if large
3. searchable copy in OpenSearch

Never overwrite prior transcript version.

---

# 19. PII REDACTION

Implement redaction stage before generic LLM analysis.

Input:

```text
raw transcript
```

Outputs:

```text
redacted transcript
redaction map (restricted access)
```

Redaction policy is configuration-driven.

Never send access tokens, secrets or credential-like values to LLM.

---

# 20. AI ORCHESTRATOR

Consumes persisted events.

Router:

```text
if event_category == voice and recording READY:
    ensure transcript exists
    build voice context
    enqueue voice analysis
else:
    build event/customer context
    enqueue standard analysis
```

Context window policy must be configuration-driven.

Starting policy:

```text
current event
+ previous 20 customer events
+ last 30 minutes
+ active support case
+ latest feedback
```

---

# 21. AI RESULT CONTRACT

AI MUST return strict JSON.

Schema:

```json
{
  "sentiment": "positive|neutral|negative|mixed|unknown",
  "intent": "string",
  "category": "string",
  "subcategory": "string|null",
  "severity": "low|medium|high|critical",
  "journey_stage": "string|null",
  "resolution_status": "resolved|partially_resolved|unresolved|unknown",
  "summary": "string",
  "action_items": ["string"],
  "mentioned_products": ["string"],
  "confidence": 0.0
}
```

Voice extension:

```json
{
  "call_reason": "string",
  "follow_up_required": true,
  "key_moments": [
    {
      "start_ms": 0,
      "end_ms": 0,
      "type": "problem_statement|complaint|resolution|commitment|escalation|other",
      "summary": "string"
    }
  ]
}
```

Validate model output with JSON schema before persistence.

---

# 22. AI VERSIONING

Persist:

```text
model_provider
model_name
model_version
prompt_version
taxonomy_version
input_hash
confidence
created_at
supersedes_analysis_id
```

Never update prior AI result in place.

---

# 23. HUMAN REVIEW

Statuses:

```text
PENDING
APPROVED
OVERRIDDEN
REJECTED
```

Create review item when:

```text
confidence < configured_threshold
OR severity == critical
OR policy requires review
OR AI category not in active taxonomy
OR random QA sampling selects item
```

Store:

```text
reviewer_id
decision
previous_result
override_result
reason
reviewed_at
```

Write AuditLog.

---

# 24. QUERY API

Implement cursor-based pagination.

## Customer

```http
GET /v1/customers/{id}
GET /v1/customers/{id}/timeline
GET /v1/customers/{id}/insights
```

Timeline response must merge:

```text
CX events
feedback
support
voice calls
AI insights
human-reviewed labels
```

Sort by `occurred_at`.

## Voice

```http
GET /v1/voice/calls
GET /v1/voice/calls/{id}
GET /v1/voice/calls/{id}/transcript
POST /v1/voice/calls/{id}/reprocess
```

Audio endpoint returns short-lived signed URL, not raw bytes through Query API unless organization policy explicitly requires proxy streaming.

---

# 25. CX PANEL — MVP SCREENS

Implement backend contracts first, then UI.

Required screens:

```text
1. Overview
2. Customer Search
3. Customer 360
4. Unified Feedback/Support Inbox
5. Voice Calls
6. Voice Call Review
7. AI Review Queue
8. Taxonomy
9. Data Quality
10. Pipeline Health
11. Audit Log
```

---

# 26. VOICE REVIEW UI

Must provide:

```text
audio player
waveform if available
play/pause
seek
playback speed
synced transcript
speaker labels
click transcript -> seek audio
AI summary
AI labels
confidence
key moments
human override
notes
reprocess
```

Permission check for audio access is mandatory.

---

# 27. RBAC

Define explicit permission constants:

```text
cx.customer.read
cx.event.read
cx.feedback.manage
cx.support.read
cx.voice.listen
cx.voice.transcript.read
cx.voice.transcript.edit
cx.ai.review
cx.ai.reprocess
cx.taxonomy.manage
cx.pipeline.manage
cx.audit.read
cx.export
cx.pii.read
cx.admin
```

Backend MUST enforce permissions; frontend hiding is not sufficient.

---

# 28. AUDIT

Audit at minimum:

```text
AI override
transcript edit
audio access
export
reprocess
taxonomy publish
retention change
role/permission change
identity merge
delete/anonymize
```

Audit record:

```json
{
  "actor_id": "...",
  "action": "...",
  "resource_type": "...",
  "resource_id": "...",
  "occurred_at": "...",
  "before": {},
  "after": {},
  "reason": "..."
}
```

---

# 29. DATA QUALITY JOBS

Implement checks for:

```text
missing customer id
unknown product
unknown event name
unsupported schema version
duplicate rate spike
missing recording
recording not transcribed
transcript confidence below threshold
invalid event timestamp
orphan support case
integration lag
```

Expose summarized result via Admin API.

---

# 30. OBSERVABILITY

Instrument all services with OpenTelemetry.

Required correlation:

```text
event_id
trace_id
request_id
correlation_id
customer_id
session_id
call_id
job_id
```

Required metrics:

```text
cx_ingest_total
cx_ingest_invalid_total
cx_ingest_latency
cx_persistence_total
cx_persistence_failures
cx_duplicate_total
cx_outbox_lag
cx_kafka_consumer_lag
cx_ai_jobs_total
cx_ai_failures_total
cx_ai_latency
cx_stt_jobs_total
cx_stt_failures_total
cx_stt_latency
cx_human_review_backlog
```

Never use customer PII as high-cardinality metric labels.

---

# 31. ERROR HANDLING

Use retry only for retryable errors.

Suggested categories:

```text
VALIDATION
AUTH
RATE_LIMIT
TRANSIENT_NETWORK
TRANSIENT_DB
PROVIDER_5XX
PERMANENT_PROVIDER
SCHEMA
AI_OUTPUT_INVALID
OBJECT_CORRUPT
```

Use exponential backoff + jitter.

Use circuit breaker for external AI/STT/provider calls.

Final failure -> appropriate DLQ + observable alert.

---

# 32. TEST STRATEGY

## Unit Tests

Must cover:

```text
event validation
event normalization
idempotency
taxonomy validation
AI JSON validation
voice state transitions
permission checks
redaction rules
```

## Integration Tests

Use real disposable dependencies where possible:

```text
PostgreSQL
Kafka
Object Storage emulator/compatible service
```

Test:

```text
raw event -> persisted DB
persisted event -> outbox
outbox -> Kafka persisted
duplicate delivery
consumer restart
DB rollback
Kafka retry
recording upload
recording checksum failure
transcript persistence
AI result versioning
```

## Contract Tests

For every external adapter.

## End-to-End

Scenario:

```text
customer interaction
-> event accepted
-> event persisted
-> AI analyzed
-> customer timeline updated
-> visible in panel
```

Voice scenario:

```text
call ended
-> recording stored
-> ready event
-> transcript
-> AI analysis
-> human review
-> Customer 360
```

---

# 33. MIGRATION / ROLLOUT PLAN

Implement in this exact order unless repository constraints require adjustment.

## Milestone 1 — Foundation

- contracts
- DB migrations
- Kafka infrastructure
- ingestion API
- persistence worker
- outbox
- Debezium
- basic observability

Definition of Done:

```text
POST event -> Kafka raw -> DB -> Outbox -> Kafka persisted
```

## Milestone 2 — Query + Customer 360

- query API
- timeline
- basic panel
- search

## Milestone 3 — Feedback / Support

- adapters
- unified inbox
- correlation

## Milestone 4 — Voice Storage

- VoIP adapter interface
- call model
- recording streaming
- object storage
- voice panel playback

## Milestone 5 — STT

- transcription job
- transcript segments
- synced transcript UI
- search

## Milestone 6 — AI

- orchestrator
- structured output
- AI result store
- review queue
- human override

## Milestone 7 — Operations

- taxonomy
- data quality
- pipeline health
- audit
- reprocess

---

# 34. NON-GOALS FOR MVP

Do NOT implement before core flow is stable:

```text
real-time live-call coaching
automatic agent performance ranking
emotion recognition from raw voice
predictive churn model
fully automated customer remediation
complex graph database
custom ML training pipeline
```

These can be later phases.

---

# 35. SECURITY CHECKLIST

Before production:

```text
[ ] TLS enabled
[ ] Kafka auth enabled
[ ] DB encryption configured
[ ] Object Storage private
[ ] signed URLs short-lived
[ ] RBAC backend enforced
[ ] PII fields classified
[ ] secrets removed from events
[ ] transcript raw access restricted
[ ] audio access audited
[ ] retention configured
[ ] deletion/anonymization workflow tested
[ ] AI provider data policy reviewed
[ ] backup/restore tested
```

---

# 36. DEFINITION OF DONE — SYSTEM LEVEL

The MVP is complete only when all are true:

```text
[ ] Every accepted event has an event_id.
[ ] Duplicate delivery does not duplicate business state.
[ ] Persist + outbox is atomic.
[ ] AI never reads cx.raw.events.v1.
[ ] Persisted event can be replayed.
[ ] Customer timeline merges multiple channels.
[ ] Feedback and support are linked to customer/product where possible.
[ ] Voice recording is stored outside Kafka/DB.
[ ] Voice READY state requires verified stored object.
[ ] Transcript has timestamps and speakers.
[ ] AI results are versioned.
[ ] Low-confidence AI can enter human review.
[ ] Human overrides are audited.
[ ] CX panel can search customer/event/ticket/call.
[ ] Authorized user can play call and view synced transcript.
[ ] Reprocess creates a new version.
[ ] Pipeline health and DLQ are observable.
```

---

# 37. CODING AGENT EXECUTION INSTRUCTIONS

When implementing:

1. Inspect existing repository conventions first.
2. Reuse existing auth, logging, configuration and deployment standards.
3. Do not replace existing infrastructure without a concrete incompatibility.
4. Create migrations before repository code that depends on them.
5. Add tests in the same PR/change set as each module.
6. Keep all contracts versioned.
7. Do not hard-code Kafka broker addresses, topic retention or provider credentials.
8. Do not hard-code Podium endpoints until verified from official `podium.ir/docs`.
9. Keep external adapters behind interfaces.
10. Implement idempotency before adding throughput optimizations.
11. Prefer streaming for voice files.
12. Never log secrets, raw tokens or full sensitive payloads.
13. Store timestamps in UTC.
14. Use cursor pagination for large lists.
15. Use structured logs.
16. Add OpenTelemetry spans/metrics to every worker.
17. Add a runbook entry for every DLQ.
18. Every manual panel action that changes classification/state must be audited.

---

# 38. FIRST IMPLEMENTATION TASK FOR THE AGENT

Start with a vertical slice.

Build:

```text
POST /v1/events
   -> cx.raw.events.v1
   -> Persistence Worker
   -> PostgreSQL cx_event + cx_outbox
   -> Debezium
   -> cx.persisted.events.v1
   -> test consumer
```

Add:

- schema validation
- event_id idempotency
- integration tests
- OpenTelemetry
- DLQ
- health endpoints

Do not implement AI before this slice passes end-to-end tests.

---

# 39. SECOND IMPLEMENTATION TASK

Build Customer 360 read model:

```text
persisted event
-> timeline projector
-> customer timeline query
-> GET /v1/customers/{id}/timeline
```

Then create minimal panel:

```text
Customer Search
Customer 360
Timeline
```

---

# 40. THIRD IMPLEMENTATION TASK

Build Voice vertical slice:

```text
voice.call.ended
-> call stored
-> recording streamed to Object Storage
-> checksum verified
-> voice.recording.ready
-> transcript worker stub
-> Voice Call Review UI
```

After this is stable, connect real STT and AI providers.

---

# 41. OPEN ITEMS TO RESOLVE WITH TEAM

Before production, team must explicitly decide:

```text
1. exact Podium API/webhook mappings
2. existing Kafka ownership and naming convention
3. PostgreSQL cluster ownership
4. Object Storage provider
5. STT provider/model
6. LLM provider/model
7. retention periods
8. consent/legal policy for call recording
9. customer identity resolution rules
10. tenant model
11. expected peak events/sec
12. expected calls/day
13. average/max recording duration
14. required data residency
15. SSO/RBAC source
16. ClickHouse/OpenSearch availability
17. production SLOs
```

These are configuration/design decisions, not reasons to block the Foundation vertical slice.
