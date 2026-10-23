# OpenSearch (Text search)

In MVP, search directly on PostgreSQL is done (`GET /v1/search`) that for identifiers and
A short text is sufficient.

To search the full text of Roy transcriptand tickets, make the following index and a consumer
Zinc `cx.persisted.events.v1` And the event `voice.transcript.ready` Write.

```json
PUT /cx-transcript
{
  "settings": {
    "index": { "number_of_shards": 3, "number_of_replicas": 1 },
    "analysis": {
      "analyzer": {
        "persian_text": {
          "tokenizer": "standard",
          "char_filter": ["zero_width_spaces"],
          "filter": ["lowercase", "decimal_digit", "arabic_normalization", "persian_normalization"]
        }
      },
      "char_filter": {
        "zero_width_spaces": { "type": "mapping", "mappings": ["\u200C=>\u0020"] }
      }
    }
  },
  "mappings": {
    "properties": {
      "tenant_id":     { "type": "keyword" },
      "call_id":       { "type": "keyword" },
      "customer_id":   { "type": "keyword" },
      "agent_id":      { "type": "keyword" },
      "occurred_at":   { "type": "date" },
      "language":      { "type": "keyword" },
      "text":          { "type": "text", "analyzer": "persian_text" },
      "segments": {
        "type": "nested",
        "properties": {
          "speaker":  { "type": "keyword" },
          "start_ms": { "type": "long" },
          "text":     { "type": "text", "analyzer": "persian_text" }
        }
      }
    }
  }
}
```

**Important:** Edition only redacted Done transcript be indexed unless access to the index
To the roles with `cx.pii.read` be limited.
