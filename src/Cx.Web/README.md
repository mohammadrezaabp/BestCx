# BestCx operator console

English, left-to-right React and TypeScript interface for customer experience and complaint handling. It uses the system font and bundles its assets locally.

## Workspaces

- `/`: overview and customer experience metrics.
- `/complaints`: complaint inbox, reports, calls, shared problems, cases, follow-up customers, service outcomes, and settings.
- `/experience`: feedback inbox, experience reports, campaigns, journey maps, and independent calls.
- `/customers`: customer profiles, contact details, and portal account management.
- `/taxonomy`: topic tree management.
- `/pipelines`, `/audit`, and `/notifications`: processing health, activity history, and notifications.

Complaints and feedback remain separate at the API boundary. A complaint is identified by its record type. Feedback has no manual review workflow; complaint case handling is separate. Legacy `/inbox` and report links redirect to the appropriate current workspace.

Dates use Gregorian calendars and English digits. Report boundaries use UTC. Account actions, theme selection, and notifications share the page title row. The notification menu shows the five latest notifications and links to the full grid.

## Develop

Start the BestCx backend using the root README, then run:

```sh
npm ci
npm run dev
```

The development console uses port 5173 and proxies API requests to `http://localhost:18080`. Production assets are served by the application host on port 18080 in the provided Compose stack.

```sh
npm run build
npm run typecheck
npx playwright install chromium
npm test
```

API failures are displayed explicitly. The console never substitutes demo data for an unavailable backend. Grid filters and paging use shared components, and workspace restrictions are enforced by the server.
