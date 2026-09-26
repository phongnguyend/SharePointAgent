FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SharePointAgent.Api.dll"]
