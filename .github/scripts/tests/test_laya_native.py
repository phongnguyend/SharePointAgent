"""Laya launcher checks; no Docker, network calls or model downloads."""
import importlib.util
import os
from pathlib import Path
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]


def load_entrypoint():
    spec = importlib.util.spec_from_file_location("laya_entrypoint", ROOT / "backend/LayaNative/entrypoint.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class LayaAuthenticationTests(unittest.TestCase):
    def test_missing_short_and_multiline_keys_never_start_server(self):
        module = load_entrypoint()
        for key in ("", "short", " " * 40, "a" * 32 + "\nextra", "a" * 32 + "\rextra"):
            with self.subTest(key_length=len(key)):
                with patch.dict(os.environ, {"LAYA_API_KEY": key}, clear=True), patch.object(module.os, "execvp") as execute:
                    with self.assertRaises(SystemExit):
                        module.main()
                    execute.assert_not_called()

    def test_preserves_key_for_laya_middleware(self):
        module = load_entrypoint()
        key = "test-token-" * 4
        with patch.dict(os.environ, {"LAYA_API_KEY": key}, clear=True), patch.object(module.os, "execvp") as execute:
            module.main()
            execute.assert_called_once_with("laya-serve", ["laya-serve"])
            self.assertEqual(os.environ["LAYA_API_KEY"], key)


if __name__ == "__main__":
    unittest.main()
