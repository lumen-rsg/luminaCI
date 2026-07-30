FROM node:24-alpine AS build
WORKDIR /app

COPY src/Services/Lumina.WebApp/package.json src/Services/Lumina.WebApp/package-lock.json ./
RUN npm ci

COPY src/Services/Lumina.WebApp/ ./
RUN npm run build

FROM nginx:alpine AS final
WORKDIR /usr/share/nginx/html

COPY --from=build /app/dist .
COPY deploy/nginx/react.conf /etc/nginx/conf.d/default.conf

EXPOSE 80
