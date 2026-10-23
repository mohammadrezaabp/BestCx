"""Create local development credentials without printing or publishing them."""
from pathlib import Path
import os
import re
import secrets


def main():
    root = Path(__file__).resolve().parent.parent
    destination = root / ".env"
    content = (root / ".env.example").read_text(encoding="utf-8-sig")
    values = {
        "CX_ADMIN_PASSWORD": secrets.token_urlsafe(32),
        "POSTGRES_PASSWORD": secrets.token_hex(32),
        "MINIO_ACCESS_KEY": secrets.token_hex(12),
        "MINIO_SECRET_KEY": secrets.token_urlsafe(32),
    }
    for name, value in values.items():
        content = re.sub(rf"^{name}=.*$", f"{name}={value}", content, flags=re.MULTILINE)
    try:
        descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError:
        raise SystemExit("An .env file already exists; it has been left unchanged.")
    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as file:
        file.write(content)
    print("Created a private .env with unique local credentials.")
    print("Read CX_ADMIN_PASSWORD in that file to sign in as admin.")
    print("Do not share the file. Next: docker compose up -d --build")


if __name__ == "__main__":
    main()
