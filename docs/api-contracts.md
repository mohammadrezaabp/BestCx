# API Contracts

Living reference: `http://localhost:18080/api-docs` (OpenAPI).
This document explains the contracts that from schema cannot be deduced.

## Authentication

Two modes, with `Auth:Mode` is selected:

| mode | Heather | Application |
|---|---|---|
| `apikey` | `X-Api-Key: <key>` | Independent deployment, development environment |
| `jwt` | `Authorization: Bearer <token>` | SSO Organization |

Each endpoint One permission It wants to be clear and **backend It applies**; to hide
Door button UI Not a security control.

| Permission | Access |
|---|---|
| `cx.event.ingest` | Sending the event and uploading the audio file |
| `cx.customer.read` | Customer, Timeline, Insights, Search |
| `cx.event.read` | List and details of the event |
| `cx.support.read` | Support tickets |
| `cx.feedback.manage` | Definition/Customer editing, registration of complaints from the panel, preparation of complaint follow-up drafts |
| `cx.voice.listen` | Receive the signed audio link |
| `cx.voice.transcript.read` | Reading the text of the conversation |
| `cx.voice.transcript.edit` | Modification one segment |
| `cx.ai.review` | historical identifier; Ability and API The revisions have been omitted |
| `cx.ai.reprocess` | Reprocessing |
| `cx.taxonomy.manage` | Add, edit and delete category tree topics |
| `cx.pipeline.manage` | Health pipeline, jobFailed, data quality |
| `cx.audit.read` | Audit report |
| `cx.pii.read` | Raw text (Non redacted) And payload Raw event |
| `cx.admin` | All of the above |

`tenant_id` It is taken from the authenticated identity, not from the request body. The manufacturer cannot
In stream Write another tenant.

## Ingestion

### `POST /v1/events`

```http
POST /v1/events
X-Api-Key: <producer key>
Content-Type: application/json
```

Successful response **Always `202 Accepted`** It is, no `201`:

```json
{ "event_id": "01J8...", "status": "accepted" }
```

`202` That is «Zinc Kafka Sit down». API **It does not wait to write to the database** — This is intentional and
It causes slowness CX Never spread to the critical path of the product.

Answer rejected `400` With the reason of the machine reader:

```json
{
  "event_id": "bad-1",
  "status": "rejected",
  "reason": "forbidden_field",
  "errors": ["forbidden field '$.metadata.access_token' must never be sent in an event payload"]
}
```

Possible reasons: `schema_invalid` · `unsupported_schema_version` · `malformed_event_name` ·
`forbidden_field` · `payload_too_large`

### `POST /v1/events/batch`

The body of an array or `{"events": [...]}`. Result **For each item** returns; an event
The failure does not cause the others to be rejected:

```json
{
  "accepted": 19,
  "rejected": 1,
  "results": [{ "event_id": "…", "status": "accepted" }, { "event_id": "…", "status": "rejected", "reason": "schema_invalid" }]
}
```

Maximum size batch: `Ingestion:MaxBatchSize` (Default 500).

### `POST /v1/voice/recordings`

Direct upload of the audio file. The body is the raw audio byte (No multipart):

```http
POST /v1/voice/recordings?callId=call_7781&recordingId=rec_a&durationMs=720000&channels=2&language=fa
Content-Type: audio/wav
```

```json
{
  "recording_id": "f1da95f2-…",
  "object_key": "cx/podium/2026/09/20/voice/call_7781/rec_a.wav",
  "size_bytes": 384044,
  "checksum_sha256": "413217…",
  "storage_state": "READY"
}
```

The contact must already exist (Event `voice.call.*`). File to object storage
stream becomes checksum is taken and read again; Only if successful `READY` Takes.

### Webhookitems

`POST /v1/webhooks/support` · `/feedback` · `/voip` · `/podium`

each payload Source to event canonical Convert to the same path ingestion They leave.

## Customer and feedback (Panel)

| Endpoint | Permission | Explanation |
|---|---|---|
| `POST /v1/customers` | `cx.feedback.manage` | Definition of customer: `display_name` (Mandatory), `phone`, `email`, `business_id`, `segment`, `notes`, `customer_id` (optional; Otherwise `C-XXXXXXXX`). Duplicate ID → `409` |
| `PUT /v1/customers/{id}` | `cx.feedback.manage` | Edit the same fields |
| `POST /v1/feedback` | `cx.feedback.manage` | Textual feedback: `feedback_id` (Key idempotency), `comment` And/Or `rating`, `customer_id`, `product_id`, `channel` |
| `POST /v1/voice/feedback?customerId=&feedbackId=&durationMs=` | `cx.feedback.manage` | Body = Audio file (`audio/wav`, `audio/mpeg`, `audio/mp4`, `audio/ogg`, …). A contact and feedback queue is created; Transcribed text (redacted) After transcription in `comment` is placed |
| `GET /v1/feedback/grid` | `cx.customer.read` | Fund table: `inbox` (`feedback`: No complaints, `complaints`: Complaints only; optional), `q`, `status`, `sentiment`, `severity`, `category`, `kind` (`text`/`voice`/`voc`), `analysis` (`analyzed`/`pending`/`failed`), `customerId`, `page`, `pageSize` ← `{items, total, page, page_size, status_counts}` |
| `POST /v1/feedback/{id}/reanalyze` | `cx.ai.reprocess` | New version of the analysis; For audio without text, it starts with transcription |
| `POST /v1/feedback/{id}/follow-up-task` | `cx.feedback.manage` | Task draft for the technical team based on feedback, analysis and previous customer problems; Priority of analysis intensity; Not saved |

Call/Customer email for roles without `cx.pii.read` The mask returns (`0912***789`).

## Query

### Pagination

Customer lists, calls, audits and history of cursor They use:

```json
{ "items": [...], "next_cursor": "MTc4OTkyNzgyMnwxMjM", "has_more": true }
```

`next_cursor` in the parameter `cursor` Send. cursor is ambiguous (keyset Zinc
`timestamp + tiebreaker`); Do not rely on its content. `offset` Not supported because zinc
Large table is slow and pages are moved when new record is entered. Grade feedback, complaints,
Files and notices from `page` And `pageSize` And the answer `{items, total, page, page_size}` They use.

### The independent range of reports and calls

```http
GET /v1/reports?inbox=complaints&from=&to=&topics=&channels=&severities=&statuses=
GET /v1/reports?inbox=feedback&from=&to=&topics=&channels=&types=&sentiments=
GET /v1/voice/calls?inbox=complaints&cursor=&limit=20
GET /v1/voice/calls?inbox=feedback&cursor=&limit=20
```

`inbox` Only `complaints` Or `feedback` is; Invalid value `400` and the default value `feedback` is.
Range applies before report aggregation, count count, channel options, and call paging..
Registration `feedback_type = "complaint"` It is only in the complaint report; No type or sense filters can change the range.
Indicators `open`, `resolved` And `overdue` Available in summary and report groups; The status and severity of the complaint are read from the related file.
Scores CSAT, CES And NPS Complaints are not counted in the complaints report and CSV It also does not have a score column.
A call connected to at least one complaint is included in complaints only; Contact is independent or connected to feedback in the customer experience.
Field `inbox` Each call in the list and details is returned to redirect the old link to the correct environment.
Cases, common problems, and processing priority count only complaints; Direct details of historical records have been preserved.

### Current account settings

```http
PUT /v1/auth/profile
Content-Type: application/json

{ "display_name": "Display name of the user" }
```

Requires a valid login session; ID, tenant and account are read from the session.
Only the display name of the same user is changed, the change is audited and the session cookie is refreshed..
It is not possible to change the user name, role or profile of other users from this path.
The choice of automatic, light or dark appearance is stored in the browser and is independent of the user’s administrative permission.
### Customer 360

```http
GET /v1/customers?q=&segment=&cursor=&limit=
GET /v1/customers/{id}
GET /v1/customers/{id}/timeline?kinds=event,voice&severity=&from=&to=&cursor=
GET /v1/customers/{id}/insights
```

`timeline` It integrates five sources into one time stream:
`event` · `feedback` · `support` · `voice` · `insight`

### Voice

```http
GET  /v1/voice/calls?inbox=feedback&customerId=&agentId=&direction=&state=&q=&from=&to=
GET  /v1/voice/calls/{callId}
GET  /v1/voice/calls/{callId}/transcript
GET  /v1/voice/calls/{callId}/audio
POST /v1/voice/calls/{callId}/reprocess
PATCH /v1/voice/transcripts/{transcriptId}/segments/{index}
```

`audio` does not return a byte; one **Short-term signed link** gives:

```json
{
  "url": "http://…/cx-voice/…wav?AWSAccessKeyId=…&Expires=…&Signature=…",
  "expires_at": "2026-09-20T18:58:55Z",
  "content_type": "audio/wav",
  "can_download": false
}
```

Every time the link is issued `audit_log` With action `audio.access` is registered.

`transcript` Depending on permission gives a different answer: No `cx.pii.read` Edition redacted
Returns and `"redacted": true` is.

### Analysis history

```http
GET /v1/ai/analyses/{entityType}/{entityId}
```

In need `cx.customer.read` is. page and API Confirmation, rejection and manual rewriting of the analysis are eliminated;
New analyzes are not queued for review. Historical versions and revisions are preserved.
`PATCH /v1/feedback/{id}/review` is also deleted; Feedback does not have a manual review and the complaint handling is done on a case-by-case basis.

### Files and notices

```http
GET  /v1/operations/cases?q=&status=&teamId=&customerId=&mine=&overdue=&page=1&pageSize=20
PUT  /v1/operations/cases/{id}
POST /v1/operations/cases/{id}/workflow
POST /v1/operations/cases/{id}/messages
GET  /v1/operations/notifications/grid?q=&status=unread&page=1&pageSize=20
GET  /v1/operations/notifications/unread-count
POST /v1/operations/notifications/{id}/read
```

Grades `{items, total, page, page_size}` and the counter `{count}` They return.
Search and filter `read`/`unread` Notifications are applied before counting and paging; The counter includes all unread.
The old route `GET /v1/operations/notifications` It remains for consistency.
Notifications are limited to the tenant and the current user, and can be read repeatedly.
Editing an optional field file `comment` It is recorded as an internal note in the same transaction.
Activity `updated` In addition to the historical fields, the array `changes` With `field`, `before` And `after` has.
records the answer of the expert, the team and the title of the stage; The general answer and the internal note are independent of each other.
Action `close` On the way workflow, the file `resolved` to make the final confirmation or reopening.
New file attached to feedback only for `feedback_type = "complaint"` It is allowed; Previous files are preserved.

### Admin

```http
GET  /v1/overview?days=7
GET  /v1/admin/data-quality
GET  /v1/admin/pipelines
GET  /v1/admin/integrations
GET  /v1/admin/jobs/failed
GET  /v1/admin/audit?action=&actorId=&resourceId=&from=&to=
GET    /v1/admin/topics
POST   /v1/admin/topics
PUT    /v1/admin/topics/{topicId}
DELETE /v1/admin/topics/{topicId}
POST   /v1/admin/reprocess
```

#### Topic tree

Operator topics in tree form (Maximum 4 levels) Defines with the title, explanation and sample sentence;
JSON And intent It is not in his business. Any change is immediately applied to the next analysis, `revision`
Moves the tree up one unit (On the results in Fig `taxonomy_version = "rev-N"` is registered) And
In the audit (`topic.create` / `topic.update` / `topic.delete`) is registered.

```json
POST /v1/admin/topics
{
  "parent_id": "…uuid Or null For the main topic…",
  "title": "Payment gateway error",
  "description": "The port gives an error or does not open. Deduction without order is not here.",
  "keywords": ["The port gave an error", "The payment page did not open"],
  "sort_order": 10,
  "is_active": true
}
```

- `code` makes it a server and cannot be edited, because saved analyzes refer to it.
- Deleting a topic that has subtopics `409` gives. Subject used in the analysis instead of deletion
  It is disabled (`{"deleted": false, "deactivated": true}`).
- Disabling a topic excludes its entire subtree from further analysis.
- Analysis result: `category` = Branch root code, `subcategory` = The code of the selected node (If it is deeper than the root).
  If there is no issue `category = "unclassified"` is registered; There is no review queue.

`POST /v1/admin/reprocess` No `"confirm": true` Gives a preview only:

```json
{ "preview": true, "count": 143, "prompt_version": "cx-prompt-v3", "note": "set confirm=true to execute" }
```

Maximum of 500 entities per request.

## Reprocessing Semantics

Reprocessing **Never** It does not overwrite the previous result:

- New row in `ai_analysis` With `supersedes_analysis_id` refers to the previous one.
- Previous row `status = 'superseded'` It takes and remains intact.
- transcript New with `supersedes_transcript_id` is registered.
- Every time in `audit_log` is registered.

## Customer journey map

- `GET /v1/operations/journeys` Returns the current tenant maps; license `cx.work.read` It is necessary.
- `PUT /v1/operations/journeys` With permission `cx.operations.configure` Saves the map. Previous fields `id`, `name`, `window_days`, `enabled` And `stages` have been preserved.
- Map field fields: `persona` (Customer group and its needs), `scenario` (Location and scope of travel), `goal` (The goal of research or improvement) And `map_type` With values `current_state` Or `future_state`.
- Each step `title`, `event_names` And `experience` has. There are steps 2 to 12. `event_names` can be an empty array; The event name is not required for the experience map. In the case of connection, a maximum of 30 names is allowed in each phase and no repetition between phases.
- `experience` Includes `goal`, `actions`, `touchpoints`, `thoughts`, `emotion`, `feeling`, `pain_points`, `opportunities`, `success_metric`, `owner`, `evidence` And `evidence_status` is. The maximum length of each text is 4000 characters.
- `emotion`: One of `unknown`, `negative`, `neutral`, `mixed`, `positive`; The default value `unknown` is. Feelings are not inferred from behavioral events.
- `evidence_status`: Default `hypothesis`; choice `researched` to non-empty text `evidence` It needs. This indicates the registration status of the source, not automatic verification of the research.
- `GET /v1/operations/journeys/{id}/funnel?from=...&to=...` Maintains existing behavioral measurement. The panel provides this additional view only for the current map with event binding at all stages; `window_days` It is related to the same measurement.
- Immigration `009_customer_journey_maps.sql` Adds experience information without removing old trips and events.

## Error codes

| Code | Meaning |
|---|---|
| `202` | Accepted (ingestion; Processing is asynchronous) |
| `400` | Invalid event or body — Reason in response |
| `401` | Key/Missing or invalid token |
| `403` | Authenticated but permission It is not necessary |
| `404` | Not found, or belongs to another tenant |
| `409` | Inconsistent situation (For example, the audio file still `READY` It is not) |
| `429` | Crossing the rate limit; heather `Retry-After` |
| `500` | Unexpected error |

## Health

```http
GET /health        # Prescription and active roles
GET /health/live   # Process liveness, without touching dependencies
GET /health/ready  # PostgreSQL Mandatory; Kafka And object storage degraded are
```

`ready` When PostgreSQL Not available `Unhealthy` is. Absence Kafka Or object storage
`Degraded` It is reported, because API It can still read from SoT Answer.
