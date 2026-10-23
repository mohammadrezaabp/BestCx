-- Seed the default CX taxonomy so AI results always carry a taxonomy_version.
-- The CX Lead can publish new versions from the panel without a deploy (panel proposal 10).

INSERT INTO taxonomy (id, tenant_id, name, description, created_at)
VALUES ('11111111-1111-4111-8111-111111111111', 'bestcx', 'cx-core', 'Default CX classification taxonomy', now())
ON CONFLICT (tenant_id, name) DO NOTHING;

INSERT INTO taxonomy_version (id, taxonomy_id, version, status, definition, created_by, published_by, created_at, published_at)
VALUES (
    '22222222-2222-4222-8222-222222222222',
    '11111111-1111-4111-8111-111111111111',
    'v1',
    'active',
    '{
      "categories": [
        {"key": "authentication", "label_fa": "Authentication", "subcategories": ["credential_configuration", "invalid_credentials", "otp", "session_expiry"]},
        {"key": "billing", "label_fa": "Finance and billing", "subcategories": ["invoice", "refund", "pricing", "payment_failure"]},
        {"key": "payment", "label_fa": "Payment", "subcategories": ["timeout", "gateway_error", "settlement", "reversal"]},
        {"key": "integration", "label_fa": "Integration", "subcategories": ["api_error", "webhook", "sdk", "documentation"]},
        {"key": "performance", "label_fa": "Efficiency", "subcategories": ["latency", "timeout", "availability"]},
        {"key": "onboarding", "label_fa": "Start of work", "subcategories": ["registration", "kyc", "activation"]},
        {"key": "product_usage", "label_fa": "Use of the product", "subcategories": ["how_to", "feature_request", "ui_issue"]},
        {"key": "support_quality", "label_fa": "Support quality", "subcategories": ["response_time", "resolution_quality", "agent_behavior"]},
        {"key": "other", "label_fa": "Others", "subcategories": []}
      ],
      "intents": [
        "technical_support", "billing_inquiry", "complaint", "feature_request",
        "cancellation", "information_request", "praise", "unknown"
      ],
      "journey_stages": [
        "awareness", "onboarding", "activation", "usage", "support", "renewal", "churn_risk"
      ],
      "resolution_statuses": ["resolved", "partially_resolved", "unresolved", "unknown"],
      "severity_rules": [
        {"severity": "critical", "when": "service unavailable, data loss, security concern or payment lost"},
        {"severity": "high", "when": "core flow blocked for the customer with no workaround"},
        {"severity": "medium", "when": "degraded experience with a workaround"},
        {"severity": "low", "when": "cosmetic, informational or general question"}
      ]
    }'::jsonb,
    'system', 'system', now(), now()
)
ON CONFLICT (taxonomy_id, version) DO NOTHING;

UPDATE taxonomy
SET active_version_id = '22222222-2222-4222-8222-222222222222'
WHERE id = '11111111-1111-4111-8111-111111111111'
  AND active_version_id IS NULL;
