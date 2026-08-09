# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (to make use of layer caching)
COPY src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj src/AzureStorageBackup.Api/
RUN dotnet restore src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj

# Copy the rest of the source and publish
COPY src/ src/
RUN dotnet publish src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj \
    -c Release -o /app/publish --no-restore

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The backup engine uses the official 7-Zip for compression/encryption/splitting (M4 decision §13.1, provides 7zz)
RUN apt-get update \
    && apt-get install -y --no-install-recommends 7zip \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

# Listens on 8080 inside the container; SQLite data lands in /app/data (persisted by a volume mounted by compose)
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AzureStorageBackup.Api.dll"]
