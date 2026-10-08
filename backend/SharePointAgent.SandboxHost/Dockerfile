# One image for both isolated-execution targets: an Azure Container Apps Dynamic Sessions
# custom-container pool, and the disk image of an Azure Container Apps sandbox. Build from the
# repository root:  docker build -f backend/SharePointAgent.SandboxHost/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source
COPY ["backend/SharePointAgent.SandboxHost/SharePointAgent.SandboxHost.csproj", "backend/SharePointAgent.SandboxHost/"]
RUN dotnet restore "backend/SharePointAgent.SandboxHost/SharePointAgent.SandboxHost.csproj"
COPY ["backend/SharePointAgent.SandboxHost/", "backend/SharePointAgent.SandboxHost/"]
RUN dotnet publish "backend/SharePointAgent.SandboxHost/SharePointAgent.SandboxHost.csproj" \
    --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

# Node 22 rather than Ubuntu's Node 18, for module detection: a .js script may use require() or import.
FROM node:22-bookworm-slim AS node

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
COPY --from=build /usr/share/powershell /usr/share/powershell
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3 python3-venv bash ca-certificates curl git jq unzip zip libatomic1 \
    && rm -rf /var/lib/apt/lists/* \
    && ln -s /usr/share/powershell/pwsh /usr/bin/pwsh \
    && ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

# A virtual environment owned by the session user, so scripts can pip install without root.
COPY ["backend/SharePointAgent.SandboxHost/requirements.txt", "/opt/sandbox/requirements.txt"]
RUN python3 -m venv /opt/venv \
    && /opt/venv/bin/pip install --no-cache-dir -r /opt/sandbox/requirements.txt \
    && mkdir -p /workspace \
    && chown -R $APP_UID /opt/venv /workspace
ENV PATH="/opt/venv/bin:$PATH" \
    ASPNETCORE_HTTP_PORTS=8080 \
    Sandbox__WorkspaceRoot=/workspace

WORKDIR /app
COPY --from=build /app/publish .
# Non-root: the Hyper-V or microVM boundary is the real isolation, but untrusted code has no business
# being root inside it either. Port 8080 and /health serve the pool's ingress and probes, or a sandbox
# port. Sandbox__ApiKey is required unless the deployment sets Sandbox__RequireApiKey=false.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SharePointAgent.SandboxHost.dll"]
