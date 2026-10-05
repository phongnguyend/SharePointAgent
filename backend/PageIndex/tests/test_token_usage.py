import asyncio
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import litellm
from app.token_usage import collect_token_usage
from app.worker import index


class TokenUsageTests(unittest.IsolatedAsyncioTestCase):
    async def test_concurrent_async_and_sync_calls_are_counted_once(self):
        def completion(*args, **kwargs):
            return SimpleNamespace(usage={"prompt_tokens": 10, "completion_tokens": 3, "total_tokens": 13})

        async def acompletion(*args, **kwargs):
            return await asyncio.to_thread(litellm.completion)

        with patch.object(litellm, "completion", completion), patch.object(litellm, "acompletion", acompletion):
            with collect_token_usage() as usage:
                await asyncio.gather(*(litellm.acompletion() for _ in range(5)))
                await asyncio.to_thread(litellm.completion)
            self.assertIs(completion, litellm.completion)
            self.assertIs(acompletion, litellm.acompletion)
        self.assertEqual({"prompt_tokens": 60, "completion_tokens": 18, "total_tokens": 78,
                          "model_calls": 6, "calls_without_usage": 0}, usage)

    async def test_missing_usage_failure_and_scope_cleanup(self):
        with patch.object(litellm, "completion", side_effect=[{}, RuntimeError("failed")]) as original:
            with self.assertRaises(RuntimeError):
                with collect_token_usage() as usage:
                    litellm.completion()
                    litellm.completion()
            self.assertIs(original, litellm.completion)
            self.assertEqual(1, usage["model_calls"])
            self.assertEqual(1, usage["calls_without_usage"])
            self.assertEqual(0, usage["total_tokens"])
            with collect_token_usage() as next_usage:
                self.assertEqual(0, next_usage["model_calls"])

    async def test_markdown_summaries_return_usage(self):
        async def completion(*args, **kwargs):
            return SimpleNamespace(
                usage=SimpleNamespace(prompt_tokens=100, completion_tokens=20, total_tokens=120),
                choices=[SimpleNamespace(message=SimpleNamespace(content="Summary"))])

        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "source.md").write_text("# One\n" + "Evidence. " * 300, encoding="utf-8")
            (directory / "request.json").write_text(json.dumps({
                "source": "source.md", "name": "source", "include_text": True,
                "include_summaries": True, "model": "azure/test-deployment",
            }), encoding="utf-8")
            with patch.object(litellm, "acompletion", completion):
                result = await index(directory)
        self.assertEqual("Summary", result["structure"][0]["summary"])
        self.assertEqual(120, result["usage"]["total_tokens"])
        self.assertEqual("azure/test-deployment", result["usage"]["model_id"])
        self.assertEqual(100, result["usage"]["input_tokens"])
        self.assertEqual(20, result["usage"]["output_tokens"])
        self.assertEqual(1, result["usage"]["model_calls"])
