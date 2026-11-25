# Serena Pool

Serena Pool 是 Windows、Linux 和 macOS 的本机 Serena 多实例管理器。每个工作目录按需启动一个 Serena 进程，每次领用建立独立的 REPL 会话。stdio、HTTP MCP 和 Web 管理页共用实例池。

## 安装

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 或更新的 SDK、ASP.NET Core 10 运行时，以及支持 REPL 的 [Serena](https://github.com/oraios/serena)。.NET 10 SDK 已包含该运行时。将 Serena 加到启动宿主的 PATH，或在宿主环境设置 `Pool__Executable`。

首个预览版发布到 [NuGet.org](https://www.nuget.org/packages/SerenaPool) 后，可用下列任一命令为 Codex 和 Claude Code 注册 MCP 及配套 skill。

```sh
dnx SerenaPool@0.1.0-preview.1 -- setup codex claude-code
dotnet tool exec SerenaPool@0.1.0-preview.1 -- setup codex claude-code
```

也可以安装成全局工具，然后用 `serena-pool` 命令运行。

```sh
dotnet tool install --global SerenaPool --version 0.1.0-preview.1
serena-pool setup codex claude-code
```

验证本地预览包时，为 `dnx` 或 `dotnet tool exec` 在 `--` 前加 `--source <本地包目录>`，并让启动 MCP 宿主的 NuGet 配置能访问同一来源。

`setup` 支持 `codex`、`claude-code`、`cursor`、`copilot`、`opencode`、`antigravity` 和 `gemini-cli`。一次可传入多个目标。交互式终端省略目标会显示可多选的列表。Claude Code 注册需要 PATH 上的 `claude` 原生命令。Copilot CLI 和 VS Code 原生 Copilot 使用同一用户配置。

安装会显示实际注册与 skill 路径。宿主启动指定版本的 Serena Pool，本机生效的 SDK 主版本决定是否写入 `--yes`。升级到另一版本时，一起选择正在共用同一 skill 的宿主。安装完成后，在宿主里开启新会话。

## 启动与连接

`serena-pool mcp` 使用 stdio，首次连接自动拉起本机管理进程。同一用户与端口的客户端复用后端。最后一个 stdio 客户端断开且所有实例回收后，管理器会在空闲检查时退出。默认空闲检查周期为 15 秒。

管理页默认在 [http://127.0.0.1:32980/](http://127.0.0.1:32980/)。HTTP MCP 入口为 `http://127.0.0.1:32980/mcp`，使用 Streamable HTTP 和 `Authorization: Bearer <令牌>`。默认凭证文件创建于首次启动时，路径见 `setup` 输出。Web 页面支持查看实例详情、释放遗留领用以及回收空闲实例。关闭浏览器不会停止管理器。

自定义端口时，给所有 stdio 客户端的 `mcp` 命令传入相同的 `--urls http://127.0.0.1:端口`，HTTP 客户端访问同一端口。单文件发行版使用程序绝对路径作为 MCP `command`，`args` 为 `["mcp"]`。

| MCP 工具 | 用法 |
| --- | --- |
| `list()` | 查看工作目录、PID、状态、公开领用编号和队列。 |
| `acquire(workdir)` | 传入已存在的项目绝对路径，读取 Serena 手册并保存私有 `instance_id`。 |
| `call(instance_id, code)` | 传入 Python REPL 代码，例如 `s.info("lsp.find_symbol")`。 |
| `release(instance_id)` | 释放领用、取消其排队调用。已开始的调用继续执行。 |

同目录的调用串行执行。工作目录会规范化并解析链接。Serena 以单项目上下文与 `no-memories` 模式启动。进程退出或原生 REPL 会话被回收后，旧领用需要重新申请。调用已经发出而连接中断时，管理器报告执行结果未知。

## 环境变量

| 环境变量 | 默认值 | 作用 |
| --- | --- | --- |
| `Pool__Executable` | Windows 的 `serena.exe` 或 Unix 的 `serena` | 从管理进程的 PATH 查找。也可指定程序或其所在目录的绝对路径。 |
| `Pool__IdleTimeout` | `00:10:00` | 最后一个领用释放且无调用后的实例保留时间。 |
| `Pool__LeaseTimeout` | `01:00:00` | 回收无活动领用的时间，排队与执行时暂停计时。 |
| `SerenaPool__Token` | 当前用户凭证文件 | Web 管理 API 和 HTTP MCP 使用的令牌。使用自定义值时，在启动宿主的进程环境中设置。 |

这些参数由共享管理进程在启动时读取。Copilot 注册通过环境变量引用向 MCP 进程传值。若宿主限制子进程环境，请按其权限规则允许 Serena Pool 所需的变量。Gemini CLI 会过滤令牌，使用自定义令牌时需在该宿主的 `security.environmentVariableRedaction.allowed` 中允许 `SerenaPool__Token`。Windows 用 Job Object 回收子进程树。Linux 和 macOS 使用独立 POSIX 进程组与监督进程来清理同组进程。

## 旧版迁移

先释放旧池在 `47321` 上的领用，关闭旧客户端与手动启动的管理器。运行 `setup` 可迁移识别到的旧版 `SerenaPool.exe stdio` 或 `SerenaPool stdio` 注册。手写命令改用 `mcp`，HTTP 客户端和书签改用 `32980`。若注册显式指定了 `--urls`，迁移会保留此参数。控制台无子命令时会显示帮助。

## 构建与验证

```sh
dotnet build SerenaPool.slnx -c Release
dotnet pack src/SerenaPool -c Release
dotnet run --project tests/SerenaPool.Smoke -c Release -- "<可写临时目录的绝对路径>"
```

NuGet 产物位于 `src/SerenaPool/bin/Release`。冒烟测试使用真实 Serena、stdio、HTTP 和语言服务器验证实例池生命周期。

也可通过 `dotnet publish src/SerenaPool -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true` 生成单文件程序。将 `win-x64` 换成 `linux-x64`、`linux-arm64`、`osx-x64` 或 `osx-arm64` 可生成其他平台的程序。

## 发布

[GitHub Actions](https://github.com/QyQj/serena-pool/actions/workflows/publish.yml) 在 PR 和 `main` 推送时构建并运行打包后的工具。推送与项目包版本一致的 `v` 标签，例如 `v0.1.0-preview.1`，才会将包发布到 NuGet.org。发布新版前，在项目文件中更新版本和 `PackageReleaseNotes`。

首次发布需要在 [NuGet.org Trusted Publishing](https://www.nuget.org/account/trustedpublishing) 创建策略。仓库所有者填 `QyQj`，仓库填 `serena-pool`，工作流文件填 `publish.yml`，环境填 `nuget`。将策略作用域限定到 `SerenaPool`，允许发布新包与新版本。再在 GitHub 的 `nuget` 环境添加 `NUGET_USER` secret，值为 NuGet 用户名。工作流通过 OIDC 获取短期发布凭据。[Microsoft 配置指南](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)

## 许可证

[MIT](https://github.com/QyQj/serena-pool/blob/main/LICENSE)
