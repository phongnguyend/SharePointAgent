"""Require authentication before starting the native server on external ingress."""
import os
import sys
import tempfile


def main():
    key = os.environ.pop("WINNOW_API_KEY", "")
    if len(key.strip()) < 32 or "\n" in key or "\r" in key:
        raise SystemExit("WINNOW_API_KEY must be a single-line secret of at least 32 characters")
    # Upstream WINNOW_API_KEY configures clients, not server authentication.
    # Use the server's key-file option to keep the secret out of argv/logs.
    with tempfile.NamedTemporaryFile(mode="w", prefix="winnow-key-", delete=False) as output:
        output.write(key)
        key_file = output.name
    os.execv(sys.executable, [
        sys.executable, "/app/scripts/serve.py",
        "--host", "0.0.0.0", "--port", "8091",
        "--model", "/opt/models/Winnow-E4B-Q8_0.gguf",
        "--target", "e4b-q8", "--alias", "Winnow-E4B",
        "--text-only", "--mtp", "off", "--context", "8192",
        "--decision-parallel", "1", "--chat-parallel", "1",
        "--batch", "512", "--ubatch", "256", "--threads", "8",
        "--cache", "f16", "--api-key-file", key_file,
    ])


if __name__ == "__main__":
    main()
