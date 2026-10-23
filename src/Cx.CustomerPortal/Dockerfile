FROM node:22-alpine AS build
WORKDIR /portal
COPY src/Cx.CustomerPortal/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/Cx.CustomerPortal/ ./
RUN npm run build
FROM nginx:stable-alpine
COPY src/Cx.CustomerPortal/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /portal/dist /usr/share/nginx/html
EXPOSE 8081
