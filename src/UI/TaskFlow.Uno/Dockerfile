# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /source

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python-is-python3 \
    && rm -rf /var/lib/apt/lists/*

COPY TaskFlow.slnx Directory.Build.props Directory.Packages.props global.json nuget.config ./
COPY src/ src/

RUN --mount=type=secret,id=nuget_credentials,required=true \
    env "NuGetPackageSourceCredentials_efreeman518-github=$(cat /run/secrets/nuget_credentials)" \
    dotnet workload install wasm-tools \
    && dotnet restore src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj -p:Configuration=Release \
    && dotnet publish src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj \
      -f net10.0-browserwasm -p:TargetFrameworkOverride=net10.0-browserwasm \
      -p:EnableUnoWasm=true -c Release -p:PublishDir=/app -m:1 --no-restore

FROM nginxinc/nginx-unprivileged:1.29.8-alpine@sha256:0c79d56aee561a1d81c63f00eee5fb5fe29279560cdc55e91425133104c7fbe6 AS runtime
USER root
COPY --from=build /app/wwwroot/ /usr/share/nginx/html/
COPY deploy/compose/static/default.conf /etc/nginx/conf.d/default.conf
COPY deploy/compose/static/app-config.json.template /etc/nginx/templates/app-config.json.template
RUN install -d -o 101 -g 101 /var/cache/nginx/app-config
COPY --chown=101:101 deploy/compose/static/app-config.json.template /var/cache/nginx/app-config/app-config.json
ENV NGINX_ENVSUBST_OUTPUT_DIR=/var/cache/nginx/app-config
USER 101
