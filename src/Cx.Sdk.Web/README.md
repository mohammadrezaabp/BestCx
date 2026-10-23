# Podium CX Web SDK

A browser-style client to send customer experience events to Ingestion API.

```ts
import { createTracker } from '@podium/cx-sdk';

const cx = createTracker({
  endpoint: 'https://cx.example.com',
  apiKey: import.meta.env.VITE_CX_KEY,   // Key with access cx.event.ingest
  service: 'merchant-portal',
  environment: 'production',
  sampling: { 'page.viewed': 0.3 },      // High volume and low value events are sampled
  metadataAllowlist: ['path', 'product_id', 'error_code', 'duration_ms'],
});

cx.identify('c_123', 'b_456');

cx.page();

cx.track('button.clicked', { path: '/checkout' });

cx.track('api.request.failed', { error_code: 'AUTH_FAILED' }, {
  productId: 'payments',
  interaction: { action: 'api_request', result: 'failed', duration_ms: 426 },
});
```

## Warranties

- Sending **Never** Does not block user navigation.
- Events batch are and in `pagehide` With `sendBeacon` are sent.
- The queue is bounded; In overflow, the oldest events are deleted.
- Sensitive keys (token, password, card And…) They are removed before sending.
- `event_id` It is generated on the client side retry Do not cause duplicate records.
