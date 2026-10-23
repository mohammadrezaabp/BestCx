# Security

## Public source and private configuration

BestCx is a fresh source repository. Environment files, private keys, certificates, credentials, database copies, logs, and build outputs are excluded from Git and Docker build contexts. `.env.example` has empty credential fields. `scripts/setup.py` generates independent random local values and refuses to overwrite existing configuration.

Never commit production connection strings, customer exports, recordings, tokens, or database dumps. Store runtime secrets in your deployment's secret manager. If a credential is exposed, revoke or rotate it immediately; deleting a file does not revoke it.

## Local defaults

- The console requires login and has no committed administrator password.
- PostgreSQL and storage require locally generated credentials. Compose fails when required values are missing.
- Published ports bind to `127.0.0.1`. Plaintext infrastructure endpoints are intended only for the isolated local development network.
- Demo customer data and the legacy connector are disabled.
- Sessions use HttpOnly, strict same-site cookies, login lockout, and account security stamps.
- The portal proxy strips operator authorization headers and API keys. Customer access, permissions, and tenant boundaries are checked on the server.
- Customer prompt content is untrusted. Personal data is redacted before analysis.
- The application container runs without root privileges and with Linux capabilities dropped.

## Production deployment

Use HTTPS with a trusted reverse proxy. Configure forwarded headers and secure cookies for that deployment. Restrict infrastructure, administrative endpoints, and observability services to private networks. Configure SSO or strong individual accounts, least privilege, durable session key storage where required, backups with verified restores, and retention for personal data and recordings.

The local stack is not a production security baseline. Pin and maintain image versions or digests for your environment and scan dependencies and images before deployment. Review provider data handling. Do not enable mock authentication on a publicly reachable deployment.

## Publication checks

Run `python scripts/check-release.py` and `gitleaks git --redact`. Run `npm audit` in both interface directories and `dotnet list BestCx.sln package --vulnerable --include-transitive` for the server. CI checks the public source policy and relevant builds and tests.

## Vulnerability reports

Use this repository's private GitHub vulnerability reporting feature. Do not publish secrets, customer data, or exploit details in a public issue.
