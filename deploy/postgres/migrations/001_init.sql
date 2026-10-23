-- BestCx - initial schema
-- Source: technical design 10, voice design 6, implementation spec 5.
-- Every timestamp is TIMESTAMPTZ and stored in UTC (spec 37.13).

-- ---------------------------------------------------------------------------
-- Pipeline A: capture & persistence
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS cx_event (
    id               BIGSERIAL PRIMARY KEY,
    event_id         VARCHAR(64)  NOT NULL,
    tenant_id        VARCHAR(64)  NOT NULL,
    customer_id      VARCHAR(128),
    anonymous_id     VARCHAR(128),
    session_id       VARCHAR(128),
    event_name       VARCHAR(160) NOT NULL,
    event_category   VARCHAR(80)  NOT NULL,
    schema_version   VARCHAR(16)  NOT NULL,
    occurred_at      TIMESTAMPTZ  NOT NULL,
    received_at      TIMESTAMPTZ  NOT NULL,
    source_type      VARCHAR(32),
    source_service   VARCHAR(128),
    product_id       VARCHAR(128),
    service_id       VARCHAR(128),
    trace_id         VARCHAR(64),
    correlation_id   VARCHAR(128),
    payload          JSONB        NOT NULL,
    created_at       TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_cx_event_event_id UNIQUE (event_id)
);

CREATE INDEX IF NOT EXISTS ix_cx_event_customer_time ON cx_event (tenant_id, customer_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_cx_event_name_time     ON cx_event (event_name, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_cx_event_correlation   ON cx_event (correlation_id);
CREATE INDEX IF NOT EXISTS ix_cx_event_session       ON cx_event (tenant_id, session_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_cx_event_category_time ON cx_event (tenant_id, event_category, occurred_at DESC);

-- Transactional outbox. Written in the same TX as cx_event (technical design 5.3).
-- published_at stays NULL under Debezium (CDC reads the WAL); the fallback relay stamps it.
CREATE TABLE IF NOT EXISTS cx_outbox (
    id              UUID PRIMARY KEY,
    aggregate_type  VARCHAR(64)  NOT NULL,
    aggregate_id    VARCHAR(128) NOT NULL,
    event_type      VARCHAR(160) NOT NULL,
    topic           VARCHAR(160) NOT NULL,
    partition_key   VARCHAR(256) NOT NULL,
    payload         JSONB        NOT NULL,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT now(),
    published_at    TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_cx_outbox_unpublished ON cx_outbox (created_at) WHERE published_at IS NULL;

-- ---------------------------------------------------------------------------
-- Customer / feedback / support
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS customer_identity (
    id             UUID PRIMARY KEY,
    tenant_id      VARCHAR(64)  NOT NULL,
    customer_id    VARCHAR(128) NOT NULL,
    business_id    VARCHAR(128),
    display_name   VARCHAR(256),
    phone_hash     VARCHAR(128),
    email_hash     VARCHAR(128),
    segment        VARCHAR(64),
    attributes     JSONB        NOT NULL DEFAULT '{}'::jsonb,
    first_seen_at  TIMESTAMPTZ  NOT NULL,
    last_seen_at   TIMESTAMPTZ  NOT NULL,
    merged_into_id UUID REFERENCES customer_identity(id),
    created_at     TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_customer_identity UNIQUE (tenant_id, customer_id)
);

CREATE INDEX IF NOT EXISTS ix_customer_identity_business ON customer_identity (tenant_id, business_id);
CREATE INDEX IF NOT EXISTS ix_customer_identity_phone    ON customer_identity (tenant_id, phone_hash);
CREATE INDEX IF NOT EXISTS ix_customer_identity_last_seen ON customer_identity (tenant_id, last_seen_at DESC);

CREATE TABLE IF NOT EXISTS feedback (
    id            UUID PRIMARY KEY,
    tenant_id     VARCHAR(64)  NOT NULL,
    event_id      VARCHAR(64),
    provider      VARCHAR(64)  NOT NULL,
    external_id   VARCHAR(191) NOT NULL,
    customer_id   VARCHAR(128),
    product_id    VARCHAR(128),
    feedback_type VARCHAR(32)  NOT NULL,
    rating        INT,
    nps_score     INT,
    csat_score    INT,
    comment       TEXT,
    channel       VARCHAR(64),
    status        VARCHAR(32)  NOT NULL DEFAULT 'new',
    assignee_id   VARCHAR(128),
    reviewed      BOOLEAN      NOT NULL DEFAULT false,
    submitted_at  TIMESTAMPTZ  NOT NULL,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_feedback_external UNIQUE (tenant_id, provider, external_id)
);

CREATE INDEX IF NOT EXISTS ix_feedback_customer_time ON feedback (tenant_id, customer_id, submitted_at DESC);
CREATE INDEX IF NOT EXISTS ix_feedback_status        ON feedback (tenant_id, status, submitted_at DESC);

CREATE TABLE IF NOT EXISTS support_case (
    id                UUID PRIMARY KEY,
    tenant_id         VARCHAR(64)  NOT NULL,
    provider          VARCHAR(64)  NOT NULL,
    external_id       VARCHAR(191) NOT NULL,
    customer_id       VARCHAR(128),
    product_id        VARCHAR(128),
    subject           TEXT,
    description       TEXT,
    status            VARCHAR(32)  NOT NULL DEFAULT 'open',
    priority          VARCHAR(32),
    channel           VARCHAR(64),
    assignee_id       VARCHAR(128),
    escalated         BOOLEAN      NOT NULL DEFAULT false,
    correlation_id    VARCHAR(128),
    opened_at         TIMESTAMPTZ  NOT NULL,
    first_response_at TIMESTAMPTZ,
    resolved_at       TIMESTAMPTZ,
    updated_at        TIMESTAMPTZ  NOT NULL DEFAULT now(),
    created_at        TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_support_case_external UNIQUE (tenant_id, provider, external_id)
);

CREATE INDEX IF NOT EXISTS ix_support_case_customer_time ON support_case (tenant_id, customer_id, opened_at DESC);
CREATE INDEX IF NOT EXISTS ix_support_case_status        ON support_case (tenant_id, status, opened_at DESC);
CREATE INDEX IF NOT EXISTS ix_support_case_correlation   ON support_case (correlation_id);

-- ---------------------------------------------------------------------------
-- Voice
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS voice_call (
    id               UUID PRIMARY KEY,
    tenant_id        VARCHAR(64)  NOT NULL,
    call_id          VARCHAR(128) NOT NULL,
    customer_id      VARCHAR(128),
    agent_id         VARCHAR(128),
    queue_id         VARCHAR(128),
    direction        VARCHAR(16),
    started_at       TIMESTAMPTZ,
    answered_at      TIMESTAMPTZ,
    ended_at         TIMESTAMPTZ,
    duration_ms      BIGINT,
    end_reason       VARCHAR(64),
    provider         VARCHAR(64),
    provider_call_id VARCHAR(128),
    correlation_id   VARCHAR(128),
    product_id       VARCHAR(128),
    lifecycle_state  VARCHAR(40)  NOT NULL DEFAULT 'DISCOVERED',
    created_at       TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_voice_call UNIQUE (tenant_id, call_id)
);

CREATE INDEX IF NOT EXISTS ix_voice_call_customer_time ON voice_call (tenant_id, customer_id, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_voice_call_agent_time    ON voice_call (tenant_id, agent_id, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_voice_call_correlation   ON voice_call (correlation_id);

CREATE TABLE IF NOT EXISTS voice_recording (
    id                 UUID PRIMARY KEY,
    call_id            UUID         NOT NULL REFERENCES voice_call(id) ON DELETE CASCADE,
    tenant_id          VARCHAR(64)  NOT NULL,
    object_key         TEXT         NOT NULL,
    content_type       VARCHAR(128),
    codec              VARCHAR(32),
    sample_rate        INT,
    channels           INT,
    size_bytes         BIGINT,
    duration_ms        BIGINT,
    checksum_sha256    VARCHAR(64),
    encryption_key_ref VARCHAR(256),
    storage_state      VARCHAR(32)  NOT NULL DEFAULT 'DISCOVERED',
    source_uri         TEXT,
    language           VARCHAR(16),
    created_at         TIMESTAMPTZ  NOT NULL DEFAULT now(),
    verified_at        TIMESTAMPTZ,
    CONSTRAINT uq_voice_recording_object UNIQUE (tenant_id, object_key)
);

CREATE INDEX IF NOT EXISTS ix_voice_recording_call  ON voice_recording (call_id);
CREATE INDEX IF NOT EXISTS ix_voice_recording_state ON voice_recording (tenant_id, storage_state, created_at DESC);

CREATE TABLE IF NOT EXISTS voice_transcript (
    id                       UUID PRIMARY KEY,
    recording_id             UUID         NOT NULL REFERENCES voice_recording(id) ON DELETE CASCADE,
    tenant_id                VARCHAR(64)  NOT NULL,
    language                 VARCHAR(16),
    engine                   VARCHAR(64)  NOT NULL,
    model                    VARCHAR(128) NOT NULL,
    model_version            VARCHAR(64),
    status                   VARCHAR(32)  NOT NULL,
    average_confidence       NUMERIC(5,4),
    transcript_text          TEXT,
    redacted_text            TEXT,
    artifact_object_key      TEXT,
    redaction_map            JSONB,
    supersedes_transcript_id UUID REFERENCES voice_transcript(id),
    created_at               TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_voice_transcript_recording ON voice_transcript (recording_id, created_at DESC);

CREATE TABLE IF NOT EXISTS voice_transcript_segment (
    id            BIGSERIAL PRIMARY KEY,
    transcript_id UUID        NOT NULL REFERENCES voice_transcript(id) ON DELETE CASCADE,
    segment_index INT         NOT NULL,
    speaker       VARCHAR(32),
    speaker_ref   VARCHAR(128),
    start_ms      BIGINT      NOT NULL,
    end_ms        BIGINT      NOT NULL,
    text          TEXT        NOT NULL,
    redacted_text TEXT,
    confidence    NUMERIC(5,4),
    CONSTRAINT uq_voice_transcript_segment UNIQUE (transcript_id, segment_index)
);

-- ---------------------------------------------------------------------------
-- AI / review / taxonomy
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS ai_analysis (
    id                     UUID PRIMARY KEY,
    tenant_id              VARCHAR(64)  NOT NULL,
    entity_type            VARCHAR(32)  NOT NULL,
    entity_id              VARCHAR(128) NOT NULL,
    customer_id            VARCHAR(128),
    analysis_type          VARCHAR(64)  NOT NULL,
    model_provider         VARCHAR(64)  NOT NULL,
    model_name             VARCHAR(128) NOT NULL,
    model_version          VARCHAR(64),
    prompt_version         VARCHAR(64),
    taxonomy_version       VARCHAR(64),
    input_hash             VARCHAR(64),
    result                 JSONB        NOT NULL,
    confidence             NUMERIC(5,4),
    status                 VARCHAR(32)  NOT NULL,
    created_at             TIMESTAMPTZ  NOT NULL DEFAULT now(),
    supersedes_analysis_id UUID REFERENCES ai_analysis(id)
);

CREATE INDEX IF NOT EXISTS ix_ai_analysis_entity   ON ai_analysis (entity_type, entity_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_ai_analysis_customer ON ai_analysis (tenant_id, customer_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_ai_analysis_status   ON ai_analysis (tenant_id, status, created_at DESC);

CREATE TABLE IF NOT EXISTS human_review (
    id              UUID PRIMARY KEY,
    tenant_id       VARCHAR(64)  NOT NULL,
    analysis_id     UUID         NOT NULL REFERENCES ai_analysis(id) ON DELETE CASCADE,
    entity_type     VARCHAR(32)  NOT NULL,
    entity_id       VARCHAR(128) NOT NULL,
    status          VARCHAR(32)  NOT NULL DEFAULT 'PENDING',
    review_reason   VARCHAR(64)  NOT NULL,
    reviewer_id     VARCHAR(128),
    previous_result JSONB,
    override_result JSONB,
    reason          TEXT,
    notes           TEXT,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT now(),
    reviewed_at     TIMESTAMPTZ,
    CONSTRAINT uq_human_review_analysis UNIQUE (analysis_id)
);

CREATE INDEX IF NOT EXISTS ix_human_review_status ON human_review (status, created_at);

CREATE TABLE IF NOT EXISTS taxonomy (
    id                UUID PRIMARY KEY,
    tenant_id         VARCHAR(64)  NOT NULL,
    name              VARCHAR(128) NOT NULL,
    description       TEXT,
    active_version_id UUID,
    created_at        TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_taxonomy_name UNIQUE (tenant_id, name)
);

CREATE TABLE IF NOT EXISTS taxonomy_version (
    id           UUID PRIMARY KEY,
    taxonomy_id  UUID        NOT NULL REFERENCES taxonomy(id) ON DELETE CASCADE,
    version      VARCHAR(64) NOT NULL,
    status       VARCHAR(32) NOT NULL DEFAULT 'draft',
    definition   JSONB       NOT NULL,
    created_by   VARCHAR(128),
    published_by VARCHAR(128),
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    published_at TIMESTAMPTZ,
    CONSTRAINT uq_taxonomy_version UNIQUE (taxonomy_id, version)
);

ALTER TABLE taxonomy
    DROP CONSTRAINT IF EXISTS fk_taxonomy_active_version;
ALTER TABLE taxonomy
    ADD CONSTRAINT fk_taxonomy_active_version
    FOREIGN KEY (active_version_id) REFERENCES taxonomy_version(id);

-- ---------------------------------------------------------------------------
-- Operations
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS audit_log (
    id            BIGSERIAL PRIMARY KEY,
    tenant_id     VARCHAR(64)  NOT NULL,
    actor_id      VARCHAR(128) NOT NULL,
    action        VARCHAR(64)  NOT NULL,
    resource_type VARCHAR(64)  NOT NULL,
    resource_id   VARCHAR(191) NOT NULL,
    before        JSONB,
    after         JSONB,
    reason        TEXT,
    ip_address    VARCHAR(64),
    occurred_at   TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_audit_log_resource ON audit_log (resource_type, resource_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_log_actor    ON audit_log (tenant_id, actor_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_log_time     ON audit_log (tenant_id, occurred_at DESC);

CREATE TABLE IF NOT EXISTS integration_checkpoint (
    id                 UUID PRIMARY KEY,
    tenant_id          VARCHAR(64)  NOT NULL,
    integration_name   VARCHAR(128) NOT NULL,
    integration_type   VARCHAR(64)  NOT NULL,
    cursor             TEXT,
    status             VARCHAR(32)  NOT NULL DEFAULT 'unknown',
    last_success_at    TIMESTAMPTZ,
    last_error_at      TIMESTAMPTZ,
    last_error         TEXT,
    records_processed  BIGINT       NOT NULL DEFAULT 0,
    updated_at         TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_integration_checkpoint UNIQUE (tenant_id, integration_name)
);

CREATE TABLE IF NOT EXISTS processing_job (
    id             UUID PRIMARY KEY,
    tenant_id      VARCHAR(64)  NOT NULL,
    job_key        VARCHAR(256) NOT NULL,
    job_type       VARCHAR(64)  NOT NULL,
    state          VARCHAR(32)  NOT NULL DEFAULT 'PENDING',
    attempts       INT          NOT NULL DEFAULT 0,
    last_error     TEXT,
    error_category VARCHAR(32),
    result_ref     VARCHAR(191),
    created_at     TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_processing_job_key UNIQUE (job_key)
);

CREATE INDEX IF NOT EXISTS ix_processing_job_state ON processing_job (state, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_processing_job_type  ON processing_job (job_type, state);
