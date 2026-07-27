# syntax=docker/dockerfile:1
# 多架构镜像（linux/amd64 + linux/arm64）。后端框架依赖发布（可移植 IL），
# 运行时用目标架构的 aspnet 基础镜像 + 官方 7-Zip；同时托管构建后的前端静态资源。
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

# ---- 3. 官方 7-Zip（按目标架构取最新正式版） ----
# 发行版打包的 p7zip 不能用：它（以及 7-Zip 23.01）从 stdin 压缩时把条目属性写成 0，
# 解出来的文件是 ----------。单文件 blob 走的正是这条路，备份看起来一切正常，
# 直到还原时才发现文件读不了。属性写死在归档里，换版本解压救不回来。
# 解压侧已经会把产物补成可读（见 SevenZipCompressor.EnsureReadable），这里是另一半：
# 不再产出这种归档。
FROM --platform=$BUILDPLATFORM debian:bookworm-slim AS sevenzip
ARG TARGETARCH
# 留空＝从官网下载页取最新版本号；要钉死某一版时传 --build-arg SEVENZIP_VERSION=2602。
ARG SEVENZIP_VERSION=
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl xz-utils \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    case "$TARGETARCH" in \
      amd64) arch=linux-x64 ;; \
      arm64) arch=linux-arm64 ;; \
      *) echo "unsupported TARGETARCH=$TARGETARCH" >&2; exit 1 ;; \
    esac; \
    ver="$SEVENZIP_VERSION"; \
    if [ -z "$ver" ]; then \
      # 下载页把历史版本一并列着，取数值最大的那个。
      ver="$(curl -fsSL https://www.7-zip.org/download.html \
             | grep -oE '7z[0-9]{4}-linux-x64\.tar\.xz' | grep -oE '[0-9]{4}' | sort -rn | head -1)"; \
    fi; \
    test -n "$ver"; \
    echo "7-Zip $ver ($arch)"; \
    curl -fsSL -o /tmp/7z.tar.xz "https://www.7-zip.org/a/7z${ver}-${arch}.tar.xz"; \
    mkdir -p /out; \
    tar -xJf /tmp/7z.tar.xz -C /out 7zz; \
    test -s /out/7zz

# ---- 4. 运行时（按目标架构拉取 aspnet） ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
COPY --from=sevenzip /out/7zz /usr/local/bin/7zz
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
