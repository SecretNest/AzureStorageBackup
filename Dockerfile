# syntax=docker/dockerfile:1
# 多架构镜像（linux/amd64 + linux/arm64）。后端框架依赖发布（可移植 IL），
# 运行时用目标架构的 aspnet 基础镜像 + p7zip；同时托管构建后的前端静态资源。
# 构建：docker buildx build --platform linux/amd64,linux/arm64 -t <image> --push .

# ---- 1. 前端（架构无关，固定在构建平台上编译） ----
FROM --platform=$BUILDPLATFORM node:22-bookworm-slim AS frontend
WORKDIR /fe
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build   # → /fe/dist

# ---- 2. 后端发布（在构建平台编译，产物为可移植 IL，任意架构可跑） ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/src/ ./backend/src/
RUN dotnet restore backend/src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj
RUN dotnet publish backend/src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false
# 前端静态资源并入 wwwroot（后端同源托管）
COPY --from=frontend /fe/dist /app/publish/wwwroot

# ---- 3. 运行时（按目标架构拉取 aspnet + 安装 7z） ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends p7zip-full \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish ./

# 默认路径指向卷挂载点（见 README「Docker」）。
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Sqlite="Data Source=/data/app.db" \
    DataProtection__KeysPath=/keys \
    Backup__TempPath=/temp

EXPOSE 8080
VOLUME ["/data", "/keys", "/temp"]

ENTRYPOINT ["dotnet", "AzureStorageBackup.Api.dll"]
