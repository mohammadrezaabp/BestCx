# Event Catalog

The source of the truth of this catalog is in the code: `src/Cx.Contracts/Events/EventNames.cs` And
schema In `src/Cx.Contracts/Schemas/canonical-event.v1.schema.json`.
This document is written for event production teams.

## Naming convention

```text
<domain>.<entity-or-action>.<state>
```

- Only English lowercase letters and `_`
- Between 2 and 4 sections
- **There is no dynamic value in the event name.** `api.request.failed.c_123` It is rejected;
  Customer ID in `customer.customer_id` is placed.

An event that does not have this pattern with reason `malformed_event_name` Rejection and in
`cx.invalid.events.v1` Visible.

## Envelope

```json
{
  "event_id": "01J8YQ6J7W6B0R1S5D9V5J4A3F",
  "schema_version": "1.0",
  "event_name": "api.request.failed",
  "event_category": "api",
  "occurred_at": "2026-09-20T15:11:19.123Z",
  "tenant_id": "bestcx",
  "customer": { "customer_id": "c_123", "business_id": "b_456" },
  "session":  { "session_id": "s_789" },
  "source":   { "type": "backend", "service": "payment-service", "channel": "api", "environment": "production" },
  "product":  { "product_id": "payments", "service_id": "gateway" },
  "interaction": { "action": "api_request", "result": "failed", "duration_ms": 426, "error_code": "AUTH_FAILED" },
  "context":  { "trace_id": "4bf9...", "correlation_id": "corr_123" },
  "privacy":  { "contains_pii": false, "classification": "internal" },
  "metadata": {}
}
```

### Mandatory fields

`event_id` · `schema_version` · `event_name` · `event_category` · `occurred_at` ·
`tenant_id` · `source.type` · `source.service`

### Validation rules

| Status | Behavior |
|---|---|
| Mandatory field missing | Ref → `cx.invalid.events.v1` |
| Edition major Not supported (For example `2.0`) | Ref |
| Edition minor Next (For example `1.3`) | It is accepted |
| Unknown key in `metadata` | It is accepted |
| Credential-like field at any depth | Rejection with `forbidden_field` |
| `event_id` Repetitive | Acceptance idempotent, no new record |
| Event greater than `Ingestion:MaxEventBytes` | Ref |

### Prohibited fields

These keys in **None** A depth of payload are not accepted:

```text
password, passwd, secret, client_secret, api_secret, api_key, apikey,
access_token, refresh_token, id_token, authorization, private_key,
cvv, cvv2, card_number, pan, otp, pin
```

Index in `Ingestion:ForbiddenFieldNames` Adjustable.

### `event_id` And idempotency

The manufacturer must `event_id` Make (ULID It is suggested). retry With the same `event_id` It is safe:
`UNIQUE(event_id)` In the database, it ensures that the second record is not created outbox Do not publish the second.
If the manufacturer `event_id` Do not send, gateway It makes one — But then retry Other idempotent
It is not, because each attempt gets a new ID.

---

## Catalogue

### User / Session — `user_session`

| Event | Release time |
|---|---|
| `user.session.started` | Start the user session |
| `user.session.ended` | The end of the meeting |
| `user.login.succeeded` | Login successful |
| `user.login.failed` | Login failed |

### Product Interaction — `product_interaction`

| Event | Release time |
|---|---|
| `page.viewed` | See the page |
| `button.clicked` | Click on the key element |
| `form.started` | Start filling the form |
| `form.submitted` | Form submission successful |
| `form.failed` | Error in sending the form |
| `product.service.viewed` | View the product page/Service |
| `product.service.activated` | Service activation |

### API — `api`

| Event | Release time |
|---|---|
| `api.request.succeeded` | Request successful (To be sampled) |
| `api.request.failed` | Request failed — **Analysis input AI** |
| `api.timeout` | timeout — **Analysis input AI** |
| `api.rate_limit.hit` | Dealing with rate constraints |

### Feedback — `feedback`

| Event | Fields metadata |
|---|---|
| `feedback.submitted` | `external_id`, `comment` |
| `feedback.csat.submitted` | `external_id`, `csat_score`, `comment` |
| `feedback.nps.submitted` | `external_id`, `nps_score`, `comment` |
| `feedback.rating.submitted` | `external_id`, `rating`, `comment` |

All these events entered the analysis AI And Unified Inbox are.

#### NPS

In the panel, «Submit feedback ← Survey NPS» The question of the possibility of recommending the product/Company to a friend or colleague
Registers with a mandatory score of 0 to 10 and an optional description. API Equivalently, `POST /v1/feedback` is:

```json
{"feedback_id":"survey-response-123","nps_score":0,"channel":"web"}
```

`nps` It is also an accepted alternative name. The score must be an integer JSON be from 0 to 10;
decimal value, string, null and is rejected out of range. For direct event
`feedback.nps.submitted`, field `metadata.nps_score` It is mandatory.
To resend the same answer, same `feedback_id` ; Each new response has a new ID.

`GET /v1/overview?days=7` Field `nps` Includes `score`, `total`, numbers `promoters`,
`passives` And `detractors` and percentages `promoter_percent`, `passive_percent` And
`detractor_percent` .. Marwjan 9–10, passive 7–8 and critics 0–There are 6.
Equivalent formula `100 × (promoters − detractors) / total` ; Passives are included in the denominator.
For example, 6 promoters, 2 passive and 2 critics, score 40+ have.

Only report valid type responses NPS In the current organization and selected time period (`from` including,
`to` Excluded) ; star rating and Hold CSAT They do not enter it.
Each answer is counted independently, even if a customer gives multiple answers. Calculation to analysis AI Not dependent.
The final score is rounded to two decimal places; In the absence of answers, points and percentages `null` are.

### Support — `support`

| Event | Fields metadata |
|---|---|
| `support.ticket.created` | `external_id`, `subject`, `description`, `priority`, `status` |
| `support.ticket.assigned` | `external_id`, `assignee_id` |
| `support.ticket.replied` | `external_id` |
| `support.ticket.escalated` | `external_id` |
| `support.ticket.resolved` | `external_id` |
| `support.ticket.reopened` | `external_id` |

`external_id` For idempotency And connecting to the timeline is necessary.

### Voice — `voice`

| Event | Manufacturer | Block `voice` |
|---|---|---|
| `voice.call.started` | VoIP adapter | `call_id`, `direction`, `agent_id`, `queue_id` |
| `voice.call.answered` | VoIP adapter | `call_id` |
| `voice.call.ended` | VoIP adapter | `call_id`, `duration_ms`, `end_reason` |
| `voice.call.dropped` | VoIP adapter | `call_id`, `end_reason` |
| `voice.recording.ready` | Voice Worker | `call_id`, `recording_id` + `object_key`, `checksum_sha256` In metadata |
| `voice.transcript.ready` | Voice Worker | `call_id` + `transcript_id`, `engine`, `model` In metadata |
| `voice.analysis.completed` | AI Result Persister (`event_category: ai`) | `call_id` + `analysis_id`, `category`, `severity` |

To start ingest Sound, file address in metadata Send:

```json
"metadata": {
  "recording_url": "https://voip.example/recordings/call_9.wav",
  "content_type": "audio/wav",
  "channels": 2,
  "duration_ms": 721000
}
```

The URL host must be the `Voip:BaseUrl` host or listed in `Voip:AllowedRecordingHosts`;
any other host is refused, and so is a redirect to one. Provider credentials are sent on
the first request only.

No `recording_url` None fetchIt is not done; The file can be opened with
`POST /v1/voice/recordings` Upload directly.

**Never** Audio bytes are not included in the event.

---

## Which events analysis AI Do they get?

Orchestrator Only sends events to the model that mean CX have:

```text
feedback.*  ·  support.ticket.created/escalated/replied/resolved
api.request.failed  ·  api.timeout  ·  form.failed  ·  user.login.failed
voice.transcript.ready  (Complete contact analysis)
```

Index in `AiOrchestratorWorker.AnalyzableEvents` is. `page.viewed` Intentionally not analyzed:
It costs money and buries the real signal.
