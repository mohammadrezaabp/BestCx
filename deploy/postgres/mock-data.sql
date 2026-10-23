-- Database-backed showcase data for local/demo mode.
-- Every business key is stable and every insert is idempotent, so startup can safely run
-- this file more than once. This file is not a schema migration and is only executed when
-- MockData:Enabled=true.

INSERT INTO customer_identity
    (id, tenant_id, customer_id, business_id, display_name, segment, attributes,
     first_seen_at, last_seen_at)
VALUES
    ('a0000000-0000-4000-8000-000000000001', 'bestcx', 'demo-customer-1001', 'merchant-501',
     'Arman Trading', 'enterprise', '{"city":"Tehran","plan":"growth"}',
     now() - interval '120 days', now() - interval '18 minutes'),
    ('a0000000-0000-4000-8000-000000000002', 'bestcx', 'demo-customer-1002', 'merchant-502',
     'Setareh Market', 'smb', '{"city":"Shiraz","plan":"standard"}',
     now() - interval '75 days', now() - interval '3 hours'),
    ('a0000000-0000-4000-8000-000000000003', 'bestcx', 'demo-customer-1003', 'merchant-503',
     'Darya Services', 'startup', '{"city":"Mashhad","plan":"starter"}',
     now() - interval '32 days', now() - interval '1 day')
ON CONFLICT (tenant_id, customer_id) DO NOTHING;

WITH events(event_id, customer_id, event_name, event_category, age, source_type,
            source_service, product_id, correlation_id, result, error_code) AS (
    VALUES
      ('mock-login-1001', 'demo-customer-1001', 'user.login.succeeded', 'user_session', interval '2 days', 'web', 'merchant-portal', NULL, 'mock-flow-1001', 'succeeded', NULL),
      ('mock-view-1001', 'demo-customer-1001', 'product.service.viewed', 'product_interaction', interval '47 hours', 'web', 'merchant-portal', 'payments', 'mock-flow-1001', 'viewed', NULL),
      ('mock-fail-1001-a', 'demo-customer-1001', 'api.request.failed', 'api', interval '46 hours', 'backend', 'payment-service', 'payments', 'mock-flow-1001', 'failed', 'AUTH_FAILED'),
      ('mock-fail-1001-b', 'demo-customer-1001', 'api.request.failed', 'api', interval '45 hours', 'backend', 'payment-service', 'payments', 'mock-flow-1001', 'failed', 'AUTH_FAILED'),
      ('mock-ticket-1001', 'demo-customer-1001', 'support.ticket.created', 'support', interval '44 hours', 'support', 'support-desk', 'payments', 'TKT-MOCK-1001', 'opened', NULL),
      ('mock-feedback-1001', 'demo-customer-1001', 'feedback.csat.submitted', 'feedback', interval '40 hours', 'feedback', 'feedback-form', 'payments', 'TKT-MOCK-1001', 'submitted', NULL),
      ('mock-login-1002', 'demo-customer-1002', 'user.login.succeeded', 'user_session', interval '30 hours', 'web', 'merchant-portal', NULL, 'mock-flow-1002', 'succeeded', NULL),
      ('mock-fail-1002', 'demo-customer-1002', 'api.request.failed', 'api', interval '29 hours', 'backend', 'settlement-service', 'settlement', 'mock-flow-1002', 'failed', 'TIMEOUT'),
      ('mock-ticket-1002', 'demo-customer-1002', 'support.ticket.created', 'support', interval '28 hours', 'support', 'support-desk', 'settlement', 'TKT-MOCK-1002', 'opened', NULL),
      ('mock-feedback-1002', 'demo-customer-1002', 'feedback.nps.submitted', 'feedback', interval '20 hours', 'feedback', 'feedback-form', 'settlement', 'TKT-MOCK-1002', 'submitted', NULL),
      ('mock-login-1003', 'demo-customer-1003', 'user.login.succeeded', 'user_session', interval '5 hours', 'mobile', 'merchant-app', NULL, 'mock-flow-1003', 'succeeded', NULL),
      ('mock-activate-1003', 'demo-customer-1003', 'product.service.activated', 'product_interaction', interval '4 hours', 'mobile', 'merchant-app', 'identity', 'mock-flow-1003', 'succeeded', NULL),
      ('mock-feedback-1003', 'demo-customer-1003', 'feedback.csat.submitted', 'feedback', interval '3 hours', 'feedback', 'feedback-form', 'identity', 'mock-flow-1003', 'submitted', NULL)
)
INSERT INTO cx_event
    (event_id, tenant_id, customer_id, event_name, event_category, schema_version,
     occurred_at, received_at, source_type, source_service, product_id, correlation_id, payload)
SELECT event_id, 'bestcx', customer_id, event_name, event_category, '1.0',
       now() - age, now() - age + interval '2 seconds', source_type, source_service,
       product_id, correlation_id,
       jsonb_build_object(
           'event_id', event_id,
           'event_name', event_name,
           'tenant_id', 'bestcx',
           'customer', jsonb_build_object('customer_id', customer_id),
           'product', jsonb_build_object('product_id', product_id),
           'interaction', jsonb_strip_nulls(jsonb_build_object('result', result, 'error_code', error_code)),
           'source', jsonb_build_object('type', source_type, 'service', source_service))
FROM events
ON CONFLICT (event_id) DO NOTHING;

INSERT INTO feedback
    (id, tenant_id, event_id, provider, external_id, customer_id, product_id,
     feedback_type, rating, nps_score, csat_score, comment, channel, status, reviewed, submitted_at)
VALUES
    ('b0000000-0000-4000-8000-000000000001', 'bestcx', 'mock-feedback-1001', 'mock-db', 'FB-MOCK-1001',
     'demo-customer-1001', 'payments', 'csat', 1, NULL, 1,
     'Payments have failed since yesterday and support has not replied yet.', 'web', 'new', false,
     now() - interval '40 hours'),
    ('b0000000-0000-4000-8000-000000000002', 'bestcx', 'mock-feedback-1002', 'mock-db', 'FB-MOCK-1002',
     'demo-customer-1002', 'settlement', 'nps', NULL, 5, NULL,
     'Daily settlement is slow, although it eventually completes.', 'email', 'in_review', false,
     now() - interval '20 hours'),
    ('b0000000-0000-4000-8000-000000000003', 'bestcx', 'mock-feedback-1003', 'mock-db', 'FB-MOCK-1003',
     'demo-customer-1003', 'identity', 'csat', 5, NULL, 5,
     'The onboarding flow was clear and activation worked immediately.', 'mobile', 'closed', true,
     now() - interval '3 hours')
ON CONFLICT (tenant_id, provider, external_id) DO NOTHING;

INSERT INTO support_case
    (id, tenant_id, provider, external_id, customer_id, product_id, subject, description,
     status, priority, channel, assignee_id, escalated, correlation_id, opened_at,
     first_response_at, resolved_at, updated_at)
VALUES
    ('c0000000-0000-4000-8000-000000000001', 'bestcx', 'mock-db', 'TKT-MOCK-1001',
     'demo-customer-1001', 'payments', 'Payment authentication failures',
     'Multiple transactions return AUTH_FAILED and customer payments are blocked.',
     'open', 'critical', 'email', 'agent-12', true, 'TKT-MOCK-1001',
     now() - interval '44 hours', now() - interval '43 hours', NULL, now() - interval '18 minutes'),
    ('c0000000-0000-4000-8000-000000000002', 'bestcx', 'mock-db', 'TKT-MOCK-1002',
     'demo-customer-1002', 'settlement', 'Daily settlement latency',
     'Settlement reports take several hours and occasionally time out.',
     'pending_customer', 'medium', 'chat', 'agent-07', false, 'TKT-MOCK-1002',
     now() - interval '28 hours', now() - interval '27 hours', NULL, now() - interval '6 hours'),
    ('c0000000-0000-4000-8000-000000000003', 'bestcx', 'mock-db', 'TKT-MOCK-1003',
     'demo-customer-1003', 'identity', 'Activation question',
     'Customer requested guidance for the identity activation flow.',
     'resolved', 'low', 'chat', 'agent-03', false, 'TKT-MOCK-1003',
     now() - interval '2 days', now() - interval '47 hours', now() - interval '46 hours', now() - interval '46 hours')
ON CONFLICT (tenant_id, provider, external_id) DO NOTHING;

INSERT INTO voice_call
    (id, tenant_id, call_id, customer_id, agent_id, queue_id, direction, started_at,
     answered_at, ended_at, duration_ms, end_reason, provider, provider_call_id,
     correlation_id, product_id, lifecycle_state, created_at, updated_at)
VALUES
    ('d0000000-0000-4000-8000-000000000001', 'bestcx', 'mock-call-9001', 'demo-customer-1001',
     'agent-12', 'priority-support', 'inbound', now() - interval '25 hours',
     now() - interval '25 hours' + interval '18 seconds', now() - interval '25 hours' + interval '12 minutes',
     702000, 'completed', 'mock-db', 'provider-mock-9001', 'TKT-MOCK-1001', 'payments',
     'ANALYZED', now() - interval '25 hours', now() - interval '24 hours'),
    ('d0000000-0000-4000-8000-000000000002', 'bestcx', 'mock-call-9002', 'demo-customer-1002',
     'agent-07', 'settlement-support', 'outbound', now() - interval '6 hours',
     now() - interval '6 hours' + interval '10 seconds', now() - interval '6 hours' + interval '6 minutes',
     350000, 'completed', 'mock-db', 'provider-mock-9002', 'TKT-MOCK-1002', 'settlement',
     'COMPLETED', now() - interval '6 hours', now() - interval '5 hours')
ON CONFLICT (tenant_id, call_id) DO NOTHING;

INSERT INTO voice_recording
    (id, call_id, tenant_id, object_key, content_type, codec, sample_rate, channels,
     size_bytes, duration_ms, checksum_sha256, storage_state, language, created_at, verified_at)
VALUES
    ('e0000000-0000-4000-8000-000000000001', 'd0000000-0000-4000-8000-000000000001', 'bestcx',
     'mock/podium/voice/mock-call-9001.wav', 'audio/wav', 'pcm_s16le', 16000, 1,
     22464000, 702000, repeat('a', 64), 'READY', 'en', now() - interval '25 hours', now() - interval '24 hours')
ON CONFLICT (tenant_id, object_key) DO NOTHING;

INSERT INTO voice_transcript
    (id, recording_id, tenant_id, language, engine, model, model_version, status,
     average_confidence, transcript_text, redacted_text, created_at)
VALUES
    ('f0000000-0000-4000-8000-000000000001', 'e0000000-0000-4000-8000-000000000001', 'bestcx',
     'en', 'mock', 'database-fixture', 'v1', 'SUCCEEDED', 0.91,
     'Customer reports repeated authentication failures. Agent confirms an escalation and promises a follow-up.',
     'Customer reports repeated authentication failures. Agent confirms an escalation and promises a follow-up.',
     now() - interval '24 hours')
ON CONFLICT (id) DO NOTHING;

INSERT INTO voice_transcript_segment
    (transcript_id, segment_index, speaker, speaker_ref, start_ms, end_ms, text, redacted_text, confidence)
VALUES
    ('f0000000-0000-4000-8000-000000000001', 0, 'customer', 'demo-customer-1001', 0, 18000,
     'Several payments have failed with an authentication error.',
     'Several payments have failed with an authentication error.', 0.93),
    ('f0000000-0000-4000-8000-000000000001', 1, 'agent', 'agent-12', 18500, 36000,
     'I can see the failures and will escalate this to the payments team.',
     'I can see the failures and will escalate this to the payments team.', 0.92),
    ('f0000000-0000-4000-8000-000000000001', 2, 'customer', 'demo-customer-1001', 36500, 51000,
     'Please update us today because transactions are blocked.',
     'Please update us today because transactions are blocked.', 0.89)
ON CONFLICT (transcript_id, segment_index) DO NOTHING;

INSERT INTO ai_analysis
    (id, tenant_id, entity_type, entity_id, customer_id, analysis_type, model_provider,
     model_name, model_version, prompt_version, taxonomy_version, input_hash, result,
     confidence, status, created_at)
VALUES
    ('10000000-0000-4000-8000-000000000001', 'bestcx', 'voice_call', 'mock-call-9001',
     'demo-customer-1001', 'cx_classification', 'mock', 'database-fixture', 'v1', 'cx-prompt-v1', '1.0', repeat('1', 64),
     '{"sentiment":"negative","intent":"report_incident","category":"payment_failure","subcategory":"authentication","severity":"critical","journey_stage":"support","resolution_status":"unresolved","summary":"Repeated payment authentication failures are blocking transactions; the case was escalated.","action_items":["Investigate AUTH_FAILED spike","Contact customer today"],"mentioned_products":["payments"],"confidence":0.68,"call_reason":"Payment failures","follow_up_required":true}',
     0.68, 'succeeded', now() - interval '23 hours'),
    ('10000000-0000-4000-8000-000000000002', 'bestcx', 'feedback', 'b0000000-0000-4000-8000-000000000002',
     'demo-customer-1002', 'cx_classification', 'mock', 'database-fixture', 'v1', 'cx-prompt-v1', '1.0', repeat('2', 64),
     '{"sentiment":"mixed","intent":"report_degradation","category":"settlement_latency","severity":"medium","journey_stage":"support","resolution_status":"partially_resolved","summary":"Settlement completes but is consistently slow and sometimes times out.","action_items":["Review settlement latency"],"mentioned_products":["settlement"],"confidence":0.82,"follow_up_required":true}',
     0.82, 'succeeded', now() - interval '19 hours'),
    ('10000000-0000-4000-8000-000000000003', 'bestcx', 'feedback', 'b0000000-0000-4000-8000-000000000003',
     'demo-customer-1003', 'cx_classification', 'mock', 'database-fixture', 'v1', 'cx-prompt-v1', '1.0', repeat('3', 64),
     '{"sentiment":"positive","intent":"praise","category":"onboarding","severity":"low","journey_stage":"activation","resolution_status":"resolved","summary":"Customer completed onboarding and activation without assistance.","action_items":[],"mentioned_products":["identity"],"confidence":0.95,"follow_up_required":false}',
     0.95, 'succeeded', now() - interval '2 hours')
ON CONFLICT (id) DO NOTHING;

INSERT INTO human_review
    (id, tenant_id, analysis_id, entity_type, entity_id, status, review_reason, created_at)
VALUES
    ('20000000-0000-4000-8000-000000000001', 'bestcx', '10000000-0000-4000-8000-000000000001',
     'voice_call', 'mock-call-9001', 'PENDING', 'low_confidence', now() - interval '23 hours')
ON CONFLICT (analysis_id) DO NOTHING;

INSERT INTO integration_checkpoint
    (id, tenant_id, integration_name, integration_type, cursor, status, last_success_at,
     records_processed, updated_at)
VALUES
    ('30000000-0000-4000-8000-000000000001', 'bestcx', 'podium-mock-db', 'database-fixture',
     'seed-v1', 'healthy', now(), 13, now())
ON CONFLICT (tenant_id, integration_name) DO UPDATE
SET status = 'healthy', last_success_at = now(), last_error = NULL, updated_at = now();

INSERT INTO processing_job
    (id, tenant_id, job_key, job_type, state, attempts, result_ref, created_at, updated_at)
VALUES
    ('40000000-0000-4000-8000-000000000001', 'bestcx', 'mock:transcribe:9001', 'TRANSCRIBE',
     'SUCCEEDED', 1, 'f0000000-0000-4000-8000-000000000001', now() - interval '24 hours', now() - interval '24 hours'),
    ('40000000-0000-4000-8000-000000000002', 'bestcx', 'mock:analyse:9001', 'AI_ANALYSIS',
     'SUCCEEDED', 1, '10000000-0000-4000-8000-000000000001', now() - interval '23 hours', now() - interval '23 hours')
ON CONFLICT (job_key) DO NOTHING;

INSERT INTO audit_log
    (tenant_id, actor_id, action, resource_type, resource_id, after, reason, occurred_at)
SELECT 'bestcx', 'demo-admin', 'mock_data.seeded', 'database', 'podium-demo',
       '{"customers":3,"events":13,"source":"database"}',
       'Database-backed demo mode enabled', now()
WHERE NOT EXISTS (
    SELECT 1 FROM audit_log
    WHERE tenant_id = 'bestcx' AND action = 'mock_data.seeded' AND resource_id = 'podium-demo'
);
