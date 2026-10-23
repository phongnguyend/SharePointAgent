# Build remotely with Release Winow Native (Azure Container Registry Tasks).
# No local Docker build or model download is needed.
FROM nvidia/cuda:12.8.1-devel-ubuntu24.04 AS build
ARG WINNOW_REVISION=a50c7d0d8cbc5c3ba34987c5c23ea114e44ce6b5
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential cmake git python3 ca-certificates libssl-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
RUN git init . && git remote add origin https://github.com/EldanRing/winnow-inference.git \
    && git fetch --depth 1 origin "${WINNOW_REVISION}" && git checkout --detach FETCH_HEAD \
    && test "$(git rev-parse HEAD)" = "${WINNOW_REVISION}"
# Compile for T4 without needing a GPU on the ACR builder.
RUN python3 scripts/build.py --backend cuda --cuda-arch 75 --jobs 4

FROM python:3.12-slim-bookworm AS models
WORKDIR /models
COPY download_model.py ./
RUN python download_model.py

FROM nvidia/cuda:12.8.1-runtime-ubuntu24.04
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 ca-certificates libssl3t64 libgomp1 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 winnow && useradd --uid 10001 --gid 10001 --create-home winnow
WORKDIR /app
COPY --from=build /src/.build/bin/winnow-server /app/.build/bin/winnow-server
COPY --from=build /src/scripts /app/scripts
COPY --from=build /src/manifests /app/manifests
COPY --from=build /src/runtime.lock.json /app/runtime.lock.json
COPY --from=build /src/LICENSE /app/LICENSE
COPY --from=build /src/.runtime/llama.cpp/LICENSE /app/LLAMA-LICENSE
COPY --from=models /models/Winnow-E4B-Q8_0.gguf /opt/models/Winnow-E4B-Q8_0.gguf
COPY entrypoint.py /app/entrypoint.py
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
USER 10001:10001
EXPOSE 8091
HEALTHCHECK --interval=30s --timeout=5s --start-period=10m \
    CMD python3 -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8091/health', timeout=4)"
ENTRYPOINT ["python3", "/app/entrypoint.py"]
