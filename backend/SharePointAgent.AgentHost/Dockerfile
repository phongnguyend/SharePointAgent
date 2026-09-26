FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source
COPY ["backend/SharePointAgent.Domain/SharePointAgent.Domain.csproj", "backend/SharePointAgent.Domain/"]
COPY ["backend/SharePointAgent.Application/SharePointAgent.Application.csproj", "backend/SharePointAgent.Application/"]
COPY ["backend/SharePointAgent.Persistence/SharePointAgent.Persistence.csproj", "backend/SharePointAgent.Persistence/"]
COPY ["backend/SharePointAgent.Infrastructure/SharePointAgent.Infrastructure.csproj", "backend/SharePointAgent.Infrastructure/"]
COPY ["backend/SharePointAgent.AgentHost/SharePointAgent.AgentHost.csproj", "backend/SharePointAgent.AgentHost/"]
RUN dotnet restore "backend/SharePointAgent.AgentHost/SharePointAgent.AgentHost.csproj"
COPY backend/SharePointAgent.Domain/ backend/SharePointAgent.Domain/
COPY backend/SharePointAgent.Application/ backend/SharePointAgent.Application/
COPY backend/SharePointAgent.Persistence/ backend/SharePointAgent.Persistence/
COPY backend/SharePointAgent.Infrastructure/ backend/SharePointAgent.Infrastructure/
COPY backend/SharePointAgent.AgentHost/ backend/SharePointAgent.AgentHost/
RUN dotnet publish "backend/SharePointAgent.AgentHost/SharePointAgent.AgentHost.csproj" \
    --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
RUN apt-get update && apt-get install -y --no-install-recommends libgsf-1-114 libgmime-3.0-0t64 libcurl4t64 libssl3t64 libsecret-1-0 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8088
EXPOSE 8088
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SharePointAgent.AgentHost.dll"]
