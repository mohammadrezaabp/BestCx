# Operational panel proposal Customer Experience For Podium

> Edition: 1.0
> Companion to the architecture specification
> Goal: Definition Product/UX and the technical requirements of the team panel CX

---

## 1. Goal

Team CX Shouldn’t be just one Dashboard have statistics. The main requirement is one **CX Operations Console** It is possible to view, review, correct and manage the collected data..

The panel should team CX Help:

- Understand the state of the customer experience.
- Customer 360 . Counts.
- contact, Ticket, Feedback And interaction in one receives Timeline See the unit.
- Play the customer’s voice and Transcript check Check.
- Analysis AI  approve Or override Slow down.
- taxonomy to manage observes.
- Identify incomplete or erroneous data.
- reprocessing to do.
- Events and pipelineitems audit Slow down.

---

## 2. Roles

### CX Analyst
- Data observation
- Search
- filter
- label
- review AI
- Construction segment
- export Limited

### CX Lead
- All access Analyst
- taxonomy management
- bulk review
- reprocess
- dashboard configuration
- saved views

### Support Supervisor
- Observation customer/ticket/call
- Checking the quality of the call
- action item
- follow-up status

### Data/AI Operator
- pipeline health
- failed jobs
- DLQ
- reprocess
- model/taxonomy versions

### Admin
- RBAC
- retention
- integrations
- audit
- data policy

---

## 3. Information Architecture

```text
CX Console
|
+-- Overview
+-- Customers
|   +-- Customer 360
|   +-- Timeline
|   +-- Insights
|
+-- Feedback & Support
|   +-- Unified Inbox
|   +-- Feedback
|   +-- Support Cases
|
+-- Voice
|   +-- Calls
|   +-- Review Queue
|   +-- Transcript Search
|
+-- AI Review
|   +-- Low Confidence
|   +-- Conflicts
|   +-- Overridden
|
+-- Product Experience
|   +-- Products
|   +-- Services
|   +-- API Issues
|
+-- Journeys
+-- Taxonomy
+-- Data Quality
+-- Pipeline Health
+-- Integrations
+-- Audit Log
+-- Settings
```

---

## 4. Page Overview

### KPIitems

- total interactions
- active customers
- feedback count
- support cases
- call count
- negative sentiment share
- unresolved high-severity issues
- top pain points
- top affected products
- AI review backlog
- data-quality warnings

### Trenditems

- CX trend by day/week
- sentiment trend
- CSAT/NPS trend
- support volume
- call volume
- API failure vs negative feedback
- product-specific experience trend

### Alert cards

Example:

```text
Payment Service
Negative feedback +38% vs 7-day baseline
Top category: timeout
Affected customers: 124
```

Alert Should explainable Be and link to underlying events Give.

---

## 5. Customer 360

Header:

```text
Customer
Business
Segment
Products
Current CX Status
Latest Sentiment
Open Support Cases
Last Interaction
```

Tabitems:

### Timeline

All eventin chronological order:

```text
10:01 login succeeded
10:03 product viewed
10:06 api failed
10:07 api failed
10:11 documentation viewed
10:16 support ticket created
10:24 voice call started
10:38 voice call ended
10:41 AI analysis completed
```

Filter:

- channel
- product
- event type
- severity
- date
- support
- voice
- feedback

### Insights

- current sentiment
- dominant intent
- recurring issues
- journey stage
- unresolved issues
- latest AI summary
- human-reviewed labels

### Voice

- calls
- duration
- agent
- reason
- transcript
- resolution
- AI summary

### Support

- open/closed tickets
- SLA fields
- escalation
- latest reply

### Feedback

- rating
- comment
- CSAT
- NPS
- linked product
- linked journey

---

## 6. Unified Feedback & Support Inbox

Team CX He should be able to Feedback And Support in one receives Inbox See common.

Columns:

- timestamp
- customer
- channel
- product
- type
- AI category
- sentiment
- severity
- status
- assignee
- reviewed

### Operation

- assign
- mark reviewed
- add internal note
- change category
- change severity
- merge duplicate issue
- link to product incident
- link to customer journey
- export
- create saved view

### Saved views

For example:

```text
High Severity + Unreviewed
Payment Complaints
Negative Voice Calls
Authentication Issues
VIP Customer Issues
Low Confidence AI
```

---

## 7. Voice Review Workspace

This page is one of the main parts of the product.

### Layout Suggestion

```text
+------------------------------------------------------+
| Call #123 | Customer | Agent | Product | 14:22       |
+------------------------------------------------------+
| Audio waveform / player                              |
| [<< 10s] [Play] [10s >>]  07:14 / 14:22             |
+------------------------------+-----------------------+
| Synced Transcript            | AI Insights           |
| 00:00 Agent: ...             | Sentiment: Negative   |
| 00:08 Customer: ...          | Category: Billing     |
| 00:21 Agent: ...             | Severity: High        |
| ...                          | Resolution: No         |
+------------------------------+-----------------------+
| Human Review / Notes / Labels / Actions              |
+------------------------------------------------------+
```

### Audio Player

- play/pause
- seek
- playback speed
- waveform
- speaker markers
- silence markers
- optional redacted segments
- transcript jump-to-time
- signed URL with short TTL

### Transcript

Each segment:

```json
{
  "speaker": "customer",
  "start_ms": 12340,
  "end_ms": 17720,
  "text": "..."
}
```

### Search

- search Inside transcript
- search All transcriptitems
- filter Based on phrase/category/product/agent/date

---

## 8. Voice AI Insights

The panel should display the following:

- call reason
- customer intent
- primary category
- secondary category
- summary
- resolution status
- follow-up required
- key moments
- mentioned product/service
- issue/entity references
- sentiment from transcript
- confidence
- model version

### Metrics Non-emotional and measurable

- total duration
- hold duration
- silence ratio
- customer talk ratio
- agent talk ratio
- overlap/interruption count
- transfer count
- disconnect/drop
- time to first response

It should not scoring The main inference «Feel the tone of voice» He built. sentiment The original is better than the text and context be extracted and acoustic metrics as observable fact be kept.

---

## 9. AI Review Queue

Any result that has one of the following conditions is entered review queue Be:

- confidence Below threshold
- category conflict
- high severity
- model validation failure repaired
- customer complaint + unresolved
- random QA sample
- policy-triggered review

### Actions

```text
Approve
Override
Reject
Add Label
Add Note
Send for Reprocess
```

Each action Should audit Be:

```text
who
when
previous_value
new_value
reason
```

---

## 10. Taxonomy Management

Team CX Must be without deploy Can taxonomy to manage observes.

### Entityitems

- Category
- Subcategory
- Intent
- Severity Rule
- Journey Stage
- Product Mapping
- Resolution Status
- Contact Reason

### Versioning

Any changes taxonomy Makes a new version:

```text
taxonomy:v12
```

AI Result Always taxonomy version has.

### Rollout

- draft
- review
- active
- retired

---

## 11. Data Quality

Panel Data Quality should show:

- events No customer id
- unknown product
- unknown event name
- schema version Old
- duplicate rate
- missing call recording
- recording No transcript
- transcript With confidence Down
- invalid timestamps
- orphan support ticket
- integration lag
- ingestion gap

Each case should:

```text
count
trend
sample records
owner
status
```

have.

---

## 12. Pipeline Health

For the team Data/AI:

- Kafka lag
- raw ingest rate
- persistence throughput
- outbox lag
- CDC lag
- AI queue depth
- STT jobs
- failed jobs
- DLQ count
- average processing time
- model-provider errors
- object-storage errors

### Operation

- retry single job
- retry selected jobs
- reprocess by date range
- reprocess by customer
- reprocess by model version
- replay topic window With proper permission

---

## 13. Integrations

For each integration:

```text
Name
Type
Status
Last Success
Last Error
Lag
Cursor
Records Today
Auth Expiry
```

Integration types:

- Podium API
- Product Backend
- Web/Mobile SDK
- Support
- CRM
- Feedback
- VoIP
- Data Warehouse

---

## 14. Audit Log

All sensitive operations:

- label override
- delete/anonymize
- transcript edit
- reprocess
- taxonomy publish
- role change
- retention change
- export
- signed audio access

Fields:

```text
actor
action
resource_type
resource_id
timestamp
ip/device if policy allows
before
after
reason
```

---

## 15. Search

Global Search Must be able to search on the following items:

- customer id
- business id
- ticket id
- call id
- event id
- transcript
- feedback text
- support text
- product
- category
- correlation id
- trace id

OpenSearch For text-heavy retrieval It is suitable; Source of Truth Also DB It is original.

---

## 16. Bulk Operations

With guardrail:

- bulk assign
- bulk mark reviewed
- bulk reclassify
- bulk export
- bulk reprocess

Bulk action Should preview And confirmation have and audit Be.

---

## 17. Permissions

Example permissionitems:

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

Voice access should from permission General customer Be separate.

---

## 18. UX Requirements

- responsive desktop-first
- LTR English
- timezone-aware
- English/English search
- keyboard-friendly review flow
- deep links
- shareable saved filters
- pagination/cursor
- no raw JSON requirement For CX user
- raw payload Only for technical role

---

## 19. APIPanels

### Search / List
```http
GET /v1/customers
GET /v1/events
GET /v1/feedback
GET /v1/support/cases
GET /v1/voice/calls
GET /v1/ai/reviews
```

### Detail
```http
GET /v1/customers/{id}
GET /v1/customers/{id}/timeline
GET /v1/voice/calls/{id}
GET /v1/voice/calls/{id}/transcript
```

### Actions
```http
POST /v1/ai/reviews/{id}/approve
POST /v1/ai/reviews/{id}/override
POST /v1/voice/calls/{id}/reprocess
POST /v1/admin/reprocess
POST /v1/taxonomies/{id}/publish
```

---

## 20. Acceptance Criteria

- Customer 360 On one page timeline It shows multi-channel.
- Search Bur customer/ticket/call/event can be done.
- CX Analyst Play the call and transcript synced . See.
- AI label Able approve/override is.
- Changes AI/human audit are.
- taxonomy Able version And publish is.
- Data Quality And Pipeline Health are visible.
- Operation reprocess original data  overwrite It does not.
- audio Only for role Permission is accessible.
- export Able audit is.
