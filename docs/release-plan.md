# Serena Pool 首版发布与改进方案

**目标**　发布一个跨平台 NuGet 工具包，只公开 `mcp` 和 `setup` 两个子命令。Web 管理页默认为 `http://127.0.0.1:32980/`，HTTP MCP 入口为同一地址的 `/mcp`。

## 两个命令

| 命令 | 行为 |
| --- | --- |
| `serena-pool mcp` | 通过 stdio 服务当前客户端。首次连接自动启动本机 ASP.NET Core 管理进程，同时提供 Web 和 HTTP MCP。同一用户、同一端口的后续客户端复用实例池。stdout 只输出 MCP 消息，浏览器由用户按需打开。最后一个客户端断开且实例全部回收后，自动管理进程按现有空闲策略退出。 |
| `serena-pool setup [harness ...]` | 一次安装所选本机 harness 的 MCP 注册和同版本 skill。交互运行且省略目标时列出候选并允许多选，标出已检测到的客户端。脚本显式传入一个或多个目标。非交互且未传目标时打印用法和目标列表并退出。成功后显示实际配置位置、skill 路径、管理页地址及默认凭证文件位置（使用文件凭证时）。 |

建议包名 `SerenaPool`。下例以拟定的预览版本为例。一个目标即可安装，同时使用多个客户端时可追加名称。正常运行需要 .NET 10 或更新的 SDK、ASP.NET Core 10 运行时及支持 REPL 的 Serena。使用 .NET 11 或更新的 SDK 时可在 `--` 前加 `--yes`，自动确认下载与运行。[.NET 工具执行方式](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-exec)

```sh
dotnet tool exec SerenaPool@0.1.0-preview.1 -- setup codex claude-code
```

## setup 的本机 harness 范围

### 各宿主默认读取的 skill 目录

下表列出 Serena Pool 可直接放置 `serena-pool/SKILL.md` 的路径。用户级目录适用于同一用户的各个项目，项目级目录相对于仓库根目录。

| 本机 harness | 用户级目录（`setup` 使用） | 项目级目录（供查阅） | 官方依据 |
| --- | --- | --- | --- |
| Codex、Cursor、OpenCode、GitHub Copilot（CLI 和 VS Code 原生 Copilot）、Gemini CLI | `~/.agents/skills/` | `.agents/skills/` | [Codex](https://developers.openai.com/codex/skills/) · [Cursor](https://cursor.com/docs/skills) · [OpenCode](https://opencode.ai/docs/skills) · [Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills) · [VS Code 原生 Copilot](https://code.visualstudio.com/docs/agent-customization/migrate-customizations) · [Gemini CLI](https://geminicli.com/docs/cli/skills/) |
| Claude Code | `~/.claude/skills/` | `.claude/skills/` | [Claude Code](https://code.claude.com/docs/en/skills) |
| Antigravity CLI | `~/.gemini/config/skills/` | `.agents/skills/` | [Google Antigravity](https://antigravity.google/docs/customization-discovery?tab=cli) |

`setup` 将所选目标映射到用户级目录，按目录去重，直接写入包内的普通文件。上表第一行任选五个目标中的一个或多个，都只写一份。选中 Claude Code 或 Antigravity CLI 时，分别在其用户级目录写一份。只选 Claude Code 时，只写 `~/.claude/skills/serena-pool/SKILL.md`。七个目标全选共写三份，项目文件由用户自行管理。

共享目录中的 skill 可能被未选中的宿主发现。skill 应先确认当前会话提供 Serena Pool 的 MCP 工具再调用。

### MCP 如何注册

只为用户选中的目标注册 MCP。启动程序和参数来自同一份定义，各宿主按自己的注册格式写入。以下路径为用户级默认位置。

| 目标参数 | MCP 注册方式 | 官方依据 |
| --- | --- | --- |
| `codex` | 用户 `config.toml` 的 `mcp_servers.serena-pool`，遵循 `CODEX_HOME` | [Codex MCP](https://developers.openai.com/codex/mcp/) |
| `claude-code` | `claude mcp add` 显式选择 stdio 和 `--scope user` | [Claude Code MCP](https://code.claude.com/docs/en/mcp) |
| `cursor` | `~/.cursor/mcp.json` 的 `mcpServers`，供本机编辑器和 CLI 使用 | [Cursor MCP](https://cursor.com/docs/mcp) |
| `copilot` | Copilot CLI 与 VS Code 原生 Copilot 共用 `$COPILOT_HOME/mcp-config.json`，默认在 `~/.copilot/` | [CLI MCP](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers) · [VS Code 原生配置](https://code.visualstudio.com/docs/agents/reference/mcp-configuration) |
| `opencode` | 用户配置的 `mcp` 项，使用 `type: local` 和包含程序与参数的 `command` 数组 | [OpenCode MCP](https://opencode.ai/docs/mcp-servers) |
| `antigravity` | `~/.gemini/config/mcp_config.json` | [Antigravity 配置](https://antigravity.google/docs/customization-discovery?tab=cli) |
| `gemini-cli` | 供仍使用 Gemini CLI 的用户显式选择，注册到其用户级 `settings.json` | [Gemini CLI MCP](https://geminicli.com/docs/tools/mcp-server/) · [Google 迁移说明](https://antigravity.google/docs/cli/gcli-migration/) |

适配本机运行的 harness。SSH、容器或远程工作区需要在实际运行 harness 的环境中执行安装。VS Code 使用原生 Copilot 时，选择一次 `copilot` 即可。[VS Code 的本机与远程配置](https://code.visualstudio.com/docs/agents/reference/mcp-configuration)

## 实现范围

1. **启动与地址。** 将当前 `47321` 改为 `32980`，确保打包配置、内部后台启动、帮助和文档一致。`mcp --urls http://127.0.0.1:端口` 显式覆盖。同端口被无关进程占用时报告地址和处理方法。无子命令时显示帮助，管理进程只由客户端按需启动。
2. **可选环境参数。** `Pool__Executable` 接受 Serena 可执行文件的绝对路径或所在目录，目录形式按平台解析 `serena.exe` 或 `serena`。省略时从 PATH 查找，显式路径无效时报告错误。`SerenaPool__Token` 用于 Web 管理 API 和 HTTP MCP，省略时沿用当前用户的凭证文件。参数由共享管理进程在启动时读取。agent 通过 `acquire(workdir)` 传入项目目录。
3. **共用启动定义。** 各 harness 注册名统一为 `serena-pool`，启动程序为解析出的 `dotnet` 绝对路径，参数为 `tool exec SerenaPool@同版本 -- mcp`，检测到 .NET 11 或更新的 SDK 时在 `--` 前加 `--yes`。适配层只处理根目录、注册格式、环境传递和 skill 发现规则。通过宿主支持的环境继承或变量引用传入口令，安装命令、配置文件和日志均不写入口令明文。使用自定义令牌时应让启动 harness 的进程可见该变量。
4. **多目标安装与更新。** 写入前检查全部选中目标，按实际路径合并共享文件。skill 使用同一份源文件，描述 `list`、`acquire`、`call`、`release` 和会话失效处理，工具名按宿主实际公布的前缀定位。如果共用同一 skill 的已安装目标固定在另一版本，提示一起升级受影响的目标。重复安装同版不改写，遇到同名外部条目、用户改过的 skill 或共享版本冲突时，列出受影响目标并在写入前停止。保留其他注册（含现有 1MCP）、自定义路径、端口与宿主权限。中途失败只撤销本次改动，恢复所选目标的一致状态。
5. **跨平台包。** 复用现有 `net10.0` Web 项目和内嵌管理页，配置 `DotnetTool`、`McpServer` 包类型。包内附 `.mcp/server.json`、README、运行配置和 `skills/serena-pool/SKILL.md`。保留按平台发布单文件程序的方式，两种分发形式提供同样的两个命令。[NuGet MCP 元数据](https://learn.microsoft.com/en-us/nuget/concepts/nuget-mcp) · [包内分发 skill](https://devblogs.microsoft.com/dotnet/delivering-skills-in-nuget-packages/)

## 旧版迁移

端口参与本机管道命名。先在 `47321` 完成并释放已有领用，关闭旧连接。手动启动的旧版 `serve` 正常退出后，再用 `setup` 将已识别的旧 EXE 加 `stdio` 注册迁移到新版 `mcp`。手写启动脚本同样改用 `mcp`。凭证文件位置改从 `setup` 输出查看。若新旧两版同时运行，原 `instance_id` 只对原池有效。

HTTP 客户端和管理页书签改用 `32980`。有意保留旧端口的部署让所有入口继续显式使用相同的 `--urls`。回退时先释放新版领用，再还原旧 EXE、旧命令与地址、原有环境参数及对应版本的 skill。

## 发布顺序与验收

| 阶段 | 工作与完成条件 |
| --- | --- |
| 1. 启动入口 | 验证公开帮助只列 `mcp`、`setup`。从实际包启动 `mcp`，在 `32980` 打开 Web 并通过带令牌的 HTTP MCP 与 stdio 访问同一池。覆盖自定义端口和占用错误。 |
| 2. 本地预览包 | 用 `dotnet pack -c Release` 生成包。从仓库外启动两个 stdio 客户端，完成 `list → acquire → call → release`，验证后台启动、工作目录复用、会话隔离和断开回收。 |
| 3. Harness 验收 | 在隔离用户配置中逐项测试表内目标，包括首次安装、重复安装、升级、现有 EXE 迁移、同名冲突和失败回滚。验证五个共享目录的目标多选或分次补装只写一份。单选 Claude Code 或 Antigravity 只写其各自的用户级目录，七个目标全选应在三个目录各有一份普通文件，同版本重跑不改写。验证跨版本冲突和安装回滚保留其他宿主已在使用的文件。按客户端分别记录“配置已验证”和“真实新会话已发现 skill、列出四个工具并完成领用与释放”。Copilot CLI 与 VS Code 原生 Copilot 分别验证。 |
| 4. 对外发布 | 先交付本地预览包与 Windows 实测结果。Linux、macOS 分别记录原生启动、子进程回收和端口实测状态。确定包名、发布账户和公开范围后推送 NuGet 预览版，验证完成后发布稳定版。 |
