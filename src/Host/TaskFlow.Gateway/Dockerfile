# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /source

# Copy solution + package management files for restore-layer caching
COPY TaskFlow.slnx Directory.Build.props Directory.Packages.props global.json nuget.config ./

# Copy the full project graph so restore cannot succeed locally but fail in the container from a missing transitive project.
COPY src/ src/

RUN --mount=type=secret,id=nuget_credentials,required=true \
    env "NuGetPackageSourceCredentials_efreeman518-github=$(cat /run/secrets/nuget_credentials)" \
    dotnet restore src/Host/TaskFlow.Gateway/TaskFlow.Gateway.csproj -r linux-x64 -p:PublishReadyToRun=true

# D-047: ReadyToRun AOT-precompiles the IL this host JITs on its first requests, which is what a
# scale-out replica pays for on every cold start. Framework-dependent (--self-contained false) so the
# chiseled aspnet base still supplies the shared framework and the image only grows by the R2R native
# code. -r linux-x64 is required for R2R and must be on the restore above too, or --no-restore here
# fails with no assets for net10.0/linux-x64.
RUN dotnet publish src/Host/TaskFlow.Gateway/TaskFlow.Gateway.csproj -c Release -r linux-x64 --self-contained false \
    -p:PublishReadyToRun=true -o /app --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "TaskFlow.Gateway.dll"]
