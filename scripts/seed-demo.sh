#!/usr/bin/env bash
# Seeds the running stack with a realistic English demo dataset so every panel
# screen has something to show: customers, failed API calls, feedback, support
# tickets and a full voice call with a recording.
#
#   ./scripts/seed-demo.sh
#
# Safe to re-run: every event carries a stable event_id, so replaying it is an
# idempotent no-op rather than a duplicate.

set -euo pipefail

BASE_URL="${CX_BASE_URL:-http://localhost:8080}"
PRODUCER_KEY="${CX_PRODUCER_KEY:-cx-dev-producer-key}"

# A corporate proxy must not intercept a localhost call.
export NO_PROXY="${NO_PROXY:-localhost,127.0.0.1}"
export no_proxy="$NO_PROXY"
unset http_proxy https_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY all_proxy 2>/dev/null || true

post_event() {
  local payload="$1"
  local code
  code=$(curl -sS -o /tmp/cx-seed-out.json -w '%{http_code}' \
    -X POST "$BASE_URL/v1/events" \
    -H 'Content-Type: application/json' \
    -H "X-Api-Key: $PRODUCER_KEY" \
    --data-binary "$payload")

  if [[ "$code" != "202" ]]; then
    echo "  ! rejected (HTTP $code): $(head -c 200 /tmp/cx-seed-out.json)" >&2
    return 1
  fi
}

iso() {
  # minutes ago -> ISO-8601 UTC
  local minutes="$1"
  if date -u -d "-${minutes} minutes" +%Y-%m-%dT%H:%M:%S.000Z 2>/dev/null; then
    return
  fi
  date -u -v-"${minutes}"M +%Y-%m-%dT%H:%M:%S.000Z
}

echo "==> waiting for $BASE_URL"
for _ in $(seq 1 60); do
  if curl -sSf "$BASE_URL/health" >/dev/null 2>&1; then break; fi
  sleep 2
done
curl -sSf "$BASE_URL/health" >/dev/null || { echo "platform is not reachable" >&2; exit 1; }

echo "==> seeding customer journeys"

seed_customer() {
  local cid="$1" bid="$2" product="$3" error_code="$4" subject="$5" description="$6" rating="$7" comment="$8"

  post_event "$(cat <<JSON
{
  "event_id": "seed-$cid-login",
  "schema_version": "1.0",
  "event_name": "user.login.succeeded",
  "event_category": "user_session",
  "occurred_at": "$(iso 120)",
  "tenant_id": "bestcx",
  "customer": { "customer_id": "$cid", "business_id": "$bid" },
  "session": { "session_id": "s-$cid" },
  "source": { "type": "web", "service": "merchant-portal", "channel": "web", "environment": "demo" }
}
JSON
)"

  post_event "$(cat <<JSON
{
  "event_id": "seed-$cid-view",
  "schema_version": "1.0",
  "event_name": "product.service.viewed",
  "event_category": "product_interaction",
  "occurred_at": "$(iso 118)",
  "tenant_id": "bestcx",
  "customer": { "customer_id": "$cid", "business_id": "$bid" },
  "session": { "session_id": "s-$cid" },
  "source": { "type": "web", "service": "merchant-portal", "channel": "web" },
  "product": { "product_id": "$product" },
  "metadata": { "path": "/services/$product" }
}
JSON
)"

  for attempt in 1 2 3; do
    post_event "$(cat <<JSON
{
  "event_id": "seed-$cid-fail-$attempt",
  "schema_version": "1.0",
  "event_name": "api.request.failed",
  "event_category": "api",
  "occurred_at": "$(iso $((115 - attempt)))",
  "tenant_id": "bestcx",
  "customer": { "customer_id": "$cid", "business_id": "$bid" },
  "source": { "type": "backend", "service": "$product-service", "channel": "api" },
  "product": { "product_id": "$product" },
  "interaction": { "action": "api_request", "result": "failed", "duration_ms": 426, "error_code": "$error_code" },
  "context": { "correlation_id": "corr-$cid" },
  "metadata": { "path": "/v2/$product", "attempt": $attempt }
}
JSON
)"
  done

  post_event "$(cat <<JSON
{
  "event_id": "seed-$cid-ticket",
  "schema_version": "1.0",
  "event_name": "support.ticket.created",
  "event_category": "support",
  "occurred_at": "$(iso 100)",
  "tenant_id": "bestcx",
  "customer": { "customer_id": "$cid", "business_id": "$bid" },
  "source": { "type": "support", "service": "support-desk", "channel": "email" },
  "product": { "product_id": "$product" },
  "context": { "correlation_id": "TKT-$cid" },
  "metadata": {
    "external_id": "TKT-$cid",
    "subject": "$subject",
    "description": "$description",
    "status": "open",
    "priority": "high"
  }
}
JSON
)"

  post_event "$(cat <<JSON
{
  "event_id": "seed-$cid-feedback",
  "schema_version": "1.0",
  "event_name": "feedback.csat.submitted",
  "event_category": "feedback",
  "occurred_at": "$(iso 90)",
  "tenant_id": "bestcx",
  "customer": { "customer_id": "$cid", "business_id": "$bid" },
  "source": { "type": "feedback", "service": "feedback-form", "channel": "web" },
  "product": { "product_id": "$product" },
  "metadata": {
    "external_id": "fb-$cid",
    "csat_score": $rating,
    "comment": "$comment"
  }
}
JSON
)"

  echo "  - $cid seeded"
}

seed_customer "c_demo_1001" "b_501" "payments" "AUTH_FAILED" \
  "Authentication error in the payment gateway" \
  "Since yesterday all our transactions with error AUTH_FAILED It returns and the customers payment is stopped." \
  1 "The payment service is not working since yesterday and the support has not yet responded."

seed_customer "c_demo_1002" "b_502" "settlement" "TIMEOUT" \
  "Slowness in daily settlement" \
  "The settlement report is made with a delay of several hours and sometimes timeout We get." \
  3 "The settlement speed is low, but it will be done eventually."

seed_customer "c_demo_1003" "b_503" "identity" "OTP_EXPIRED" \
  "Problem sending a one-time code" \
  "The verification code arrives late and has expired by then." \
  4 "After following support, the problem was solved, thanks to the team."

echo "==> seeding a voice call"

CALL_ID="call_demo_9001"

post_event "$(cat <<JSON
{
  "event_id": "seed-voice-started",
  "schema_version": "1.0",
  "event_name": "voice.call.started",
  "event_category": "voice",
  "occurred_at": "$(iso 80)",
  "tenant_id": "bestcx",
  "customer": { "customer_id": "c_demo_1001", "business_id": "b_501" },
  "source": { "type": "voip", "service": "contact-center", "channel": "voice" },
  "product": { "product_id": "payments" },
  "voice": { "call_id": "$CALL_ID", "direction": "inbound", "agent_id": "agent_12", "queue_id": "support" },
  "context": { "correlation_id": "TKT-c_demo_1001" },
  "privacy": { "contains_pii": true, "classification": "restricted" }
}
JSON
)"

post_event "$(cat <<JSON
{
  "event_id": "seed-voice-ended",
  "schema_version": "1.0",
  "event_name": "voice.call.ended",
  "event_category": "voice",
  "occurred_at": "$(iso 68)",
  "tenant_id": "bestcx",
  "customer": { "customer_id": "c_demo_1001", "business_id": "b_501" },
  "source": { "type": "voip", "service": "contact-center", "channel": "voice" },
  "product": { "product_id": "payments" },
  "voice": {
    "call_id": "$CALL_ID", "direction": "inbound", "agent_id": "agent_12", "queue_id": "support",
    "duration_ms": 720000, "end_reason": "completed", "provider": "contact-center"
  },
  "context": { "correlation_id": "TKT-c_demo_1001" },
  "privacy": { "contains_pii": true, "classification": "restricted" }
}
JSON
)"

echo "  - waiting for the call to be projected"
sleep 8

WAV="${TMPDIR:-/tmp}/cx-demo-recording.wav"

PYTHON=""
for candidate in python3 python py; do
  # Actually run it: on Windows `python3` resolves to a Store alias that is not a
  # usable interpreter, so presence on PATH proves nothing.
  if "$candidate" -c "import wave, struct, math" >/dev/null 2>&1; then
    PYTHON="$candidate"
    break
  fi
done

if [[ ! -f "$WAV" && -n "$PYTHON" ]]; then
  GEN="${TMPDIR:-/tmp}/cx-make-wav.py"
  # 12 seconds of two-channel tone: enough to exercise storage, checksum and playback.
  cat > "$GEN" <<'PY'
import math, struct, sys, wave
with wave.open(sys.argv[1], 'wb') as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(8000)
    data = bytearray()
    for i in range(8000 * 12):
        t = i / 8000.0
        left = int(9000 * math.sin(2 * math.pi * 220 * t) * (1 if (i // 24000) % 2 == 0 else 0.05))
        right = int(9000 * math.sin(2 * math.pi * 330 * t) * (1 if (i // 24000) % 2 == 1 else 0.05))
        data += struct.pack('<hh', left, right)
    w.writeframes(bytes(data))
PY
  "$PYTHON" "$GEN" "$WAV" || echo "  ! could not generate a demo recording" >&2
fi

if [[ ! -f "$WAV" ]]; then
  echo "  ! no python available to generate a demo recording; skipping the audio upload" >&2
  echo "    (the call itself is seeded and visible in the panel)" >&2
  WAV=""
fi

if [[ -n "$WAV" ]]; then
  code=$(curl -sS -o /tmp/cx-seed-rec.json -w '%{http_code}' \
    -X POST "$BASE_URL/v1/voice/recordings?callId=$CALL_ID&recordingId=rec_demo_a&durationMs=720000&channels=2&language=fa" \
    -H 'Content-Type: audio/wav' \
    -H "X-Api-Key: $PRODUCER_KEY" \
    --data-binary "@$WAV")

  if [[ "$code" == "202" ]]; then
    echo "  - recording stored: $(head -c 160 /tmp/cx-seed-rec.json)"
  else
    echo "  ! recording upload failed (HTTP $code): $(head -c 200 /tmp/cx-seed-rec.json)" >&2
  fi
fi

cat <<EOF

==> done

The pipeline is asynchronous: transcription and AI analysis land within a minute or two.

  console : $BASE_URL
  api docs: $BASE_URL/api-docs
  call    : $BASE_URL/voice/$CALL_ID

Sign in to the console with the admin key: ${CX_ADMIN_KEY:-cx-dev-admin-key}
EOF
