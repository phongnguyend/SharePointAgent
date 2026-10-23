"""Dependency-free packaging tests: no Docker, network calls or model downloads."""
import hashlib
import importlib.util
import io
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]


def load_module(service, filename):
    path = ROOT / "backend" / "SystemOne" / service / filename
    spec = importlib.util.spec_from_file_location(service + filename, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class AuthenticationTests(unittest.TestCase):
    def test_missing_short_and_multiline_keys_never_start_server(self):
        for service, variable in (("WinowNative", "WINNOW_API_KEY"),):
            module = load_module(service, "entrypoint.py")
            for key in ("", "short", " " * 40, "a" * 32 + "\nextra", "a" * 32 + "\rextra"):
                with self.subTest(service=service, key_length=len(key)):
                    with patch.dict(os.environ, {variable: key}, clear=True), patch.object(module.os, "execv") as execv, patch.object(module.os, "execvp") as execvp:
                        with self.assertRaises(SystemExit):
                            module.main()
                        execv.assert_not_called()
                        execvp.assert_not_called()

    def test_winnow_passes_private_key_file_not_secret_in_arguments(self):
        module = load_module("WinowNative", "entrypoint.py")
        key = "test-token-" * 4
        with patch.dict(os.environ, {"WINNOW_API_KEY": key}, clear=True), patch.object(module.os, "execv") as execute:
            module.main()
            arguments = execute.call_args.args[1]
            key_path = Path(arguments[arguments.index("--api-key-file") + 1])
            try:
                self.assertEqual(key_path.read_text(), key)
                self.assertNotIn(key, " ".join(arguments))
                self.assertNotIn("WINNOW_API_KEY", os.environ)
                self.assertIn("--text-only", arguments)
                self.assertEqual(arguments[arguments.index("--target") + 1], "e4b-q8")
                if os.name != "nt":
                    self.assertEqual(key_path.stat().st_mode & 0o777, 0o600)
            finally:
                key_path.unlink()


class ArtifactTests(unittest.TestCase):
    def check_download(self, expected_size, expected_digest, succeeds):
        module = load_module("WinowNative", "download_model.py")
        payload = b"small fake GGUF fixture, not model weights"
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "fixture.gguf"
            # Redirect the local output path while leaving the pinned URL intact.
            with patch.object(module, "Path", return_value=destination), patch.object(module, "SIZE", expected_size), patch.object(module, "SHA256", expected_digest), patch.object(module.urllib.request, "urlopen", return_value=io.BytesIO(payload)) as request:
                if succeeds:
                    module.main()
                    self.assertEqual(destination.read_bytes(), payload)
                else:
                    with self.assertRaisesRegex(SystemExit, "size/SHA256"):
                        module.main()
                    self.assertFalse(destination.exists())
                url = request.call_args.args[0]
                self.assertIn("/resolve/" + module.REVISION + "/", url)
                self.assertNotIn("/resolve/main/", url)

    def test_valid_artifact_is_accepted(self):
        payload = b"small fake GGUF fixture, not model weights"
        self.check_download(len(payload), hashlib.sha256(payload).hexdigest(), True)

    def test_wrong_digest_is_rejected_and_removed(self):
        payload = b"small fake GGUF fixture, not model weights"
        self.check_download(len(payload), "0" * 64, False)

    def test_truncated_artifact_is_rejected_and_removed(self):
        payload = b"small fake GGUF fixture, not model weights"
        self.check_download(len(payload) + 1, hashlib.sha256(payload).hexdigest(), False)


if __name__ == "__main__":
    unittest.main()
