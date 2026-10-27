# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3@sha256:3a9175b6c54b2f076817af1360f691c9994447b192faa48a1a1f112a516844c7 AS build
WORKDIR /source

COPY TaskFlow.slnx Directory.Build.props Directory.Packages.props global.json nuget.config ./
COPY src/ src/

RUN --mount=type=secret,id=nuget_credentials,required=true \
    env "NuGetPackageSourceCredentials_efreeman518-github=$(cat /run/secrets/nuget_credentials)" \
    dotnet restore src/UI/TaskFlow.Blazor/TaskFlow.Blazor.csproj

RUN dotnet publish src/UI/TaskFlow.Blazor/TaskFlow.Blazor.csproj -c Release -o /app --no-restore

# D-047: -chiseled-extra, not plain -chiseled. This host sets InvariantGlobalization=false and plain
# chiseled ships no ICU, so the runtime would fail at startup with no valid ICU package found.
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.3-resolute-chiseled-extra@sha256:4a02e6f650d442ef3acf66932e74a2f63f3585b44e1990c3c8edd2c010298ff0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "TaskFlow.Blazor.dll"]
