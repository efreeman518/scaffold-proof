# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /source

# Copy solution + package management files for restore-layer caching
COPY TaskFlow.slnx Directory.Build.props Directory.Packages.props global.json nuget.config ./

# Copy the full project graph so restore cannot succeed locally but fail in the container from a missing transitive project.
COPY src/ src/

RUN --mount=type=secret,id=nuget_credentials,required=true \
    env "NuGetPackageSourceCredentials_EFGithub=$(cat /run/secrets/nuget_credentials)" \
    dotnet restore src/Host/TaskFlow.Functions/TaskFlow.Functions.csproj

RUN dotnet publish src/Host/TaskFlow.Functions/TaskFlow.Functions.csproj -c Release -o /app --no-restore

# Runtime - Azure Functions isolated worker
# D-047: -chiseled-extra, not plain -chiseled. This host sets InvariantGlobalization=false and plain
# chiseled ships no ICU, so the runtime would fail at startup with no valid ICU package found.
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute-chiseled-extra@sha256:cea9d5494f358fe43082300c24d1cd9f651066b6b7de1e5735bca8d3ade68649 AS runtime
WORKDIR /app
EXPOSE 8080
ENV AzureWebJobsScriptRoot=/app \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "TaskFlow.Functions.dll"]
