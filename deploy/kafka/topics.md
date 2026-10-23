# Kafka topics

Platform in the development environment topicIt automates the (`Kafka:AutoCreateTopics=true`).
In the environment production This option is turned off and the platform team topicwith the following settings.

| Topic | Key | Producer | Consumer | Retention Suggestion | Partition |
|---|---|---|---|---|---|
| `cx.raw.events.v1` | `tenant:customer` | Ingestion API, Voice Worker | Persistence Worker | 7 Day | 12 |
| `cx.persisted.events.v1` | `tenant:customer` | Debezium / Outbox Relay | AI Orchestrator, Voice Projection, Inbox Projection | 30 Day | 12 |
| `cx.voice.recording.ready.v1` | `call_id` | Voice Worker | Ops/The future | 7 Day | 6 |
| `cx.voice.transcription.request.v1` | `call_id` | AI Orchestrator, Query API (reprocess) | Transcription Worker | 3 Day | 6 |
| `cx.voice.transcription.result.v1` | `call_id` | Transcription Worker | Ops/The future | 3 Day | 6 |
| `cx.ai.analysis.request.v1` | `entity_id` | AI Orchestrator, Admin API | AI Worker | 3 Day | 12 |
| `cx.ai.analysis.result.v1` | `entity_id` | AI Worker | AI Result Persister | 7 Day | 12 |
| `cx.customer.timeline.updated.v1` | `tenant:customer` | AI Result Persister | Panel/CRM | 1 Day | 6 |
| `cx.invalid.events.v1` | `event_id` | Ingestion API | Ops | 30 Day | 3 |
| `cx.persistence.dlq.v1` | `event_id` | Persistence Worker | Ops | 30 Day | 3 |
| `cx.ai.dlq.v1` | `entity_id` | AI Worker, Orchestrator | Ops | 30 Day | 3 |
| `cx.voice.dlq.v1` | `call_id` | Voice Workers | Ops | 30 Day | 3 |

## Handcrafted

```bash
for topic in cx.raw.events.v1 cx.persisted.events.v1 cx.ai.analysis.request.v1 cx.ai.analysis.result.v1; do
  kafka-topics --bootstrap-server "$BROKERS" --create --if-not-exists \
    --topic "$topic" --partitions 12 --replication-factor 3 \
    --config min.insync.replicas=2 --config retention.ms=2592000000
done

for topic in cx.invalid.events.v1 cx.persistence.dlq.v1 cx.ai.dlq.v1 cx.voice.dlq.v1; do
  kafka-topics --bootstrap-server "$BROKERS" --create --if-not-exists \
    --topic "$topic" --partitions 3 --replication-factor 3 \
    --config min.insync.replicas=2 --config retention.ms=2592000000
done
```

## Notes

- **Partition key** For customer events `tenant_id:customer_id` is to order the events of one
  Jupiter in a partition To be maintained. Count partition can be increased later, but this work Send
  mixes the order of existing keys; Be generous from the start.
- **Producer** With `enable.idempotence=true` And `acks=all` It works.
- **Consumer** offset only after success commit It does; delivery Of the type at-least-once is
  And all handleritems idempotent are.
- Name topicin configuration Can be changed (`Kafka:Topics:*`) To with convention Available
  Coordinate the organization.

## Consumers (consumer group)

| Group | Topic | role |
|---|---|---|
| `cx-persistence-worker` | `cx.raw.events.v1` | Write in PostgreSQL + outbox |
| `cx-voice-projection` | `cx.persisted.events.v1` | Construction `voice_call` And start ingest Audio |
| `cx-inbox-projection` | `cx.persisted.events.v1` | Construction `feedback` And `support_case` |
| `cx-ai-orchestrator` | `cx.persisted.events.v1` | Path analysis |
| `cx-voice-transcription` | `cx.voice.transcription.request.v1` | Transcription |
| `cx-ai-worker` | `cx.ai.analysis.request.v1` | Calling the model |
| `cx-ai-result-persister` | `cx.ai.analysis.result.v1` | Save the analysis version + Review queue |
