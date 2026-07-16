# --- 构建阶段 ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先还原（利用层缓存）
COPY src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj src/AzureStorageBackup.Api/
RUN dotnet restore src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj

# 复制其余源码并发布
COPY src/ src/
RUN dotnet publish src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj \
    -c Release -o /app/publish --no-restore

# --- 运行阶段 ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# 容器内监听 8080；SQLite 数据落在 /app/data（由 compose 挂载 volume 持久化）
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AzureStorageBackup.Api.dll"]
