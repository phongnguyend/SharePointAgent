"""Fail closed if the external service has no bearer token configured."""
import os


def main():
    key = os.environ.get("LAYA_API_KEY", "")
    if len(key.strip()) < 32 or "\n" in key or "\r" in key:
        raise SystemExit("LAYA_API_KEY must be a single-line secret of at least 32 characters")
    os.execvp("laya-serve", ["laya-serve"])


if __name__ == "__main__":
    main()
