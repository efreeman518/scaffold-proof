FROM node:24.21.0-alpine@sha256:be80f76cf40ec8e42b9bec49f60a55e0660f30af58d3e5a25530785b30ea67e2 AS build
WORKDIR /src
RUN npm install --global npm@12.0.2
COPY src/UI/TaskFlow.React/package*.json ./
RUN npm ci
COPY src/UI/TaskFlow.React/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.29.8-alpine@sha256:0c79d56aee561a1d81c63f00eee5fb5fe29279560cdc55e91425133104c7fbe6 AS runtime
USER root
COPY --from=build /src/dist/ /usr/share/nginx/html/
COPY deploy/compose/static/default.conf /etc/nginx/conf.d/default.conf
COPY deploy/compose/static/app-config.json.template /etc/nginx/templates/app-config.json.template
RUN install -d -o 101 -g 101 /var/cache/nginx/app-config
COPY --chown=101:101 deploy/compose/static/app-config.json.template /var/cache/nginx/app-config/app-config.json
ENV NGINX_ENVSUBST_OUTPUT_DIR=/var/cache/nginx/app-config
USER 101
