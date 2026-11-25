# Serena Pool

Serena Pool 让同一台电脑上的多个 MCP 客户端共用 Serena。它按项目目录启动 Serena 进程，为每次领用提供独立的 REPL 会话，并提供 Web 管理页查看实例状态。支持 Windows、Linux 和 macOS。

## 安装与配置

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 或更新的 SDK、ASP.NET Core 10 运行时，以及支持 REPL 的 [Serena](https://github.com/oraios/serena)。.NET 10 SDK 已包含所需运行时。请让 MCP 客户端能从 PATH 找到 Serena，或设置 `Pool__Executable` 指向 Serena 的绝对路径。

通过 [NuGet](https://www.nuget.org/packages/SerenaPool) 运行预览版。以下命令会为 Codex 注册 MCP 服务及配套 skill。

```sh
dnx SerenaPool --prerelease -- setup codex
```

也可以用 `dotnet tool exec SerenaPool --prerelease -- setup codex`。安装全局工具后，可以直接在终端运行 `serena-pool` 命令。

```sh
dotnet tool install --global SerenaPool --prerelease
serena-pool setup codex
```

客户端标识包括 `codex`、`claude-code`、`cursor`、`copilot`、`opencode`、`antigravity` 和 `gemini-cli`。可在 `setup` 后传入多个标识，也可以在交互式终端省略标识，从列表中选择。Claude Code 需要 PATH 上的 `claude` 命令。Copilot CLI 与 VS Code 原生 Copilot 共用配置。

`setup` 会显示 MCP 注册位置、skill 路径及 HTTP MCP 凭证文件路径。配置完成后，在客户端开启新会话。

## 连接与管理

客户端首次连接时会启动本机管理进程。同一用户使用相同端口的客户端共用实例池。

- **stdio**　手动配置客户端时，可将已安装的 `serena-pool` 设为启动命令，传入 `mcp` 参数。
- **Web 管理页**　访问 [http://127.0.0.1:32980/](http://127.0.0.1:32980/) 可直接查看实例和领用状态、释放遗留领用以及回收空闲实例。页面仅允许本机访问。
- **HTTP MCP**　使用 Streamable HTTP 连接 `http://127.0.0.1:32980/mcp`，请求头带上 `Authorization: Bearer <令牌>`。

HTTP MCP 默认令牌文件在管理进程首次启动时创建。如需自定义端口，为每个 stdio 客户端的 `mcp` 命令传入相同的 `--urls http://127.0.0.1:端口`，并在 Web 和 HTTP MCP 地址中使用该端口。

| MCP 工具 | 用法 |
| --- | --- |
| `list()` | 查看实例、领用和排队状态。 |
| `acquire(workdir)` | 传入项目的绝对路径，返回使用说明和会话专属的 `instance_id`。 |
| `call(instance_id, code)` | 在领用的 Serena REPL 会话中运行代码，例如 `s.info("lsp.find_symbol")`。 |
| `release(instance_id)` | 释放会话并取消属于该会话的排队调用。已开始的调用会继续执行。 |

同一目录的调用按顺序执行。若实例或会话失效，客户端需要重新领用。最后一个 stdio 客户端断开且实例全部回收后，自动启动的管理进程会空闲退出。

## 环境变量

在启动 MCP 客户端前设置环境变量，Serena Pool 会在管理进程启动时读取。

| 环境变量 | 默认值 | 用法 |
| --- | --- | --- |
| `Pool__Executable` | 在 PATH 查找 `serena.exe` 或 `serena` | 指定 Serena 程序或其所在目录的绝对路径。 |
| `Pool__IdleTimeout` | `00:10:00` | 实例无领用和待处理调用后的保留时间。 |
| `Pool__LeaseTimeout` | `01:00:00` | 无活动领用的保留时间，排队和执行期间暂停计时。 |
| `SerenaPool__Token` | 首次启动时生成并保存在凭证文件中 | 为 HTTP MCP 指定自定义令牌。 |

如果客户端会过滤传给子进程的环境变量，请在客户端设置中允许需要传入的变量。Gemini CLI 使用自定义令牌时，需要在 `security.environmentVariableRedaction.allowed` 中允许 `SerenaPool__Token`。

## 许可证

[MIT](https://github.com/QyQj/serena-pool/blob/main/LICENSE)
