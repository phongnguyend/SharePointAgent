"""Run only in the remote image build; verify the upstream release artifact."""
import hashlib
import urllib.request
from pathlib import Path

REVISION = "17871fd27a880b3f8dad94841774db70e59fca47"
SHA256 = "840e3f50e5a9c218727f44e121d1b37cc9e2c3b318c8eb422ba6ef2e27b618a2"
SIZE = 8005437472
FILE = "Winnow-E4B-Q8_0.gguf"


def main():
    url = f"https://huggingface.co/EldanRing/Winnow-E4B/resolve/{REVISION}/gguf/{FILE}"
    target = Path(FILE)
    digest = hashlib.sha256()
    size = 0
    with urllib.request.urlopen(url, timeout=120) as response, target.open("wb") as output:
        while chunk := response.read(1024 * 1024):
            output.write(chunk)
            digest.update(chunk)
            size += len(chunk)
    if size != SIZE or digest.hexdigest() != SHA256:
        target.unlink()
        raise SystemExit("Winnow artifact size/SHA256 does not match the pinned release")


if __name__ == "__main__":
    main()
