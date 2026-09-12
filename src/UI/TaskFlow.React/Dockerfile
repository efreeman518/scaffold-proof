FROM node:22-alpine AS build
WORKDIR /src
COPY src/UI/TaskFlow.React/package*.json ./
RUN npm ci
COPY src/UI/TaskFlow.React/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.29-alpine AS runtime
USER root
COPY --from=build --chown=101:101 /src/dist/ /usr/share/nginx/html/
COPY --chown=101:101 deploy/compose/static/default.conf /etc/nginx/conf.d/default.conf
COPY --chown=101:101 deploy/compose/static/app-config.json.template /etc/nginx/templates/app-config.json.template
RUN install -d -o 101 -g 101 /var/cache/nginx/app-config
COPY --chown=101:101 deploy/compose/static/app-config.json.template /var/cache/nginx/app-config/app-config.json
ENV NGINX_ENVSUBST_OUTPUT_DIR=/var/cache/nginx/app-config
USER 101
