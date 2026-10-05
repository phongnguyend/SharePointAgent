"""Collect completed LiteLLM response usage inside one isolated indexing worker."""
from contextlib import contextmanager
from contextvars import ContextVar
from functools import wraps
from threading import Lock


@contextmanager
def collect_token_usage(enabled=True):
    usage = {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0,
             "model_calls": 0, "calls_without_usage": 0}
    if not enabled:
        yield usage
        return

    import litellm

    lock = Lock()
    # Some providers implement async completion through the sync entry point.
    # Count the outer response only, including across asyncio.to_thread calls.
    active = ContextVar("pageindex_usage_active", default=False)
    completion = litellm.completion
    acompletion = litellm.acompletion

    def record(response):
        reported = getattr(response, "usage", None)
        if isinstance(response, dict):
            reported = response.get("usage")
        with lock:
            usage["model_calls"] += 1
            values = [reported.get(key) if isinstance(reported, dict) else getattr(reported, key, None)
                      for key in ("prompt_tokens", "completion_tokens", "total_tokens")]
            if any(value is None for value in values):
                usage["calls_without_usage"] += 1
            for key, value in zip(("prompt_tokens", "completion_tokens", "total_tokens"), values):
                if value is not None:
                    usage[key] += int(value)

    @wraps(completion)
    def tracked_completion(*args, **kwargs):
        nested = active.get()
        token = active.set(True)
        try:
            response = completion(*args, **kwargs)
            if not nested:
                record(response)
            return response
        finally:
            active.reset(token)

    @wraps(acompletion)
    async def tracked_acompletion(*args, **kwargs):
        nested = active.get()
        token = active.set(True)
        try:
            response = await acompletion(*args, **kwargs)
            if not nested:
                record(response)
            return response
        finally:
            active.reset(token)

    # Safe here because the API starts a separate process for each document.
    # Record before returning, avoiding background callback timing races.
    litellm.completion = tracked_completion
    litellm.acompletion = tracked_acompletion
    try:
        yield usage
    finally:
        litellm.completion = completion
        litellm.acompletion = acompletion
