# BestCx customer portal

Independent English, left-to-right React interface served on `http://localhost:18081` by the provided Compose stack. It proxies only customer API paths to the application host. Operator authorization headers and API keys are stripped by the proxy.

## Routes

- `/complaints`: submit a complaint with a category, title, and text or voice message.
- `/feedbacks`: submit feedback, satisfaction (CSAT, 1-5), and ease of use (CES, 1-7).
- `/voice-of-customer`: answer category-based questions and the recommendation question (NPS, 0-10).
- `/tracking`: view the customer's own requests and public activity, confirm resolution, or request reopening.
- `/surveys`: view and answer campaign invitations.
- `/notifications`: view the customer's notifications.

Customers sign in using accounts managed from their profiles in the operator console. There are no default customer passwords or public registration. The server derives the customer identity from its HttpOnly session cookie; it does not trust identity fields supplied by the browser.

## Topics and submissions

Only topics whose complete ancestry is enabled are shown. Empty and unavailable topic catalogs show explicit states. The taxonomy revision accompanies submissions so a changed catalog can be refreshed safely.

Text and audio submissions use the persisted-event pipeline. Acceptance does not mean transcription or analysis has completed. CSAT and CES are stored independently; unanswered scores are never treated as zero. A voice message is converted to speech-quality WAV and uploaded with its answers, subject to the server's size and duration limits.

No provider key, administrator credential, or server connection string is included in the browser bundle. The backend performs access checks and personal-data redaction before AI analysis.

## Develop

Start BestCx using the root README, then run:

```sh
npm ci
npm run dev
```

Vite uses port 5174 and proxies to `http://localhost:18080`. Set `CX_BACKEND_URL` locally to use another backend.

```sh
npm run build
npx playwright install chromium
npm test
```

The portal uses its own session and never inherits operator permissions. Use HTTPS and the deployment controls described in the root SECURITY.md before enabling remote access.
