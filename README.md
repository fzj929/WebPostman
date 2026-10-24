# WebPostman

本地运行的 API 请求工作台。后端使用 .NET 8，页面使用 Vue 3，数据保存在本地 SQLite。

## 账户、本机存储与分享

| 使用方式 | 保存位置 | 分享能力 |
| --- | --- | --- |
| 未登录 | 当前浏览器的 IndexedDB；请求、环境和历史不在服务端持久化 | 浏览公开分享，复制到本机集合 |
| 已登录 | 服务端当前用户独立的工作区 | 发布请求/集合分享，复制到个人集合，撤回自己的分享 |

点击右上角“登录 / 注册”，填写邮箱、密码及确认密码即可注册并登录；密码为 8～128 个字符。邮箱用于登录，本期不发送验证邮件。密码使用 ASP.NET Core Identity 的 PasswordHasher 哈希保存，登录会话保留 7 天，退出时撤销该会话；会话凭据使用 HttpOnly、SameSite=Strict Cookie，HTTPS 下启用 Secure。

游客刷新页面后仍可继续使用本机工作区；清除浏览器数据会删除这些内容，建议使用“导出”备份。登录和退出会切换存储，未保存的草稿先保留在原存储中。本机数据不会自动上传，登录后可点击“导入本机数据”复制集合和环境到个人账户；同一版本不会反复导入，原本机数据保留。

左侧“分享”标签提供搜索、请求/集合类型筛选和“只看我的分享”，详情中可复制直达链接，打开链接可直接浏览该分享。发布前展示内容预览，常见凭据和文件内容会移除，非 JSON 原始正文默认留空；仍需检查预览中的私密值。公开内容是已保存版本的独立快照，后续修改原请求不会自动更新分享。访客可点击“存到本机集合”，登录后可点击“存到我的集合”；单个请求可以选择已有集合，集合会复制为独立集合。撤回分享不会删除其他人已经保存的副本。

所有分享仅在同一个服务实例内可见，不会自动同步到其他独立安装。多人使用时部署到同一服务地址；默认仍只监听本机。远程访问应通过 HTTPS 反向代理，并限制请求代理的网络访问范围；该工具会代用户访问所填写的 HTTP 地址，适合在可信环境中使用。默认信任本机反向代理的转发协议信息，其他代理需要按部署环境配置可信代理。

账户和分享位于 `App_Data/accounts.db`，个人工作区位于 `App_Data/users/<用户ID>/workspace.db`。升级前的匿名服务端 `workspace.db` 保留原样，不会分配给任意注册用户；请先在旧版导出，再登录目标账户导入。备份服务端时应备份整个 `App_Data`。

## 下载即用（Windows / Linux x64）

从 [Releases](https://github.com/fzj929/WebPostman/releases) 下载 `WebPostman-v版本号-win-x64.zip`，完整解压到可写目录后，双击 `Start-WebPostman.cmd`，访问 `http://127.0.0.1:5078`。便携包已包含前端页面及 .NET 运行时，无需安装 Node.js、SDK 或 .NET。保持命令窗口打开，按 Ctrl+C 停止。

登录账户与分享数据保存在解压目录的 `App_Data`（含 `accounts.db` 和 `users` 目录）；游客数据在浏览器中。更新前停止程序并备份整个 `App_Data`，游客可通过导出备份。Release 同时提供 `SHA256SUMS.txt` 用于校验下载包。需要后台运行时，可使用包内的 `scripts/install-service.ps1` 安装 Windows 服务。

Linux x64 下载 `WebPostman-v版本号-linux-x64.tar.gz`，解压后运行：

```bash
tar -xzf WebPostman-v1.0-linux-x64.tar.gz
cd WebPostman
./start-webpostman.sh
```

访问 `http://127.0.0.1:5078`，按 Ctrl+C 停止。Linux 包同样包含前端及 .NET 运行时，适用于基于 glibc 的 x64 发行版，需要系统提供 ICU、OpenSSL、zlib 等 .NET 8 原生依赖；不适用于 Alpine/musl。需要 systemd 服务时，在解压目录执行 `sudo bash scripts/install-service.sh`，安装器会使用随包附带的可执行文件。

GitHub Actions 工作流 `Build portable release` 在推送 `v*` 标签时自动构建 Windows / Linux x64 包、启动验证并上传 Release 附件。也可在 Actions 中选择该工作流，点击 **Run workflow** 并填写已有标签（例如 `v1.0`）补发安装包；应用构建使用该标签对应的源码，启动器和服务安装脚本使用工作流对应版本。

## 运行

需要 .NET 8 SDK、Node.js 20.19+。

```powershell
dotnet run --project WebPostman.Api
```

开发前端时，在另一个终端运行：

```powershell
cd WebPostman.Web
npm install
npm run dev
```

开发页面地址由 Vite 输出（通常为 `http://127.0.0.1:5173`）；前端 API 请求代理到 `http://127.0.0.1:5078`。

制作单端口版本：

```powershell
cd WebPostman.Web
npm install
npm run build
cd ..
dotnet run --project WebPostman.Api
```

浏览器打开 `http://127.0.0.1:5078`。应用只监听本机地址。账户与分享数据在 `WebPostman.Api/App_Data`，游客工作区在浏览器中。

## 系统服务

### 从发布目录直接安装

发布目录中的独立安装脚本只注册并启动服务，不执行 npm、前端编译或 `dotnet publish`。请保持 `scripts` 与 `WebPostman.Api.exe`、`WebPostman.Api.dll` 位于截图所示的相对位置。端口参数可省略，默认使用 `5078`：

```powershell
# Windows：管理员 PowerShell
.\scripts\install-service.ps1
.\scripts\install-service.ps1 5080
.\scripts\install-service.ps1 5080 0.0.0.0
```

```bash
# Linux
sudo bash scripts/install-service.sh
sudo bash scripts/install-service.sh 5080
sudo bash scripts/install-service.sh 5080 0.0.0.0
```

第二个参数为监听地址。使用 `0.0.0.0` 可接受来自本机所有 IPv4 网络接口的连接；省略时只监听 `127.0.0.1`。重新执行脚本会更新并重启已经安装的服务，不会重新编译程序。

程序直接从当前发布目录运行，SQLite 数据保存在该目录的 `App_Data` 中。安装服务后不要移动或删除发布目录。Windows 服务以 LocalService 身份运行；Linux 服务以 `webpostman` 用户运行。框架依赖发布需要目标机器安装 .NET 8 ASP.NET Core Runtime，Linux 还需要 systemd。

### 从源码构建并管理服务

`manage-service` 脚本的 `install` 操作适用于完整源码目录：它会安装前端依赖、构建 Vue 页面、执行 `dotnet publish`，再注册服务。此方式需要 .NET 8 SDK、Node.js 20.19+ 和 npm。存在 `WebPostman.Web/package-lock.json` 时使用 `npm ci`，锁文件缺失时自动改用 `npm install`。

默认监听 `http://127.0.0.1:5078`，可在 `install` 或 `run` 后传入其他端口，例如 `install 5080`。切换端口时重新安装服务。

Windows：在**管理员 PowerShell** 中安装、卸载或控制服务。

```powershell
.\scripts\manage-service.ps1 install
.\scripts\manage-service.ps1 status
.\scripts\manage-service.ps1 stop
.\scripts\manage-service.ps1 start
.\scripts\manage-service.ps1 restart
.\scripts\manage-service.ps1 uninstall
```

Linux：使用 sudo 安装、卸载或控制服务。

```bash
sudo bash scripts/manage-service.sh install
bash scripts/manage-service.sh status
sudo bash scripts/manage-service.sh stop
sudo bash scripts/manage-service.sh start
sudo bash scripts/manage-service.sh restart
sudo bash scripts/manage-service.sh uninstall
```

`run` 用于在前台直接启动服务器，不注册系统服务，也不需要管理员权限；按 Ctrl+C 停止。已在默认端口运行开发服务器或系统服务时，请先停止它，再执行 `run`。

```powershell
.\scripts\manage-service.ps1 run
```

```bash
bash scripts/manage-service.sh run
```

通过源码管理脚本安装时，Windows 程序发布在 `%ProgramFiles%\WebPostman`、数据位于 `%ProgramData%\WebPostman`；Linux 程序发布在 `/opt/webpostman`、数据位于 `/var/lib/webpostman`。Windows 服务名为 `WebPostman`，Linux 服务名为 `webpostman.service`。再次执行安装可更新服务配置，`uninstall` 只移除服务注册并保留程序文件和工作区数据。

## 功能

- 请求标签页分别保留草稿、响应和发送状态；支持复制请求、复制 Bash cURL、Ctrl+S / ⌘S 保存，以及关闭未保存标签时确认。
- 集合与请求搜索、集合/文件夹折叠、Postman 嵌套文件夹与授权继承；Multipart 导入保留文本和文件字段，文件内容需要重新选择。
- 每个请求可配置 1～600 秒超时和重定向开关；诊断区分原始与最终 URL，跨来源重定向不转发敏感请求头。敏感诊断默认隐藏，可按请求显式显示。
- 响应正文搜索高亮、文本/二进制下载；超过 2 MB 的响应会标明截断，下载内容也仅包含已读取的部分。
- 账户工作区的集合、环境和历史分表存储，仅提交变更实体；保存采用版本检测，旧页面无法覆盖新修改。冲突后可导出备份（含未保存标签草稿）再重新载入。
- 每个账户或本机工作区保留最近 1000 条历史，账户历史通过独立接口分页查询。导出时包含全部保留历史；导入集合不会改写已有历史。

- HTTP 请求编辑与发送，支持参数、请求头、请求 Cookie、JSON、文本、表单和 Multipart 文件。
- 可按请求启用不安全 SSL 连接，用于访问自签名或证书不受信任的 HTTPS 服务；该选项会随请求及历史快照保存。
- Basic Auth、Bearer Token、API Key；支持集合、文件夹、请求三级配置与继承。
- 请求可选择集合及文件夹保存；历史记录保留发送时的请求参数、正文、Cookie、有效认证与环境变量快照（旧版历史只有地址）。
- 响应单独显示 Set-Cookie；请求诊断显示最终 URL、请求头与正文预览，以及准备、等待响应头、读取响应体阶段耗时。等待响应头包含 DNS/TCP/TLS/服务端处理，无法精确拆分这些子阶段。
- 自动添加 `Accept`、`User-Agent`、`Accept-Encoding` 请求头，自定义同名头可以覆盖。
- 环境变量 `{{变量名}}`、本地工作区导入导出，以及 Postman Collection v2.1 的基础导入。

请求凭据及历史快照以明文保存在相应的浏览器工作区、服务端账户 SQLite 工作区或导出的 JSON 中；登录密码仅存哈希。请妥善保管这些文件；不要将本应用直接作为公网服务部署。

测试时可通过环境变量 `WebPostman__ListenUrl` 和 `WebPostman__DataDirectory` 指定独立端口与数据目录，避免更改已有工作区。

### 数据升级与验证

个人工作区存储兼容旧数据库的分表迁移，迁移在事务内完成并保留原始 `workspace` JSON 行。旧匿名服务器数据库不会自动分配给注册用户，请使用旧版导出后导入到目标账户。升级前请备份 `App_Data`；降级旧版本不能读取新版表中的后续修改。新旧版本应分别使用自己的数据目录。

安装前端依赖后，可在仓库根目录执行：

```powershell
npm --prefix WebPostman.Web run build
dotnet build WebPostman.Api/WebPostman.Api.csproj -c Release
node --test tests/frontend.test.mjs tests/api.test.mjs tests/accounts.test.mjs
```

测试使用独立端口和临时 SQLite 数据库，需要 Node.js 22、.NET 8 SDK 和 Python 3（生成旧库迁移测试数据）。`Regression checks` 在提交到 main 或创建 PR 时自动在 Windows / Linux 上执行。发布工作流继续对两个平台的便携包执行启动检查。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
