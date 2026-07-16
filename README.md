# Azure Storage Backup

将本地文件备份到 Azure Storage Account 的应用。单用户、无身份验证。

## 技术栈

- **后端**：.NET 10 Minimal API + EF Core（SQLite）+ Azure.Storage.Blobs
- **前端**：Vite + React + TypeScript
- **部署**：Docker，前端 nginx 托管静态资源并反代 `/api` 到后端

## 目录结构

```
.
├── backend/                     # .NET 10 Minimal API
│   ├── src/AzureStorageBackup.Api/
│   │   ├── Endpoints/           # Minimal API 端点分组
│   │   ├── Services/            # 业务逻辑（Azure 存储 / 备份编排）
│   │   ├── Data/                # EF Core DbContext
│   │   ├── Models/              # 实体与 DTO
│   │   └── Program.cs           # 组装入口
│   └── tests/                   # xUnit 测试
├── frontend/                    # Vite + React + TS
│   └── src/{api,components,pages}/
├── docker-compose.yml
└── .env.example
```

## 本地开发

**后端**（端口 5122）：

```bash
cd backend
dotnet run --project src/AzureStorageBackup.Api
```

**前端**（端口 5173，`/api` 经 Vite proxy 转发到后端）：

```bash
cd frontend
npm install
npm run dev
```

## 测试

```bash
cd backend && dotnet test
```

## Docker 部署

```bash
cp .env.example .env      # 填入 AZURE_STORAGE_CONNECTION_STRING
docker compose up --build
```

启动后访问 http://localhost:8080 。前端容器（nginx）托管静态资源并把 `/api` 反代到后端容器。

## 配置

| 配置项 | 说明 | 默认 |
| --- | --- | --- |
| `ConnectionStrings__Sqlite` | SQLite 连接串 | `Data Source=data/app.db` |
| `ConnectionStrings__AzureStorage` | Azure Storage 账户连接串 | 空（回退到本地 Azurite `UseDevelopmentStorage=true`） |
| `Cors__AllowedOrigins__0` | 允许的前端来源 | `http://localhost:5173` |
| `DataProtection__KeysPath` | 敏感信息加密密钥环目录（**须持久化**，否则重启后无法解密已存密钥） | `keys` |
| `Paths__Temp` | 临时目录（备份压缩等，M4 使用） | `temp` |

### 持久化卷（docker）

| 容器路径 | 用途 | compose 卷 |
| --- | --- | --- |
| `/app/data` | SQLite 数据库 | `backend-data` |
| `/app/keys` | Data Protection 密钥环（丢失则密钥不可解） | `backend-keys` |
| `/app/temp` | 备份临时区 | `backend-temp` |

运行时可通过 `GET /api/system/paths` 查看这些路径的绝对位置（对应 PRD 第 6 章「目录」）。

## 状态

已完成：基础骨架 + **M1 账户管理**——账户 CRUD（敏感信息经 Data Protection 加密存储）、代理与分区配置、连通测试、系统路径/版本端点。

路线图见 `docs/roadmap.md`，完整需求见 `docs/product-requirements.md`。
