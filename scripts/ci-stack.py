"""Build and test a disposable BestCx stack without touching an existing installation."""
from pathlib import Path
import os
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request


def main():
    root = Path(__file__).resolve().parent.parent
    private_env = root / ".env"
    for resource in ("container", "volume", "network"):
        existing = subprocess.check_output(
            ["docker", resource, "ls", "-q", "--filter", "label=com.docker.compose.project=bestcx"],
            cwd=root, text=True,
        )
        if existing.strip():
            raise SystemExit("An existing BestCx installation was found; CI will not modify it.")
    if private_env.exists():
        raise SystemExit("An existing .env was found; CI will not use or replace it.")

    subprocess.run([sys.executable, "scripts/setup.py"], cwd=root, check=True, capture_output=True)
    env = {**os.environ}
    for line in private_env.read_text(encoding="utf-8").splitlines():
        if line and not line.startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            env[key] = value
    env.update({
        "OPENROUTER_API_KEY": "", "DEEPGRAM_API_KEY": "",
        "CX_E2E_PRODUCER_KEY": secrets.token_hex(32),
        "CX_E2E_ADMIN_KEY": secrets.token_hex(32),
        "CX_E2E_PORTAL_USERNAME": "ci-customer",
        "CX_E2E_PORTAL_PASSWORD": secrets.token_hex(32),
        "CX_E2E_BASE_URL": "http://127.0.0.1:18080",
        "CX_TEST_REQUIRE_DEPENDENCIES": "true",
    })
    compose = ["docker", "compose", "--project-name", "bestcx", "--env-file", str(private_env),
               "-f", "docker-compose.yml", "-f", "deploy/ci/compose.yml"]
    secret_values = [env[key] for key in ("POSTGRES_PASSWORD", "MINIO_ACCESS_KEY", "MINIO_SECRET_KEY",
                     "CX_ADMIN_PASSWORD", "CX_E2E_PRODUCER_KEY", "CX_E2E_ADMIN_KEY", "CX_E2E_PORTAL_PASSWORD")]
    try:
        subprocess.run(compose + ["up", "--detach", "--build", "--wait", "--wait-timeout", "240"],
                       cwd=root, env=env, check=True)
        # Readiness can report degraded storage or Kafka with HTTP 200; require Healthy.
        http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        deadline = time.monotonic() + 120
        while True:
            try:
                with http.open(env["CX_E2E_BASE_URL"] + "/health/ready", timeout=10) as response:
                    if response.read().decode().strip() == "Healthy":
                        break
            except (OSError, urllib.error.URLError):
                pass
            if time.monotonic() >= deadline:
                raise RuntimeError("The stack never reached healthy PostgreSQL, Kafka, and object storage.")
            time.sleep(2)
        for port in (18080, 18081):
            with http.open(f"http://127.0.0.1:{port}", timeout=10) as response:
                if response.status != 200 or b'id="root"' not in response.read():
                    raise RuntimeError(f"The browser application on port {port} is unavailable.")
        subprocess.run(["dotnet", "test", "tests/Cx.EndToEndTests/Cx.EndToEndTests.csproj", "-c", "Release",
                        "--logger", "trx", "--results-directory", "artifacts/end-to-end"],
                       cwd=root, env=env, check=True)
        subprocess.run([sys.executable, "scripts/check-test-results.py", "artifacts/end-to-end"],
                       cwd=root, check=True)
    except BaseException:
        logs = subprocess.run(compose + ["logs", "--no-color", "--tail", "100"],
                              cwd=root, env=env, capture_output=True, text=True)
        redacted = logs.stdout + logs.stderr
        for value in secret_values:
            redacted = redacted.replace(value, "[REDACTED]")
        print(redacted, file=sys.stderr)
        raise
    finally:
        try:
            subprocess.run(compose + ["down", "--volumes", "--remove-orphans"], cwd=root, env=env, check=True)
        finally:
            private_env.unlink()


if __name__ == "__main__":
    main()
