# Build remotely in Azure Container Registry Tasks; no local model downloads.
FROM python:3.11-slim-trixie AS build
ARG LAYA_REVISION=6f0496f797dc7127265d62e1543a62de507d943c
ARG TORCH_VERSION=2.14.0
ENV PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN python -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
RUN pip install "torch==${TORCH_VERSION}" --index-url https://download.pytorch.org/whl/cpu
WORKDIR /src
RUN git init . && git remote add origin https://github.com/NandhaKishorM/laya.git \
    && git fetch --depth 1 origin "${LAYA_REVISION}" && git checkout --detach FETCH_HEAD \
    && test "$(git rev-parse HEAD)" = "${LAYA_REVISION}" \
    && pip install ".[serve]" && pip check
COPY download_models.py /tmp/download_models.py
RUN python /tmp/download_models.py

FROM python:3.11-slim-trixie
ENV PATH="/opt/venv/bin:$PATH" \
    TORCH_DISABLE_NATIVE_JIT=1 PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 \
    USE_TF=0 USE_TORCH=1 TOKENIZERS_PARALLELISM=false OMP_NUM_THREADS=4 \
    LAYA_HOST=0.0.0.0 LAYA_PORT=8000 LAYA_DEVICE=cpu LAYA_THREADS=4 \
    LAYA_PRELOAD=1 LAYA_MODELS=english,multilingual LAYA_MAX_LOADED=2 \
    LAYA_AUTO_TASK=0 LAYA_DEFAULT_MODEL=multilingual LAYA_IDLE_UNLOAD_SECONDS=0 \
    LAYA_MAX_CONCURRENT=4 LAYA_MAX_TOKEN_BUDGET=2048 LAYA_MAX_BATCH_TOKENS=8192 \
    LAYA_JEV_STRICT=1 HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 \
    HF_HOME=/home/laya/.cache/huggingface \
    LAYA_EXTRA_MODELS='{"english":"/opt/models/english","multilingual":"/opt/models/multilingual"}'
RUN groupadd --gid 10001 laya && useradd --uid 10001 --gid 10001 --create-home laya
COPY --from=build /opt/venv /opt/venv
# The upstream tokenizer compatibility fix can write into the checkpoint directory.
COPY --from=build --chown=10001:10001 /opt/models /opt/models
COPY --from=build /src/LICENSE /usr/share/doc/laya/LICENSE
COPY entrypoint.py /app/entrypoint.py
USER 10001:10001
WORKDIR /home/laya
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=10m \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4)"
ENTRYPOINT ["python", "/app/entrypoint.py"]
