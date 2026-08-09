# --- Build stage ---
FROM node:24-alpine AS build
WORKDIR /app

# Install the dependencies first (to make use of layer caching)
COPY package.json package-lock.json ./
RUN npm ci

# Copy the source and build the static assets
COPY . .
RUN npm run build

# --- Runtime stage: nginx serves the static assets and reverse-proxies /api ---
FROM nginx:alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80
