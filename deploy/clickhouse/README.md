# ClickHouse (Phase analytics)

ClickHouse In MVP It is not mandatory. Source of Truth Operational PostgreSQL and panel queries
Answers are given directly from it.

When the volume of the event exceeds that PostgreSQL For aggregate He answers easily, passed.
`cx.persisted.events.v1` with one consumer To ClickHouse Pour and only queries
Analytical panel (Overview And Journey) Move to it.

```sql
CREATE TABLE cx_event_fact
(
    event_id       String,
    tenant_id      LowCardinality(String),
    customer_id    String,
    event_name     LowCardinality(String),
    event_category LowCardinality(String),
    product_id     LowCardinality(String),
    source_type    LowCardinality(String),
    occurred_at    DateTime64(3, 'UTC'),
    received_at    DateTime64(3, 'UTC'),
    result         LowCardinality(String),
    error_code     LowCardinality(String)
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(occurred_at)
ORDER BY (tenant_id, occurred_at, customer_id)
TTL toDateTime(occurred_at) + INTERVAL 24 MONTH;
```

Note: ClickHouse Copy is analytical, not a source of truth. Delete/Anonymization of the customer must be done in both
apply warehouse; This in workflow Include deletion.
