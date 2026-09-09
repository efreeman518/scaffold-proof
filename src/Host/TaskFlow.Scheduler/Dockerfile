# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "TaskFlow.Scheduler.dll"]
