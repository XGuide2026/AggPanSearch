# AggPanSearchAdmin 线上部署说明（Windows + IIS，环境文件机制）

本文档描述 Release 版本的出包、上传、数据库配置与启动验证全流程。**核心机制：同一份发布包通过 `ASPNETCORE_ENVIRONMENT` 自动区分本地/线上配置，线上零手工配置。**

## 1. 环境信息

| 项 | 值 |
| --- | --- |
| 线上服务器 | Windows + IIS |
| IIS 应用池 | `AggPanSearchAdmin`（Admin）、`AggPanSearchApi`（Api） |
| 数据库 | MySQL，库名 `aggpansearch`，用户名 `aggpansearch`，密码 `aggpansearch`，端口 3306 |
| 目标框架 | net10.0 |
| 发布源目录 | `Publish/Admin`、`Publish/Api`（Release 编译产物，**含 appsettings.json / appsettings.Development.json / appsettings.Production.json 全套配置**） |
| 发布包 | `Publish/Publish.zip`（zip 压缩，顶层含 Admin、Api 两目录） |

> 数据库表结构调整（pascal_case 迁移）已由线上处理完毕，本发布不涉及迁移脚本执行。

## 2. 配置加载机制（核心）

ASP.NET Core 应用启动时按 `appsettings.json` + `appsettings.{ASPNETCORE_ENVIRONMENT}.json` 的顺序合并加载配置（后者同名键覆盖前者）。本方案依赖该机制：

| 环境 | 触发方式 | 生效配置 |
| --- | --- | --- |
| **线上（Production）** | IIS 应用池**未设置** `ASPNETCORE_ENVIRONMENT`，框架默认即为 `Production` | 自动加载 `appsettings.Production.json`，数据库连接指向线上库 `aggpansearch` |
| **本地开发（Development）** | Visual Studio 启动时由 `launchSettings.json` 自动注入 `ASPNETCORE_ENVIRONMENT=Development` | 加载 `appsettings.Development.json`（仅覆盖日志级别），数据库仍走 `appsettings.json` 的本地连接串 |

即：**同一份发布包，本地 VS 启动用本地库，线上 IIS 部署用线上库，无需任何手工改文件。**

⚠️ **线上务必检查**：应用池/IIS 站点级别若曾被设置过 `ASPNETCORE_ENVIRONMENT=Development`，会错误加载本地配置。确认线上环境变量**未设置**或显式为 `Production`。

线上连接串内容（已在包内 `appsettings.Production.json`）：
```
server=localhost;database=aggpansearch;user=aggpansearch;password=aggpansearch;port=3306;pooling=true;max pool size=20;persist security info=True;charset=utf8mb4;
```
`server=localhost` 指 MySQL 与站点同机；若线上数据库在异机，编辑包内 `appsettings.Production.json` 的 `server` 后重启应用池即可。

## 3. 开发机出包

在仓库根目录运行一站式出包脚本（涵盖清理→编译两项目→删 web.config/Resource→标准 zip 打包）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Document\Deployment\pack-publish.ps1
```

> **不要用 `tar -a -c -f` 或 `Compress-Archive` 手工打包**：前者生成的 zip 条目带 `./` 前缀，后者使用反斜杠分隔路径，均非标准 zip 格式，Windows 自带解压/资源管理器与多数解压软件会解压失败。脚本内部用 .NET `ZipArchive` 生成严格标准 zip（正斜杠路径、UTF-8 文件名），实测 Windows 原生解压 26 秒完整还原 4230 个文件。

## 4. 上传至线上

- 将 `Publish/Publish.zip` 传输到线上服务器（FTP / SCP / 共享盘均可），用 Windows 资源管理器右键“全部解压缩”直接解压（原生支持），或 WinRAR / 7-Zip 解压，得到 `Admin`、`Api` 两个目录。
- 将 `Admin` 内文件整体覆盖到线上 Admin 站点根目录，`Api` 内文件整体覆盖到线上 Api 站点根目录。
- **不要覆盖**线上已有的 `web.config`（IIS 托管配置由线上维护）；本包本身就不含 web.config。
- 确认包内 `appsettings.Production.json` 已随覆盖进入站点目录（核验项）。
- 在 IIS 管理器中回收/重启 `AggPanSearchAdmin`、`AggPanSearchApi` 应用池。

## 5. 启动与验证

重启应用池后按清单验证：

1. **Admin 登录**：浏览器访问 Admin 站点，正常登录、菜单数据从 `aggpansearch` 库读取。
2. **Api 连通**：访问 Api 站点，接口返回正常、无数据库连接错误（查看应用日志确认连接串加载成功）。
3. **WebSearch 接口**：调用 `/api/other/web_search`（如搜索“周杰伦”）能返回合并结果，确认 AggPanSearch 服务连通。
4. **数据库**：在 MySQL 中确认连接来自新版本进程，`SysUserLog` 等表有即时写入。
5. **配置核验**（可选）：线上站点 `appsettings.Production.json` 存在且连接串含 `database=aggpansearch`。

## 6. 线上按需调整项

| 项 | 位置 | 默认 | 说明 |
| --- | --- | --- | --- |
| `AllowCorsSite` | Api appsettings | `http://localhost:5000` | 改为线上 Admin 实际站点地址，否则 Admin 访问 Api 被跨域拒绝 |
| `ApiSite` | Admin appsettings | `http://localhost:5001` | Admin 调用 Api 的地址，按线上调整 |
| `VirtualDirectory` | Admin appsettings | `/admin` | IIS 虚拟目录，按实际绑定调整 |
| `CacheProvider` | 两者 | `Memory` | 如已部署 Redis，可改为 `Redis` 并填 `RedisConnectionString` |
| `AggPanSearch.Proxy` | Admin appsettings | 空（直连） | 国内服务器 TG 搜索通常需配置代理，否则 t.me 搜不到结果 |
| `SystemConfig.DBConnectionString` | 两者 `appsettings.Production.json` | `server=localhost;database=aggpansearch;...` | 线上改库只需编辑该文件并重启应用池 |

> 除 DBConnectionString 推荐在 `appsettings.Production.json` 按线上调整外，其余项可直接编辑对应 appsettings 文件（对本地开发不生效）或本地源码配置后重新出包。

## 7. 备用示例配置（手工覆盖用）

`Document/Deployment/` 下另保留两份完整线上示例：
- `appsettings.Admin.online.example.json`
- `appsettings.Api.online.example.json`

它们是“完整 appsettings.json + 线上连接串”的形态，仅在需要**整体替换**站点 `appsettings.json`（非环境文件机制场景）时作为参考使用；使用环境文件机制时无需它们。