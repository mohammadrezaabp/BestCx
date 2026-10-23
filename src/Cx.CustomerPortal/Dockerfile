ARG NODE_IMAGE=node:22-alpine
ARG NGINX_IMAGE=nginx:stable-alpine
FROM ${NODE_IMAGE} AS build
WORKDIR /portal
COPY src/Cx.CustomerPortal/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/Cx.CustomerPortal/ ./
RUN npm run build
FROM ${NGINX_IMAGE}
COPY src/Cx.CustomerPortal/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /portal/dist /usr/share/nginx/html
EXPOSE 8081
