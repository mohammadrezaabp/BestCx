-- Customers can now be defined by an operator, not only discovered from events, and a
-- voice message is a first-class feedback row linked to the call that holds its audio.

ALTER TABLE customer_identity
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();

-- Voice feedback: the feedback row carries the transcript, the voice_call row carries the audio.
ALTER TABLE feedback
    ADD COLUMN IF NOT EXISTS call_id VARCHAR(128);

CREATE INDEX IF NOT EXISTS ix_feedback_call ON feedback (tenant_id, call_id) WHERE call_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_feedback_time ON feedback (tenant_id, submitted_at DESC, id DESC);

-- Operators search customers by name and phone.
CREATE INDEX IF NOT EXISTS ix_customer_identity_name ON customer_identity (tenant_id, lower(display_name));
