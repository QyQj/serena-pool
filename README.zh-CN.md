# Serena Pool

[English](README.md) | **简体中文**

Serena Pool 让同一台电脑上的多个 MCP 客户端和编码 Agent 共用 [Serena](https://github.com/oraios/serena) 进程。它按项目或 Git worktree 目录最多启动一个 Serena 实例，为每次领用提供独立的 Python REPL 会话，同时提供 `stdio` 与 Streamable HTTP MCP 入口以及本机 Web 管理页。支持 Windows、Linux 和 macOS。

## 核心特性

- **按工作目录 / worktree 复用进程**：将 `workdir` 规范化为物理绝对路径（自动解析符号链接与目录联接），同一仓库的多个客户端共用同一个预热好的 Serena 与语言服务器进程，不同 Git worktree 并行运行、互不干扰。
- **隔离的 REPL 会话（领用 / Lease）**：每次调用 `acquire(workdir)` 都会在 Serena 中开启独立的 REPL 会话并返回私有 `instance_id`，各 Agent 的 Python 变量命名空间相互隔离，同时避免重复启动语言服务器。
- **同目录串行调度**：发往同一项目目录的调用通过实例内互斥门控排队顺序执行，防止并发操作 REPL 或 LSP 状态产生冲突。
- **多客户端共享（`stdio` 与 Streamable HTTP）**　后台管理进程用同一个实例池处理两种连接。每条 `stdio` 连接各自通过一个桥接进程，经本机命名管道接入后台。
- **一条命令接入 7 种客户端**：`serena-pool setup` 可同时写入 MCP 注册项并安装配套 `serena-pool` skill，支持写入前冲突检查与失败自动回滚。
- **本机 Web 管理页**：在 `http://127.0.0.1:32980/` 实时查看实例状态、PID、执行/排队数、最近错误和领用记录，支持手动释放滞留领用与回收空闲实例。Windows 上可从系统托盘打开管理页。
- **子进程树可靠回收**：Windows 使用 Job Object（`KILL_ON_JOB_CLOSE`），Linux 和 macOS 使用独立 POSIX 进程组配合父进程守望启动器，即使管理进程异常退出也不会残留语言服务器子进程。

## 工作原理

```text
  MCP 客户端
      │ stdio
      ▼
  客户端桥接进程 (serena-pool mcp)
      │ 命名管道
      ▼
  ┌────────────────────────────────────────────────┐
  │ ASP.NET Core 管理后台进程 (--background-serve) │
  │ LocalPipeServer ─► PoolTools ─► InstancePool   │──► Serena 工作进程 ─► 语言服务器
  └────────────────────────────────────────────────┘
```

1. **按需启动共享管理进程**　所有 `serena-pool mcp` 桥接进程默认使用 `http://127.0.0.1:32980`。桥接先按配置的端口连接 `serena-pool-<user>-<port>`。连接超时后，它会尝试启动脱离终端的 `--background-serve` 后台进程，并传入同一个监听地址和端口。多个进程同时启动且使用同一个回环 IP 地址和 TCP 端口时，只有一个能绑定该监听地址。同一用户在不同回环地址使用相同端口时，管道名仍相同。要运行独立的实例池，请使用不同端口。
2. **按目录管理实例**　管道服务端与实例池运行在同一个 ASP.NET Core 后台进程中。`PoolTools` 收到 `acquire(workdir)` 后交给 `InstancePool`。如果该目录没有可用实例，`InstancePool` 会启动 Serena 工作进程（`serena start-mcp-server --transport stdio --project <workdir> --context ide --agent-interface repl --add-mode no-memories --enable-web-dashboard false --enable-gui-log-window false`）。每次领用都会在对应工作进程中新建独立的 REPL 会话。
3. **故障隔离**：若某个 Serena 进程意外退出或中断，仅该实例被标记为 `failed` 并使其领用失效；正在执行的调用会返回“执行结果未知”错误以便调用方先核对状态再决定是否重试，其他工作目录的实例不受影响。
4. **自动回收与空闲退出**：无活动领用在超过 `Pool__LeaseTimeout` 后自动回收（排队与执行期间暂停计时）；无领用且无待处理调用的空闲实例在超过 `Pool__IdleTimeout` 后停止。所有 `stdio` 客户端断开且全部实例回收后，自动启动的后台进程会空闲退出（命名管道每 15 秒检查一次）。

## 运行前提

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 或更新版本（已包含 ASP.NET Core 10 运行时）。
- 支持 REPL 接口的 [Serena](https://github.com/oraios/serena)（需支持 `--agent-interface repl`，提供 `initial_instructions` 与 `serena_repl`）。
- Serena Pool 在工作进程启动时读取 `serena_repl` 声明的必填会话参数。Serena 2.0.0 使用 `session_id`，Serena 2.1.0 使用 `session`。MCP 客户端通过 `instance_id` 传递 Pool 的领用 ID。
- 安装 `serena`（Windows 上为 `serena.exe`）。`setup` 会搜索 `PATH` 和用户安装目录，也可通过 `Pool__Executable` 显式指定可执行文件或所在目录。

## 安装与快速配置

通过 [NuGet](https://www.nuget.org/packages/SerenaPool) 直接使用 `dnx` 或 `dotnet tool exec` 运行预览版（以下示例为 Codex 注册 MCP 与配套 skill）：

```sh
dnx SerenaPool --prerelease -- setup codex
```

也可使用 `dotnet tool exec`：

```sh
dotnet tool exec SerenaPool --prerelease -- setup codex
```

或者安装为全局工具后直接使用 `serena-pool` 命令：

```sh
dotnet tool install --global SerenaPool --prerelease
serena-pool setup codex
```

> [!NOTE]
> 在 .NET 11 或更新版本的 SDK 中交互运行 `dotnet tool exec` 时，可在 `--` 前加上 `--yes` 跳过确认提示。`serena-pool setup` 检测到系统默认 SDK 为 .NET 11+ 时，会自动在写入客户端的启动参数中加入 `--yes`。

### 升级

用新版本执行 `setup`，即可更新客户端配置里固定的包版本。以 Codex 为例：

```sh
dotnet tool exec SerenaPool@0.1.0-preview.7 -- setup codex
```

已安装的客户端如果共用同一份 skill，请在同一条命令里一起更新，例如 `setup codex cursor`。习惯使用全局命令时，先运行 `dotnet tool update --global SerenaPool --version 0.1.0-preview.7`，再执行 `serena-pool setup codex`。

释放正在使用的领用并关闭连接旧共享池的客户端。等旧后台进程退出后，再开启新会话。仅升级全局工具不会修改客户端里固定的版本。

### 支持的客户端（`setup`）

可在 `setup` 后一次传入多个客户端标识（如 `serena-pool setup codex cursor`），或在交互式终端中省略标识，按提示从已检测到的列表中进行选择：

| 客户端标识 | MCP 配置写入位置 | Skill 安装路径 | 说明 |
| --- | --- | --- | --- |
| `codex` | `$CODEX_HOME/config.toml`（默认 `~/.codex/config.toml`） | `~/.agents/skills/serena-pool/SKILL.md` | 写入 `[mcp_servers.serena-pool]`。 |
| `claude-code` | `$CLAUDE_CONFIG_DIR/.claude.json`（默认 `~/.claude.json`） | `$CLAUDE_CONFIG_DIR/skills/serena-pool/SKILL.md`（默认 `~/.claude/skills/serena-pool/SKILL.md`） | 通过 `claude mcp add --transport stdio --scope user` 注册；需要 PATH 上有原生 `claude` 命令。 |
| `cursor` | `~/.cursor/mcp.json` | `~/.agents/skills/serena-pool/SKILL.md` | 写入 `mcpServers.serena-pool`（`type: "stdio"`）。 |
| `copilot` | `$COPILOT_HOME/mcp-config.json`（默认 `~/.copilot/mcp-config.json`） | `~/.agents/skills/serena-pool/SKILL.md` | Copilot CLI 与 VS Code 原生 Copilot 共用配置。 |
| `opencode` | `$OPENCODE_CONFIG`，或 `$XDG_CONFIG_HOME/opencode/opencode.json[c]`（默认 `~/.config/opencode/opencode.json`） | `~/.agents/skills/serena-pool/SKILL.md` | 写入 `mcp.serena-pool`（`type: "local"` 与 `command` 数组）。 |
| `antigravity` | `~/.gemini/config/mcp_config.json` | `~/.gemini/config/skills/serena-pool/SKILL.md` | 写入 `mcpServers.serena-pool`。 |
| `gemini-cli` | `$GEMINI_CLI_HOME/.gemini/settings.json`（默认 `~/.gemini/settings.json`） | `~/.agents/skills/serena-pool/SKILL.md` | 写入 `mcpServers.serena-pool`。 |

`setup` 在写入任何文件前都会执行检查并保障一致性：
- 自动按物理路径去重共享 skill 目录（例如 `codex`、`cursor`、`copilot`、`opencode` 与 `gemini-cli` 共用 `~/.agents/skills/serena-pool/SKILL.md`），并在 `~/.serena-pool/setup.json` 记录托管状态。
- 保留其他无关 MCP 条目以及已为 `serena-pool` 配置的自定义 `--urls` 参数。Codex 配置已有 `[mcp_servers]` 时，只追加 Serena Pool 的子表。
- 若发现同名外部 MCP 条目、用户手动修改过 skill 文件，或共用同一 skill 的其他客户端仍固定在另一版本，会在写入前报错停止（跨版本冲突时需将共用客户端一并升级）。
- 写入前会运行找到的 Serena，确认它支持 REPL。
- 在各宿主的 MCP 条目中将 Serena 的绝对路径写为 `Pool__Executable`，并清理已有受管条目中的旧凭证设置。
- 修改已有 MCP 配置前会创建备份，文件名以 `.SerenaPool` 结尾。若已有同名备份，会在后缀前加编号。
- 写入过程中若发生异常，会自动回滚本次所有改动。

配置完成后，终端会输出版本、各客户端的配置和 skill 路径、本次写入的 Serena Pool 管理项、备份路径（如有）及运行地址。之后在客户端开启新会话即可加载。

## 连接与管理

### CLI 命令

```sh
serena-pool mcp [--urls http://127.0.0.1:32980]
serena-pool setup [harness ...]
serena-pool --help
```

### MCP 连接方式

- **stdio**：手动配置客户端时，可将启动命令设为已安装的 `serena-pool` 并传入 `["mcp"]`（或使用 `setup` 生成的固定版本 `dotnet tool exec SerenaPool@<版本> -- mcp`）：

  ```json
  {
    "mcpServers": {
      "serena-pool": {
        "command": "serena-pool",
        "args": ["mcp"]
      }
    }
  }
  ```

- **Streamable HTTP**：本机客户端可直接连接 `http://127.0.0.1:32980/mcp`。请求须来自回环地址且使用回环 Host，浏览器请求的 Origin 须匹配。

- **自定义端口**：如需更换端口，请为需要共享实例池的所有 stdio 客户端 `mcp` 命令传入相同的 `--urls http://127.0.0.1:<端口>`，并在 Web 管理页和 `/mcp` 地址中使用同一端口。`--urls` 必须是带固定非零端口的本机回环 `http` 或 `https` 地址，且不能包含路径、用户信息、查询参数或片段。

### MCP 工具与 REPL 用法

支持 MCP 工具标题的客户端会显示 `SerenaPool Acquire` 这样的标题。实际调用名见下表。

| MCP 工具 | 用法说明 |
| --- | --- |
| `list()` | 只读查看当前所有实例（`number`、`workdir`、`pid`、`state`、`started`、`recycleAt`、`running`、`queued`、`lastError`）以及公开的领用编号，不会泄露私有的 `instance_id`。 |
| `acquire(workdir)` | 传入已存在的项目或 worktree 绝对路径，按需启动对应实例并返回 `{ instance_id, workdir, instructions }`。每个 Agent/会话应独立领用并妥善保存各自的 `instance_id`。 |
| `call(instance_id, code)` | 在领用的 Serena REPL 会话中执行 Python 代码，变量在同一领用周期内持久保留。通过 `s.lsp` 调用符号分析，通过 `s.info(...)` 查看 API 签名。 |
| `release(instance_id)` | 释放会话领用并取消属于该领用的排队调用（已开始执行的调用会继续完成）。返回是否实际释放。 |

在 `call(instance_id, code)` 中运行 Serena REPL 代码示例：

```python
# 查询可用的 Serena LSP 接口签名
s.info("lsp.find_symbol", "lsp.find_referencing_symbols")
```

```python
# 在当前领用的工作目录中查找符号（变量在后续 call() 中保留）
symbols = s.lsp.find_symbol(
    "InstancePool",
    relative_path="src/SerenaPool/InstancePool.cs",
    include_body=False,
)
symbols
```

若领用超时失效或底层 Serena 实例重启，需要重新调用 `acquire(workdir)` 并阅读新返回的 `instructions`（旧会话中的 REPL 变量会随原会话一并销毁）。

### Web 管理页

在浏览器打开 [http://127.0.0.1:32980/](http://127.0.0.1:32980/)（若使用了 `--urls` 则替换为对应端口）。

- **仅限本机同源访问**：仅放行回环 IP、`127.0.0.1`/`localhost` 主机头及同源 `Origin` 请求，跨源或非本机访问将返回 `403 Forbidden`。
- **免令牌操作**：查看实例和领用状态、释放领用（`DELETE /api/leases/{number}`）以及回收空闲/已失效实例（`DELETE /api/workers/{number}`）均无需输入令牌，破坏性操作前会弹出确认对话框。
- **调用日志与 REPL 工具统计**：`/api/calls` 在内存中保留最近 200 条已结束的 REPL 请求，包括工作目录、公开领用编号、状态和含排队时间的耗时。管理页按这些请求中实际执行的 Serena 工具和 REPL 方法的调用次数排序，并显示有跟踪数据的请求数。日志只保存元数据，重启共享池后清空。
- **自动刷新**：页面可见时每 2.5 秒更新实例状态和调用历史。关闭浏览器页面不会停止后台实例池。

Windows、macOS 和 Linux 都提供管理页，浏览器标签使用 Serena Pool 图标。Windows 上每个共享池还会显示一个同款托盘图标，点击即可用默认浏览器打开管理页。管理页默认启用，浏览器按需打开。

如需关闭管理页、管理 API、调用跟踪和托盘，在 MCP 条目的环境配置中将 `Pool__WebDashboard` 设为 `"false"`。HTTP MCP 和 stdio 照常使用。若希望共享池启动时自动打开已启用的管理页，将 `Pool__WebDashboardOpenOnLaunch` 设为 `"true"`。

在 Codex 中，把开关加到 `[mcp_servers.serena-pool]` 的现有 `env` 表中，并保留 `setup` 写入的 Serena 路径。例如：

```toml
env = { Pool__Executable = 'C:\tools\serena.exe', Pool__WebDashboard = "false" }
```

## 环境变量

Serena Pool 的 CLI、后台诊断和 MCP 消息默认跟随系统界面语言。将 `SERENA_POOL_LANGUAGE` 设为 `en` 或 `zh-CN` 可覆盖默认值。管理页跟随浏览器语言，也可在页头切换。

请将这些变量写入 MCP 客户端配置。共享后台管理进程在启动时读取这些设置，修改配置或升级包后需重启后台进程。

| 环境变量 | 默认值 | 说明 |
| --- | --- | --- |
| `SERENA_POOL_LANGUAGE` | 系统界面语言，其他语言默认英语 | 用于 CLI 输出和实例池消息，可设为英语或简体中文。 |
| `Pool__Executable` | 依次搜索 `PATH`、`UV_TOOL_BIN_DIR`、`XDG_BIN_HOME` 和 `~/.local/bin` | 指定 Serena 可执行文件或其所在目录的绝对路径。 |
| `Pool__IdleTimeout` | `00:10:00` | 实例在全部领用释放且无待处理调用后的空闲保留时长（必须大于零）。 |
| `Pool__WebDashboard` | `true` | 启用管理页、管理 API 和 Windows 托盘图标。 |
| `Pool__WebDashboardOpenOnLaunch` | `false` | 共享池启动时，用浏览器打开已启用的管理页。 |
| `Pool__LeaseTimeout` | `01:00:00` | 无活动领用的超时回收时长，排队与执行期间暂停计时（必须大于零）。 |

## 从源码构建、测试与打包

源码构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（见 `global.json`）。运行冒烟测试还需 `git` 以及 `PATH` 上可用的 `serena` 可执行文件。

```sh
# 构建解决方案
dotnet build SerenaPool.slnx -c Release

# 打包 NuGet 工具包（产物位于 src/SerenaPool/bin/Release）
dotnet pack src/SerenaPool -c Release

# 验证本地打包产物
dotnet tool exec SerenaPool@0.1.0-preview.7 --source src/SerenaPool/bin/Release -- --help

# 使用真实 Serena 与临时目录运行端到端冒烟测试
dotnet run --project tests/SerenaPool.Smoke -c Release -- "<可写临时目录的绝对路径>"
```

冒烟测试会在指定目录中创建临时 Git worktree 与独立的 `SERENA_HOME`，使用真实 Serena 与语言服务器验证 `stdio` 与 Streamable HTTP 传输、会话命名空间隔离、排队调用取消、Web 管理接口、实例崩溃隔离、子进程树清理及自动空闲退出。

也可发布依赖系统 .NET 运行时的单文件可执行程序（将 `win-x64` 替换为 `linux-x64`、`linux-arm64`、`osx-x64` 或 `osx-arm64` 可生成其他平台版本）：

```sh
dotnet publish src/SerenaPool -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## 许可证

[MIT](https://github.com/QyQj/serena-pool/blob/main/LICENSE)
