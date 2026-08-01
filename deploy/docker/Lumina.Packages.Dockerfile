FROM node:24-alpine AS build
WORKDIR /app

COPY src/Websites/Lumina.Packages/package.json src/Websites/Lumina.Packages/package-lock.json ./
RUN npm ci

COPY src/Websites/Lumina.Packages/ ./
RUN npm run build

FROM nginx:alpine AS final
WORKDIR /usr/share/nginx/html

COPY --from=build /app/dist .
COPY deploy/nginx/packages-web.conf /etc/nginx/conf.d/default.conf

EXPOSE 80
