/**
 * Podium CX browser tracking SDK (implementation spec 12).
 *
 * Design rules this file follows:
 * - Telemetry NEVER blocks navigation or user interaction.
 * - Events are batched, retried with a bounded queue, and flushed on page hide.
 * - Only allowlisted metadata keys are sent; secrets are stripped before the network.
 * - High-volume, low-value events can be sampled away at the source.
 */

export interface CxTrackerOptions {
  /** Ingestion API base, e.g. https://cx.example.com */
  endpoint: string;
  /** Producer API key. Use a key scoped to cx.event.ingest only. */
  apiKey: string;
  tenantId?: string;
  service: string;
  environment?: string;
  /** Events buffered before an automatic flush. */
  batchSize?: number;
  /** Maximum time an event waits in the buffer, in ms. */
  flushIntervalMs?: number;
  /** Hard cap on the queue; oldest events are dropped beyond it so memory stays bounded. */
  maxQueueSize?: number;
  /** Metadata keys allowed to leave the browser. Anything else is dropped. */
  metadataAllowlist?: string[];
  /** Per-event-name sampling rate in [0,1]. Missing entries are always sent. */
  sampling?: Record<string, number>;
  debug?: boolean;
}

interface QueuedEvent {
  event_id: string;
  schema_version: '1.0';
  event_name: string;
  event_category: string;
  occurred_at: string;
  tenant_id: string;
  customer?: { customer_id?: string; anonymous_id?: string; business_id?: string };
  session?: { session_id: string };
  source: { type: 'web'; service: string; channel: 'web'; environment?: string };
  product?: { product_id?: string; service_id?: string };
  interaction?: Record<string, unknown>;
  context?: { request_id?: string };
  metadata: Record<string, unknown>;
}

const ANON_KEY = 'cx.anonymous_id';
const SESSION_KEY = 'cx.session_id';
const SESSION_TS_KEY = 'cx.session_ts';
const SESSION_TTL_MS = 30 * 60 * 1000;

/** Never send these, whatever the allowlist says. */
const FORBIDDEN = [
  'password', 'passwd', 'secret', 'token', 'api_key', 'apikey', 'authorization',
  'access_token', 'refresh_token', 'cvv', 'card_number', 'pan', 'otp', 'pin',
];

const CATEGORY_BY_PREFIX: Record<string, string> = {
  user: 'user_session',
  page: 'product_interaction',
  button: 'product_interaction',
  form: 'product_interaction',
  product: 'product_interaction',
  api: 'api',
  feedback: 'feedback',
  support: 'support',
  voice: 'voice',
};

export class CxTracker {
  private readonly options: Required<Omit<CxTrackerOptions, 'sampling' | 'metadataAllowlist'>> &
    Pick<CxTrackerOptions, 'sampling' | 'metadataAllowlist'>;

  private queue: QueuedEvent[] = [];
  private timer: ReturnType<typeof setInterval> | null = null;
  private customerId?: string;
  private businessId?: string;
  private flushing = false;

  constructor(options: CxTrackerOptions) {
    this.options = {
      tenantId: 'bestcx',
      environment: 'production',
      batchSize: 20,
      flushIntervalMs: 5000,
      maxQueueSize: 500,
      debug: false,
      ...options,
    } as CxTracker['options'];

    if (typeof window !== 'undefined') {
      this.timer = setInterval(() => void this.flush(), this.options.flushIntervalMs);

      // pagehide fires reliably on mobile where unload does not; sendBeacon survives it.
      window.addEventListener('pagehide', () => this.flushBeacon());
      document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'hidden') this.flushBeacon();
      });
    }
  }

  /** Associates subsequent events with a signed-in customer. */
  identify(customerId: string, businessId?: string): void {
    this.customerId = customerId;
    this.businessId = businessId;
  }

  track(eventName: string, metadata: Record<string, unknown> = {}, extra?: {
    productId?: string;
    serviceId?: string;
    interaction?: Record<string, unknown>;
  }): void {
    if (!this.shouldSample(eventName)) return;

    const event: QueuedEvent = {
      event_id: newUlid(),
      schema_version: '1.0',
      event_name: eventName,
      event_category: categoryOf(eventName),
      occurred_at: new Date().toISOString(),
      tenant_id: this.options.tenantId,
      customer: {
        customer_id: this.customerId,
        anonymous_id: anonymousId(),
        business_id: this.businessId,
      },
      session: { session_id: sessionId() },
      source: {
        type: 'web',
        service: this.options.service,
        channel: 'web',
        environment: this.options.environment,
      },
      product: extra?.productId || extra?.serviceId
        ? { product_id: extra.productId, service_id: extra.serviceId }
        : undefined,
      interaction: extra?.interaction,
      metadata: this.sanitize(metadata),
    };

    this.enqueue(event);
  }

  page(path?: string, metadata: Record<string, unknown> = {}): void {
    this.track('page.viewed', {
      path: path ?? (typeof location !== 'undefined' ? location.pathname : undefined),
      referrer: typeof document !== 'undefined' ? document.referrer || undefined : undefined,
      ...metadata,
    });
  }

  /** Flush what is buffered. Safe to call at any time; failures never throw to the caller. */
  async flush(): Promise<void> {
    if (this.flushing || this.queue.length === 0) return;

    this.flushing = true;
    const batch = this.queue.splice(0, this.options.batchSize);

    try {
      const response = await fetch(`${this.options.endpoint.replace(/\/$/, '')}/v1/events/batch`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Api-Key': this.options.apiKey },
        body: JSON.stringify(batch),
        keepalive: true,
      });

      if (!response.ok && response.status >= 500) {
        // Server-side failures are worth retrying; a 4xx means the events are
        // malformed and retrying them forever would just block the queue.
        this.requeue(batch);
      }
    } catch (error) {
      this.requeue(batch);
      this.log('flush failed', error);
    } finally {
      this.flushing = false;
    }
  }

  /** Last-chance delivery on page hide; sendBeacon survives the document teardown. */
  private flushBeacon(): void {
    if (this.queue.length === 0 || typeof navigator === 'undefined' || !navigator.sendBeacon) {
      void this.flush();
      return;
    }

    const batch = this.queue.splice(0, this.queue.length);
    // sendBeacon cannot set headers, so the key travels as a query parameter.
    // The API accepts it over TLS for this path only.
    const url = `${this.options.endpoint.replace(/\/$/, '')}/v1/events/batch?api_key=${encodeURIComponent(
      this.options.apiKey,
    )}`;

    const delivered = navigator.sendBeacon(url, new Blob([JSON.stringify(batch)], {
      type: 'application/json',
    }));

    if (!delivered) this.requeue(batch);
  }

  dispose(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  private enqueue(event: QueuedEvent): void {
    this.queue.push(event);

    if (this.queue.length > this.options.maxQueueSize) {
      // Bounded queue: drop the oldest rather than growing without limit.
      const dropped = this.queue.length - this.options.maxQueueSize;
      this.queue.splice(0, dropped);
      this.log(`queue full, dropped ${dropped} event(s)`);
    }

    if (this.queue.length >= this.options.batchSize) void this.flush();
  }

  private requeue(batch: QueuedEvent[]): void {
    this.queue = [...batch, ...this.queue].slice(0, this.options.maxQueueSize);
  }

  private shouldSample(eventName: string): boolean {
    const rate = this.options.sampling?.[eventName];
    return rate === undefined || Math.random() < rate;
  }

  private sanitize(metadata: Record<string, unknown>): Record<string, unknown> {
    const allowlist = this.options.metadataAllowlist;
    const result: Record<string, unknown> = {};

    for (const [key, value] of Object.entries(metadata)) {
      if (value === undefined || value === null) continue;

      const lower = key.toLowerCase();
      if (FORBIDDEN.some((f) => lower.includes(f))) {
        this.log(`dropped forbidden metadata key '${key}'`);
        continue;
      }

      if (allowlist && !allowlist.includes(key)) continue;

      // Only primitives cross the wire: a nested object could smuggle anything.
      result[key] = typeof value === 'object' ? JSON.stringify(value).slice(0, 500) : value;
    }

    return result;
  }

  private log(...args: unknown[]): void {
    if (this.options.debug) console.debug('[cx]', ...args);
  }
}

function categoryOf(eventName: string): string {
  const prefix = eventName.split('.')[0];
  return CATEGORY_BY_PREFIX[prefix] ?? 'product_interaction';
}

function anonymousId(): string {
  if (typeof localStorage === 'undefined') return newUlid();
  let id = localStorage.getItem(ANON_KEY);
  if (!id) {
    id = newUlid();
    localStorage.setItem(ANON_KEY, id);
  }
  return id;
}

/** A session expires after 30 minutes of inactivity, matching the context window policy. */
function sessionId(): string {
  if (typeof sessionStorage === 'undefined') return newUlid();

  const now = Date.now();
  const last = Number(sessionStorage.getItem(SESSION_TS_KEY) ?? 0);
  let id = sessionStorage.getItem(SESSION_KEY);

  if (!id || now - last > SESSION_TTL_MS) {
    id = newUlid();
    sessionStorage.setItem(SESSION_KEY, id);
  }

  sessionStorage.setItem(SESSION_TS_KEY, String(now));
  return id;
}

const ULID_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

/** Lexicographically sortable id; the server accepts producer-supplied ids for idempotency. */
function newUlid(): string {
  let timestamp = Date.now();
  let time = '';
  for (let i = 0; i < 10; i++) {
    time = ULID_ALPHABET[timestamp % 32] + time;
    timestamp = Math.floor(timestamp / 32);
  }

  let random = '';
  const bytes = new Uint8Array(16);
  if (typeof crypto !== 'undefined' && crypto.getRandomValues) crypto.getRandomValues(bytes);
  else for (let i = 0; i < 16; i++) bytes[i] = Math.floor(Math.random() * 256);
  for (let i = 0; i < 16; i++) random += ULID_ALPHABET[bytes[i] % 32];

  return time + random;
}

export function createTracker(options: CxTrackerOptions): CxTracker {
  return new CxTracker(options);
}
