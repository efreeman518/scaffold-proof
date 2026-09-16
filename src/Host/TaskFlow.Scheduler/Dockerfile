# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /source

# Copy solution + package management files for restore-layer caching
COPY TaskFlow.slnx Directory.Build.props Directory.Packages.props global.json nuget.config ./

# Copy the full project graph so restore cannot succeed locally but fail in the container from a missing transitive project.
COPY src/ src/

RUN --mount=type=secret,id=nuget_credentials,required=true \
    env "NuGetPackageSourceCredentials_efreeman518-github=$(cat /run/secrets/nuget_credentials)" \
    dotnet restore src/Host/TaskFlow.Scheduler/TaskFlow.Scheduler.csproj

RUN dotnet publish src/Host/TaskFlow.Scheduler/TaskFlow.Scheduler.csproj -c Release -o /app --no-restore

# Runtime - no port exposed (background worker)
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra@sha256:6385dc0eaef704fad88d3f65c334e791a371bbe448f52ca39d83d2df49251e28 AS runtime
WORKDIR /app
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "TaskFlow.Scheduler.dll"]
