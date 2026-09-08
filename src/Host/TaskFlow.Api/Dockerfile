# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Copy solution + package management files for restore-layer caching
COPY TaskFlow.slnx Directory.Build.props Directory.Packages.props global.json nuget.config ./

# Copy the full project graph so restore cannot succeed locally but fail in the container from a missing transitive project.
COPY src/ src/

RUN --mount=type=secret,id=nuget_credentials,required=true \
    env "NuGetPackageSourceCredentials_efreeman518-github=$(cat /run/secrets/nuget_credentials)" \
    dotnet restore src/Host/TaskFlow.Api/TaskFlow.Api.csproj -r linux-x64

# D-047: ReadyToRun AOT-precompiles the IL this host JITs on its first requests, which is what a
# scale-out replica pays for on every cold start. Framework-dependent (--self-contained false) so the
# chiseled aspnet base still supplies the shared framework and the image only grows by the R2R native
# code. -r linux-x64 is required for R2R and must be on the restore above too, or --no-restore here
# fails with no assets for net10.0/linux-x64.
RUN dotnet publish src/Host/TaskFlow.Api/TaskFlow.Api.csproj -c Release -r linux-x64 --self-contained false \
    -p:PublishReadyToRun=true -o /app --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
# D-054: 8080 REST/HTTP1.1, 8081 cleartext HTTP/2 for the internal gRPC read service. Both listeners are
# declared in appsettings Kestrel:Endpoints, and no ASPNETCORE_URLS is set here on purpose: with both
# present Kestrel binds the configured endpoints and logs an "overriding address(es)" warning, so the
# environment variable would be dead weight that only obscures where the ports come from.
EXPOSE 8080
EXPOSE 8081
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "TaskFlow.Api.dll"]
