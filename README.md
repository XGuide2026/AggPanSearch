# AggPanSearch

AggPanSearch 是一个**网盘资源聚合搜索平台**：后端移植开源项目 [fish2018/pansou](https://github.com/fish2018/pansou) 的 TG 频道搜索核心（C# 版），并扩展了插件化全网搜索、并发调度、结果归并、网盘类型识别与二级缓存；前台提供面向普通访客的网盘资源搜索站（基于 guhua-xinyue-search 二开），管理后台基于 YiShaAdmin 框架实现资源维护、TG 频道管理、采集任务、版权投诉审核等运营功能。

> 技术栈：.NET 10 / ASP.NET Core MVC + Razor / MySQL（兼容 SQLServer、Oracle）/ Memory 或 Redis 缓存 / Quartz 定时任务

---

## 功能总览

### 用户前台（面向访客）

| 功能 | 说明 |
| --- | --- |
| **全网资源搜索** | 一次输入关键词，并发搜索多个 TG 频道并聚合结果，按网盘类型分组返回；前后端均可通过 `/api/other/web_search` 调用（`AggPanSearchService`，`SourceTypes.All` + `ResultTypes.Merge`） |
| **按网盘类型筛选** | 结果按夸克 / 百度 / 阿里 / UC / 迅雷 / 115 / 123 / 天翼等类型分组；可只查看指定网盘类型（`is_type` 参数） |
| **结果去重 / 归并 / 排序** | 跨频道同一资源自动归并，屏蔽空结果，按频道与时间排序（`SearchEngine`） |
| **资源详情页** | 展示介绍、图片轮播、网盘链接与提取码、相关资源；SEO 伪静态地址 `/d/{id}.html` |
| **资源分类浏览** | 左侧分类（电影 / 电视剧 / 动漫 / 综艺 / 短剧等），右侧分类资源列表，支持分页 |
| **链接有效性检测** | 详情页/搜索页对网盘分享链接调用 `/api/pancheck` 真实探测（夸克/百度/阿里/UC/天翼/123/迅雷/115 官方接口），有效 / 失效 / 需提取码三态展示 |
| **屏蔽词过滤** | 人工屏蔽词 + 版权投诉关键词双重过滤，默认必中即拦截；关键词命中屏蔽词时毫秒级返回空结果 |
| **版权投诉** | 页脚入口提供「版权保护与侵权投诉」，图形验证码 + 证明材料上传，同 IP 限频 |
| **屏蔽词公示** | `/banned_words` 页面公示参与过滤的关键词 |
| **站点地图 / SEO** | `sitemap.xml`（栏目+分类+全量详情页）、`robots.txt`、可视化地图页 `/sitemap` |
| **主题切换** | 前台与后台统一的站点主题（red / blue / green / dark），后台可一键切换 |
| **搜索统计** | 首页/接口展示今日搜索量、总搜索量、平均搜索耗时（来自 `pan_search_log`，进程内缓存） |

### 管理后台（网盘管理 / 搜索设置）

| 模块 | 说明 |
| --- | --- |
| **资源管理** | 本地网盘资源增删改查、分类挂载、多图轮播（`vod_pic`，最多 5 张）、访问量；**夸克链接事实清单**（只读分享页、不消耗 AI token）；**AI 改写资源介绍**（基于链接事实生成，不落库）；**自动截图**（无头浏览器给夸克资源截图，追加到轮播图，支持进度/取消） |
| **TG 频道管理** | 每个频道一条记录，可单独**启停 / 分组 / 排序**；**频道检测**（是否可访问 + 最近一次发布网盘资源的时间，回写 `last_update`）；一键**重置系统默认频道**；变更后前台搜索频道缓存即时失效 |
| **采集资源** | 从网盘资源站**采集**资源：详情页 / 单个列表 / 分页列表三种模式；站点信息存 JSON 文件，支持站点分类 ↔ 分类资源映射、自动抓取站点分类候选；**转存到夸克网盘**（生成永久无码分享链接写入本地资源）；**补图任务**（原图下载到本机回写本地地址）；均支持后台任务进度查询与取消 |
| **资源分类** | 前台分类树维护（名称、排序、状态、图标） |
| **搜索设置** | 搜索项配置、**屏蔽词过滤**、**搜索插件**（可查看运行时已注册插件）、**网盘类型启停**（与前台共用 `PanCloudTypeRegistry`）；保存后前台搜索配置即时生效 |
| **搜索日志** | 关键词、来源 IP、是否缓存、结果数、各网盘结果分布、耗时；今日/总量统计与清空 |
| **版权投诉审核** | 查看前台投诉（含证明材料），批量**通过 / 驳回**（必填备注）；审核通过后作品名自动写入版权关键词，前台搜索即时过滤 |
| **参数配置 / 主题** | 全站 `pan_conf` 参数维护；站点主题一键切换 |

### 其他（YiShaAdmin 框架内置）

员工 / 部门 / 职位 / 角色 / 菜单 / 通用字典 / 行政区划 / 系统日志（登录、操作、Api）/ **Quartz 定时任务** / Swagger Api 文档与测试 / 数据表管理 / 代码生成 / 服务器信息。

---

## 核心设计

### 搜索链路（全网搜索）

```
前台输入关键词
  └─ /api/other/web_search (UserFrontendApiController)
       ├─ 读取「搜索设置」（屏蔽词 / 搜索插件 / 网盘类型，60s 进程内缓存）
       ├─ 读取启用中的 TG 频道（pan_search_channel.status=1，60s 缓存）
       ├─ 屏蔽词预判（命中直接返回空，毫秒级）
       └─ AggPanSearchService.SearchAsync
            ├─ TgChannelSource：抓取各 TG 频道 t.me/s/{channel}?q={keyword}
            ├─ TgParser：解析标题/链接/密码/日期，跳过频道简介与无资源行
            ├─ 插件源：GenericJsonNetdiskPlugin 等第三方搜索接口
            ├─ SearchEngine：并发调度（上限 16，可配）、结果归并去重、按网盘类型分组
            └─ 二级缓存（进程内 Memory，可配 Redis），空结果不缓存
```

### 网盘类型识别

- `NetDiskDetector` 从链接识别网盘平台（夸克/百度/阿里/UC/迅雷/115/123/天翼/移动等），并抽取分享码/提取码
- `PanCloudTypeRegistry` 统一维护 类型值 ↔ 类型名 ↔ 前端 `is_type` 数值 的映射，后台配置与前台展示同源

### 链接有效性检测

- `PanLinkCheckService`（移植自 pansou 的 check_service.go）：按平台调用官方公开接口 / 页面获取真实元数据判断有效 / 失效 / 需提取码
- 内置进程内 TTL 缓存、在途请求合并、信号量限流（最大 4 并发）

---

## 快速开始

1. 新建数据库并导入表结构（或使用项目自带 SQL 脚本）：

   ```sql
   CREATE DATABASE aggpansearch DEFAULT CHARACTER SET utf8mb4;
   ```

2. 修改连接串：`AggPanSearch.Web/AggPanSearch.Admin.Web/appsettings.json` 的 `SystemConfig:DBConnectionString`

   ```json
   "DBConnectionString": "server=localhost;database=aggpansearch;user=root;password=123456;port=3306;pooling=true;charset=utf8mb4;"
   ```

   - 线上环境可编辑 `appsettings.Production.json` 仅覆盖连接串（该文件已被 `.gitignore` 忽略，不提交敏感信息）

3. 启动两个 Web 项目：

   | 项目 | 说明 | 默认地址 |
   | --- | --- | --- |
   | `AggPanSearch.Admin.Web` | 管理后台 + 用户前台 | `http://localhost:5000` |
   | `AggPanSearch.Admin.WebApi` | 独立 Api（WebApi 登录、跨站） | `http://localhost:5001` |

4. 如需启用 TG 全网搜索，在 `AggPanSearch:Proxy` 填入代理地址（国内直连 t.me 通常不通，会导致 0 结果）：

   ```json
   "AggPanSearch": {
     "Proxy": "http://127.0.0.1:7890",
     "Concurrency": 0,
     "AsyncResponseTimeoutSeconds": 4.0,
     "CacheTtlMinutes": 60
   }
   ```

---

## 环境要求

- **运行时**：.NET 10 SDK（net10.0，LTS）
- **数据库**：MySQL 8.x（默认）；工程同时兼容 SQLServer / Oracle，通过 `SystemConfig:DBProvider` 切换
- **缓存**：默认进程内 Memory；如配置 `CacheProvider: Redis`，需另备 Redis 服务
- **截图 / 采集补图**（可选，用于后台「自动截图」与「采集-补图」任务）：服务器需安装 Chromium，在发布目录执行：

  ```bash
  playwright install chromium
  ```

  > Playwright 版本固定 1.61.0（对应 Chromium 1228）；升级该包后必须重新执行 `playwright install chromium`，并确保服务器磁盘足够。
- **任务调度**：Quartz 在线定时任务，内置执行结果日志
- **网络（可选）**：TG 频道全网搜索需代理（见上），`AggPanSearch:Proxy` 支持 `http://` 或 `socks5://`

---

## 解决方案结构

```
AggPanSearch.sln
├─ AggPanSearch                # 核心搜索库（pansou C# 移植）
│   ├─ Sources/                #   TG 频道源、解析器、频道探测、插件源
│   ├─ Engine/                 #   搜索引擎：并发调度、归并去重、排序、缓存
│   ├─ Detection/              #   网盘类型识别、链接规范化、提取码抽取
│   ├─ Models/                 #   搜索请求/响应/结果模型
│   └─ Cache/                  #   二级缓存抽象
├─ AggPanSearch.Business       # 业务逻辑层（资源、频道、采集、分类、投诉、日志等）
├─ AggPanSearch.Data           # EF Core 数据访问（MySQL/SqlServer/Oracle）
├─ AggPanSearch.Entity         # 实体 / 枚举 / 模型
├─ AggPanSearch.Cache          # Redis / Memory 缓存实现
├─ AggPanSearch.Util           # 公共工具、代码生成、雪花 ID
├─ AggPanSearch.AI             # AI 能力（资源介绍改写等）
├─ AggPanSearch.CollectSite    # 资源站采集引擎
└─ AggPanSearch.Web
    ├─ AggPanSearch.Admin.Web  # 管理后台 + 用户前台（MVC/Razor）
    └─ AggPanSearch.Admin.WebApi
```

---

## License

[MIT](https://github.com/XGuide2026/AggPanSearch)