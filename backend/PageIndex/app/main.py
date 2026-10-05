import asyncio
from contextlib import asynccontextmanager
import json
import os
from pathlib import Path
import secrets
import sys
import tempfile

from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import JSONResponse
from dotenv import load_dotenv


ENV_FILE = Path(__file__).resolve().parent.parent / ".env"


def positive_setting(name: str, default: int) -> int:
    value = int(os.environ.get(name, default))
    if value <= 0:
        raise ValueError(f"{name} must be positive")
    return value


@asynccontextmanager
async def lifespan(app: FastAPI):
    load_dotenv(ENV_FILE, override=False)
    app.state.max_bytes = positive_setting("PAGEINDEX_MAX_FILE_BYTES", 25 * 1024 * 1024)
    app.state.timeout = positive_setting("PAGEINDEX_TIMEOUT_SECONDS", 300)
    app.state.slots = asyncio.Semaphore(positive_setting("PAGEINDEX_MAX_CONCURRENCY", 2))
    yield


app = FastAPI(title="PageIndex API", description="Build document trees from Markdown or PDF", version="1.0.0", lifespan=lifespan)


@app.middleware("http")
async def require_api_key(request: Request, call_next):
    key = os.environ.get("PAGEINDEX_SERVICE_API_KEY", "")
    if request.url.path != "/health" and key:
        supplied = request.headers.get("X-Api-Key", "")
        if not secrets.compare_digest(supplied.encode(), key.encode()):
            return JSONResponse(status_code=401, content={"detail": "Unauthorized"})
    return await call_next(request)


@app.get("/health")
def health():
    return {"status": "ok"}


async def run_worker(directory: Path, timeout: int) -> dict:
    worker = Path(__file__).with_name("worker.py").resolve()
    process = await asyncio.create_subprocess_exec(
        sys.executable, str(worker), str(directory), cwd=directory,
        stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL,
    )
    try:
        await asyncio.wait_for(process.wait(), timeout=timeout)
        if process.returncode != 0:
            raise HTTPException(502, "PageIndex indexing failed. Check the document and server model configuration.")
        return json.loads((directory / "result.json").read_text(encoding="utf-8"))
    except asyncio.TimeoutError:
        raise HTTPException(504, "PageIndex indexing timed out.") from None
    finally:
        if process.returncode is None:
            process.kill()
            await process.wait()


@app.post("/index")
async def index_document(request: Request, file: UploadFile = File(...),
                         include_text: bool = Form(True), include_summaries: bool = Form(False)):
    try:
        name = (file.filename or "").replace("\\", "/").split("/")[-1]
        extension = Path(name).suffix.lower()
        if extension not in {".pdf", ".md", ".markdown"}:
            raise HTTPException(415, "Upload a PDF or UTF-8 Markdown file.")
        async with request.app.state.slots:
            with tempfile.TemporaryDirectory(prefix="pageindex-") as temporary:
                directory = Path(temporary)
                source = directory / ("source" + extension)
                size = 0
                with source.open("wb") as target:
                    while chunk := await file.read(1024 * 1024):
                        size += len(chunk)
                        if size > request.app.state.max_bytes:
                            raise HTTPException(413, "File exceeds the configured size limit.")
                        target.write(chunk)
                if size == 0:
                    raise HTTPException(422, "The document is empty.")
                if extension == ".pdf":
                    with source.open("rb") as content:
                        if content.read(5) != b"%PDF-":
                            raise HTTPException(422, "The document does not have a PDF header.")
                else:
                    try:
                        text = source.read_text(encoding="utf-8-sig")
                    except UnicodeDecodeError:
                        raise HTTPException(422, "Markdown must be UTF-8 encoded.") from None
                    if not text.strip():
                        raise HTTPException(422, "The document is empty.")
                    source.write_text(text, encoding="utf-8")
                (directory / "request.json").write_text(json.dumps({
                    "source": source.name, "name": Path(name).stem,
                    "include_text": include_text, "include_summaries": include_summaries,
                    "model": os.environ.get("PAGEINDEX_INDEX_MODEL", "gpt-4.1-mini"),
                }), encoding="utf-8")
                return await run_worker(directory, request.app.state.timeout)
    finally:
        await file.close()
