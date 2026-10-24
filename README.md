# WebPostman

本地运行的 API 请求工作台。后端使用 .NET 8，页面使用 Vue 3，数据保存在本地 SQLite。

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

浏览器打开 `http://127.0.0.1:5078`。应用只监听本机地址。数据文件在 `WebPostman.Api/App_Data/workspace.db`。

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

- HTTP 请求编辑与发送，支持参数、请求头、请求 Cookie、JSON、文本、表单和 Multipart 文件。
- 可按请求启用不安全 SSL 连接，用于访问自签名或证书不受信任的 HTTPS 服务；该选项会随请求及历史快照保存。
- Basic Auth、Bearer Token、API Key；支持集合、文件夹、请求三级配置与继承。
- 请求可选择集合及文件夹保存；历史记录保留发送时的请求参数、正文、Cookie、有效认证与环境变量快照（旧版历史只有地址）。
- 响应单独显示 Set-Cookie；请求诊断显示最终 URL、请求头与正文预览，以及准备、等待响应头、读取响应体阶段耗时。等待响应头包含 DNS/TCP/TLS/服务端处理，无法精确拆分这些子阶段。
- 自动添加 `Accept`、`User-Agent`、`Accept-Encoding` 请求头，自定义同名头可以覆盖。
- 环境变量 `{{变量名}}`、本地工作区导入导出，以及 Postman Collection v2.1 的基础导入。

认证值及历史快照以明文保存在本机 SQLite 工作区及导出的 JSON 中。请妥善保管这些文件；不要将本应用直接作为公网服务部署。

测试时可通过环境变量 `WebPostman__ListenUrl` 和 `WebPostman__DataDirectory` 指定独立端口与数据目录，避免更改已有工作区。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
