"""Check tracked release content without reading private local configuration."""
from pathlib import Path
import re
import subprocess


def main():
    root = Path(__file__).resolve().parent.parent
    result = subprocess.run(["git", "ls-files", "-z"], cwd=root, check=True, capture_output=True)
    paths = [name for name in result.stdout.decode("utf-8").split("\0") if name]
    if not paths:
        raise SystemExit("Stage the intended source release before checking it.")
    errors = []
    ranges = [(0x0600, 0x06FF), (0x0750, 0x077F), (0x08A0, 0x08FF), (0xFB50, 0xFDFF), (0xFE70, 0xFEFF)]
    token_patterns = [r"sk-(?:or-v1-)?[A-Za-z0-9_-]{24,}", r"gh[pousr]_[A-Za-z0-9_]{30,}", r"github_pat_[A-Za-z0-9_]{30,}", r"AKIA[A-Z0-9]{16}"]
    forbidden_parts = {"node_modules", "bin", "obj", "dist", "secrets", "credentials"}
    forbidden_extensions = {".pem", ".key", ".pfx", ".p12", ".jks", ".dump", ".db", ".sqlite", ".log", ".bak"}
    for name in paths:
        path = Path(name)
        if (path.name.startswith(".env") and path.name != ".env.example") or path.suffix.lower() in forbidden_extensions or any(part in forbidden_parts for part in path.parts):
            errors.append(f"Forbidden release path: {name}")
        try:
            text = (root / path).read_bytes().decode("utf-8-sig")
        except UnicodeDecodeError:
            errors.append(f"Binary file requires review: {name}")
            continue
        if any(any(start <= ord(char) <= end for start, end in ranges) for char in text):
            errors.append(f"Non-English script found: {name}")
        if any(re.search(pattern, text) for pattern in token_patterns):
            errors.append(f"Possible secret in: {name}")
    example = (root / ".env.example").read_text("utf-8-sig")
    for key in ["OPENROUTER_API_KEY", "DEEPGRAM_API_KEY", "CX_ADMIN_PASSWORD", "POSTGRES_PASSWORD", "MINIO_ACCESS_KEY", "MINIO_SECRET_KEY"]:
        if not re.search(rf"^{key}=$", example, re.MULTILINE):
            errors.append(f"Example credential must be empty: {key}")
    if errors:
        raise SystemExit("\n".join(errors))
    print(f"Release policy passed for {len(paths)} tracked UTF-8 source files.")


if __name__ == "__main__":
    main()
