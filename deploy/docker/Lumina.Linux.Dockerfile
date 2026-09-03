FROM node:24-alpine AS build
WORKDIR /app

COPY src/Websites/Lumina.Linux/package.json src/Websites/Lumina.Linux/package-lock.json ./
RUN npm ci

COPY src/Websites/Lumina.Linux/ ./
RUN npm run build

FROM nginx:alpine AS final
WORKDIR /usr/share/nginx/html

COPY --from=build /app/dist .
COPY deploy/nginx/linux-web.conf /etc/nginx/conf.d/default.conf

EXPOSE 80
