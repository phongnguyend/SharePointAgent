import asyncio
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

from fastapi import HTTPException
from fastapi.testclient import TestClient
from app import main


class ApiTests(unittest.TestCase):
    def test_env_file_configures_authentication_and_preserves_environment(self):
        with tempfile.TemporaryDirectory() as directory:
            env_file = Path(directory) / ".env"
            env_file.write_text("PAGEINDEX_SERVICE_API_KEY=file-secret\nPAGEINDEX_MAX_FILE_BYTES=100\n", encoding="utf-8")
            with patch.dict(os.environ, {}, clear=True), patch.object(main, "ENV_FILE", env_file):
                os.environ["PAGEINDEX_MAX_FILE_BYTES"] = "200"
                with TestClient(main.app) as client:
                    self.assertEqual(200, main.app.state.max_bytes)
                    self.assertEqual(401, client.post("/index").status_code)
                    self.assertEqual(422, client.post("/index", headers={"X-Api-Key": "file-secret"}).status_code)

    def test_missing_env_file_is_optional(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.dict(os.environ, {}, clear=True), patch.object(main, "ENV_FILE", Path(directory) / ".env"):
                with TestClient(main.app) as client:
                    self.assertEqual(200, client.get("/health").status_code)
                    self.assertEqual(25 * 1024 * 1024, main.app.state.max_bytes)

    def test_real_pdf_index_includes_page_text(self):
        content = b"BT /F1 24 Tf 40 740 Td (1 Introduction) Tj /F1 12 Tf 0 -40 Td (This document describes the test project.) Tj 0 -20 Td (The source evidence must be retained.) Tj ET"
        objects = [b"<< /Type /Catalog /Pages 2 0 R >>",
                   b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                   b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
                   b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                   b"<< /Length " + str(len(content)).encode() + b" >>\nstream\n" + content + b"\nendstream"]
        pdf = b"%PDF-1.4\n"
        offsets = [0]
        for number, obj in enumerate(objects, 1):
            offsets.append(len(pdf))
            pdf += str(number).encode() + b" 0 obj\n" + obj + b"\nendobj\n"
        xref = len(pdf)
        pdf += b"xref\n0 6\n0000000000 65535 f \n"
        for offset in offsets[1:]:
            pdf += f"{offset:010d} 00000 n \n".encode()
        pdf += f"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF".encode()
        with patch.dict(os.environ, {"PAGEINDEX_SERVICE_API_KEY": ""}), TestClient(main.app) as client:
            response = client.post("/index", files={"file": ("report.pdf", pdf)})
            self.assertEqual(200, response.status_code, response.text)
            self.assertEqual("report", response.json()["doc_name"])
            self.assertIn("source evidence", json.dumps(response.json()["structure"]))
            self.assertEqual(0, response.json()["usage"]["total_tokens"])
            self.assertEqual(0, response.json()["usage"]["model_calls"])
            self.assertEqual(0, response.json()["usage"]["input_tokens"])
            self.assertEqual(0, response.json()["usage"]["output_tokens"])

    def test_authentication_precedes_upload_validation(self):
        with patch.dict(os.environ, {"PAGEINDEX_SERVICE_API_KEY": "secret"}), TestClient(main.app) as client:
            self.assertEqual(200, client.get("/health").status_code)
            self.assertEqual(401, client.post("/index").status_code)
            self.assertEqual(401, client.post("/index", headers={"X-Api-Key": "wrong"}).status_code)
            self.assertEqual(422, client.post("/index", headers={"X-Api-Key": "secret"}).status_code)

    def test_validation(self):
        with patch.dict(os.environ, {"PAGEINDEX_SERVICE_API_KEY": "", "PAGEINDEX_MAX_FILE_BYTES": "10"}), TestClient(main.app) as client:
            for name, content, status in [("a.docx", b"text", 415), ("a.md", b"", 422),
                                          ("a.md", b"\xff", 422), ("a.pdf", b"broken", 422),
                                          ("a.md", b"x" * 11, 413)]:
                with self.subTest(name=name, status=status):
                    self.assertEqual(status, client.post("/index", files={"file": (name, content)}).status_code)

    def test_options_filename_and_cleanup(self):
        seen = []

        async def worker(directory, timeout):
            seen.append(directory)
            options = json.loads((directory / "request.json").read_text())
            self.assertEqual("report", options["name"])
            self.assertFalse(options["include_text"])
            self.assertTrue(options["include_summaries"])
            self.assertEqual("source.md", options["source"])
            return {"structure": []}

        with patch.dict(os.environ, {"PAGEINDEX_SERVICE_API_KEY": ""}), patch.object(main, "run_worker", worker), TestClient(main.app) as client:
            response = client.post("/index", files={"file": ("../../report.md", b"# Title")},
                                   data={"include_text": "false", "include_summaries": "true"})
            self.assertEqual(200, response.status_code)
        self.assertFalse(seen[0].exists())

    def test_upstream_failure_is_sanitized_and_cleans_up(self):
        seen = []

        async def worker(directory, timeout):
            seen.append(directory)
            raise HTTPException(502, "Indexing failed")

        with patch.dict(os.environ, {"PAGEINDEX_SERVICE_API_KEY": ""}), patch.object(main, "run_worker", worker), TestClient(main.app) as client:
            self.assertEqual(502, client.post("/index", files={"file": ("a.md", b"# A")}).status_code)
        self.assertFalse(seen[0].exists())

    def test_real_markdown_index_preserves_preamble_and_hierarchy(self):
        with patch.dict(os.environ, {"PAGEINDEX_SERVICE_API_KEY": ""}), TestClient(main.app) as client:
            response = client.post("/index", files={"file": ("report.md", b"Preamble\n# Overview\nBody\n## Detail\nEvidence")})
            self.assertEqual(200, response.status_code, response.text)
            tree = response.json()
            self.assertEqual("report", tree["doc_name"])
            self.assertEqual({"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0,
                              "model_calls": 0, "calls_without_usage": 0,
                              "input_tokens": 0, "output_tokens": 0,
                              "model_id": os.environ.get("PAGEINDEX_INDEX_MODEL", "gpt-4.1-mini")}, tree["usage"])
            self.assertEqual(2, tree["source_line_offset"])
            self.assertIn("Preamble", tree["structure"][0]["text"])
            self.assertEqual("Detail", tree["structure"][1]["nodes"][0]["title"])

    def test_real_headingless_markdown_without_text(self):
        with patch.dict(os.environ, {"PAGEINDEX_SERVICE_API_KEY": ""}), TestClient(main.app) as client:
            response = client.post("/index", files={"file": ("notes.md", b"Important evidence")}, data={"include_text": "false"})
            self.assertEqual(200, response.status_code, response.text)
            self.assertEqual("Document", response.json()["structure"][0]["title"])
            self.assertNotIn("text", response.json()["structure"][0])


class WorkerTests(unittest.IsolatedAsyncioTestCase):
    async def test_timeout_kills_worker(self):
        process = Mock()
        process.returncode = None
        stopped = asyncio.Event()

        async def wait():
            await stopped.wait()
            return 0

        def kill():
            process.returncode = -1
            stopped.set()

        process.wait = wait
        process.kill.side_effect = kill
        with patch.object(main.asyncio, "create_subprocess_exec", return_value=process):
            with self.assertRaises(HTTPException) as error:
                await main.run_worker(Path.cwd(), 0.01)
            self.assertEqual(504, error.exception.status_code)
            process.kill.assert_called_once()


if __name__ == "__main__":
    unittest.main()
