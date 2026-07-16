# --- 构建阶段 ---
FROM node:24-alpine AS build
WORKDIR /app

# 先装依赖（利用层缓存）
COPY package.json package-lock.json ./
RUN npm ci

# 复制源码并构建静态资源
COPY . .
RUN npm run build

# --- 运行阶段：nginx 托管静态资源 + 反代 /api ---
FROM nginx:alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80
