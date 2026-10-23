-- CES uses 1 (very difficult) to 7 (very easy). Missing answers remain NULL.
ALTER TABLE feedback ADD COLUMN IF NOT EXISTS ces_score INT;
ALTER TABLE feedback ADD COLUMN IF NOT EXISTS selected_topic VARCHAR(128);
ALTER TABLE feedback ADD CONSTRAINT ck_feedback_ces CHECK (ces_score BETWEEN 1 AND 7);

-- Portal ratings were explicitly collected as satisfaction; unrelated star ratings
-- and NPS are deliberately excluded from this historical conversion.
UPDATE feedback SET csat_score = rating
WHERE provider = 'cx-customer-portal' AND feedback_type IN ('voc', 'generic', 'voice')
  AND csat_score IS NULL AND rating BETWEEN 1 AND 5;

UPDATE feedback f SET selected_topic = e.payload->'metadata'->>'selected_category'
FROM cx_event e WHERE e.event_id = f.event_id AND e.tenant_id = f.tenant_id
  AND f.selected_topic IS NULL;

CREATE INDEX IF NOT EXISTS ix_feedback_topic_time ON feedback (tenant_id, selected_topic, submitted_at DESC);
