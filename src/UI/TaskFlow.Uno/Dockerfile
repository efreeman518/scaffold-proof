# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY TaskFlow.slnx Directory.Build.props Directory.Packages.props global.json nuget.config ./
COPY src/ src/

RUN --mount=type=secret,id=nuget_credentials,required=true \
    env "NuGetPackageSourceCredentials_efreeman518-github=$(cat /run/secrets/nuget_credentials)" \
    dotnet workload install wasm-tools \
    && dotnet restore src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj -p:Configuration=Release \
    && dotnet publish src/UI/TaskFlow.Uno/TaskFlow.Uno.csproj \
      -f net10.0-browserwasm -p:TargetFrameworkOverride=net10.0-browserwasm \
      -p:EnableUnoWasm=true -c Release -p:PublishDir=/app -m:1 --no-restore

FROM nginxinc/nginx-unprivileged:1.29-alpine AS runtime
USER root
COPY --from=build --chown=101:101 /app/wwwroot/ /usr/share/nginx/html/
COPY --chown=101:101 deploy/compose/static/default.conf /etc/nginx/conf.d/default.conf
COPY --chown=101:101 deploy/compose/static/app-config.json.template /etc/nginx/templates/app-config.json.template
RUN install -d -o 101 -g 101 /var/cache/nginx/app-config
COPY --chown=101:101 deploy/compose/static/app-config.json.template /var/cache/nginx/app-config/app-config.json
ENV NGINX_ENVSUBST_OUTPUT_DIR=/var/cache/nginx/app-config
USER 101
