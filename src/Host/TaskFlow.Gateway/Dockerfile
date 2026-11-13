# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /source

# Copy solution + package management files for restore-layer caching
COPY TaskFlow.slnx Directory.Build.props Directory.Packages.props global.json nuget.config ./

# Copy the full project graph so restore cannot succeed locally but fail in the container from a missing transitive project.
COPY src/ src/

RUN --mount=type=secret,id=nuget_credentials,required=true \
    env "NuGetPackageSourceCredentials_EFGithub=$(cat /run/secrets/nuget_credentials)" \
    dotnet restore src/Host/TaskFlow.Gateway/TaskFlow.Gateway.csproj -r linux-x64 -p:PublishReadyToRun=true

# D-047: ReadyToRun AOT-precompiles the IL this host JITs on its first requests, which is what a
# scale-out replica pays for on every cold start. Framework-dependent (--self-contained false) so the
# chiseled aspnet base still supplies the shared framework and the image only grows by the R2R native
# code. -r linux-x64 is required for R2R and must be on the restore above too, or --no-restore here
# fails with no assets for net11.0/linux-x64.
RUN dotnet publish src/Host/TaskFlow.Gateway/TaskFlow.Gateway.csproj -c Release -r linux-x64 --self-contained false \
    -p:PublishReadyToRun=true -o /app --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute-chiseled@sha256:fc0ee12f9ea79bde6cda88c7a34e5467fe383dbb62276be0ec13e5e9cc1654ac AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "TaskFlow.Gateway.dll"]
