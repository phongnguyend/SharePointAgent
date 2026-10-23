# Ollaya serving the winnow:e4b decision model on a serverless T4 GPU in Azure Container Apps.
# Build from this folder:  docker build -t ollaya .
#
# The cuda12 variant ships the CUDA 12 runtime, which runs on both the 570 driver Container Apps
# serverless GPUs use today and the 580 driver they are moving to; the plain cuda tag needs 580+.
ARG OLLAYA_IMAGE=ghcr.io/ollaya-dev/ollaya:0.12.0-cuda12
FROM ${OLLAYA_IMAGE}

ARG OLLAYA_MODEL=winnow:e4b

# The base image declares /home/ollaya/.ollaya as a volume, and files written under a volume path
# during a build may not reach the image, so the model store lives outside it.
USER root
RUN mkdir -p /opt/ollaya/models && chown 1000:1000 /opt/ollaya/models
USER 1000:1000
ENV OLLAYA_MODELS=/opt/ollaya/models

# Bake the weights (about 8 GB) into the image, so a replica scaling up from zero loads them from
# local disk instead of downloading them from the Ollaya registry on every start.
RUN set -e; \
    ollaya serve > /tmp/ollaya-build.log 2>&1 & server=$!; \
    attempts=0; \
    until ollaya list > /dev/null 2>&1; do \
        attempts=$((attempts + 1)); \
        if [ "$attempts" -gt 60 ]; then cat /tmp/ollaya-build.log; exit 1; fi; \
        sleep 1; \
    done; \
    ollaya pull "$OLLAYA_MODEL"; \
    ollaya list; \
    kill "$server"; \
    rm -f /tmp/ollaya-build.log

# Keep the model on the GPU for the life of the replica; scale-to-zero is what releases it.
# OLLAYA_API_KEY is supplied at deployment; without it the server answers anyone who can reach it.
ENV OLLAYA_KEEP_ALIVE=-1

EXPOSE 11435
