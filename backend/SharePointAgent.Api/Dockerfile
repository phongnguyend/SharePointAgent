FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source

COPY ["backend/SharePointAgent.Domain/SharePointAgent.Domain.csproj", "backend/SharePointAgent.Domain/"]
COPY ["backend/SharePointAgent.Application/SharePointAgent.Application.csproj", "backend/SharePointAgent.Application/"]
COPY ["backend/SharePointAgent.Persistence/SharePointAgent.Persistence.csproj", "backend/SharePointAgent.Persistence/"]
COPY ["backend/SharePointAgent.Infrastructure/SharePointAgent.Infrastructure.csproj", "backend/SharePointAgent.Infrastructure/"]
COPY ["backend/SharePointAgent.Api/SharePointAgent.Api.csproj", "backend/SharePointAgent.Api/"]
RUN dotnet restore "backend/SharePointAgent.Api/SharePointAgent.Api.csproj"

COPY . .
RUN dotnet publish "backend/SharePointAgent.Api/SharePointAgent.Api.csproj" \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
COPY --from=build /usr/share/powershell /usr/share/powershell
RUN apt-get update && apt-get install -y --no-install-recommends python3 dnsutils libatomic1 libgsf-1-114 libgmime-3.0-0t64 libcurl4t64 libssl3t64 libsecret-1-0 && rm -rf /var/lib/apt/lists/* \
    && ln -s /usr/share/powershell/pwsh /usr/bin/pwsh
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SharePointAgent.Api.dll"]
