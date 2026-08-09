# syntax=docker/dockerfile:1
# Multi-arch image (linux/amd64 + linux/arm64). The backend is published framework-dependent
# (portable IL); the runtime uses the aspnet base image of the target architecture plus the
# official 7-Zip, and also serves the built frontend static assets.
# Build: docker buildx build --platform linux/amd64,linux/arm64 -t <image> --push .

# ---- 1. Frontend (architecture-independent, always compiled on the build platform) ----
FROM --platform=$BUILDPLATFORM node:22-bookworm-slim AS frontend
WORKDIR /fe
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build   # → /fe/dist

# ---- 2. Backend publish (compiled on the build platform; the output is portable IL and runs on any architecture) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/src/ ./backend/src/
RUN dotnet restore backend/src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj
RUN dotnet publish backend/src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false
# Fold the frontend static assets into wwwroot (the backend serves them same-origin)
COPY --from=frontend /fe/dist /app/publish/wwwroot

# ---- 3. Official 7-Zip (latest official release for the target architecture) ----
# The distro-packaged p7zip is unusable: it (and 7-Zip 23.01 too) writes entry attributes as 0
# when compressing from stdin, so the extracted files come out as ----------. Single-file blobs
# take exactly this path, so the backup looks perfectly fine right up until restore time, when
# the files turn out to be unreadable. The attributes are baked into the archive; extracting
# with another version cannot rescue them.
# The extraction side already patches its output back to readable (see
# SevenZipCompressor.EnsureReadable); this is the other half: stop producing such archives.
FROM --platform=$BUILDPLATFORM debian:bookworm-slim AS sevenzip
ARG TARGETARCH
# Empty = take the latest version number from the official download page; to pin one specific
# version, pass --build-arg SEVENZIP_VERSION=2602.
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
      # The download page lists the historical versions alongside, so take the numerically largest one.
      ver="$(curl -fsSL https://www.7-zip.org/download.html \
             | grep -oE '7z[0-9]{4}-linux-x64\.tar\.xz' | grep -oE '[0-9]{4}' | sort -rn | head -1)"; \
    fi; \
    test -n "$ver"; \
    echo "7-Zip $ver ($arch)"; \
    curl -fsSL -o /tmp/7z.tar.xz "https://www.7-zip.org/a/7z${ver}-${arch}.tar.xz"; \
    mkdir -p /out; \
    tar -xJf /tmp/7z.tar.xz -C /out 7zz; \
    test -s /out/7zz

# ---- 4. Runtime (pulls aspnet for the target architecture) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
COPY --from=sevenzip /out/7zz /usr/local/bin/7zz
WORKDIR /app
COPY --from=build /app/publish ./

# The default paths point at the volume mount points (see "Docker" in the README).
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Sqlite="Data Source=/data/app.db" \
    DataProtection__KeysPath=/keys \
    Backup__TempPath=/temp

EXPOSE 8080
VOLUME ["/data", "/keys", "/temp"]

ENTRYPOINT ["dotnet", "AzureStorageBackup.Api.dll"]
