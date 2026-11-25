# Serena Pool

Serena Pool 是 Windows 本机的 Serena 多实例管理器。每个工作目录按需启动一个 Serena 进程，每次领用都会建立独立的 REPL 会话。不同 Git worktree 可以并行工作。

## 启动与连接

运行环境为 Windows、ASP.NET Core 10 运行时和支持 REPL 的 Serena（已用 2.1.0 验证）。源码开发使用 .NET 10 SDK。在项目根目录运行：

```sh
dotnet run --project src/SerenaPool
```

打开 [Web 管理页](http://127.0.0.1:47321/)。首次启动会在当前 Windows 用户的 `%LOCALAPPDATA%\SerenaPool\access-token` 创建凭证。可在 Niubash 里用下面的命令将令牌复制到剪贴板，再粘贴到管理页：

```sh
clip.exe < "$LOCALAPPDATA/SerenaPool/access-token"
```

Agent 使用 Streamable HTTP 连接 `http://127.0.0.1:47321/mcp`，请求头使用 `Authorization: Bearer <令牌>`。该凭证授予以当前用户身份运行 Serena REPL 和管理所有本机实例的权限，仅供受信任的本机 Agent 使用。网页的令牌仅在当前页面内存中保存，刷新后需要重新连接。

| MCP 工具 | 用法 |
| --- | --- |
| `acquire(workdir)` | 传入已存在的项目绝对路径，读取返回的 Serena 手册并保存私有 `instance_id`。 |
| `call(instance_id, code)` | 传入 REPL 代码，例如 `s.info("lsp.find_symbol")`。管理器绑定底层会话并转发 Serena 的结果。 |
| `list()` | 查看工作目录、PID、状态、公开的领用编号、正在执行与排队的调用。 |
| `release(instance_id)` | 释放领用，取消该领用排队中的调用，已开始的调用继续执行。返回值表示此次是否实际释放。 |

同一目录的调用串行执行。工作目录按绝对路径规范化并解析目录链接。Serena 启动时启用单项目上下文和 `no-memories` 模式。Web 页支持查看实例详情、释放遗留领用、回收空闲实例。关闭浏览器不影响进程池。

REPL 变量由 Serena 保存在内存。Serena 2.1.0 每个进程默认保留最近使用的 100 个原生会话。同一进程内大量反复领用时，较早会话的变量可能被 Serena 回收。

## 配置与回收

| 环境变量 | 默认值 | 作用 |
| --- | --- | --- |
| `Pool__Executable` | `serena.exe` | 从 PATH 查找，也可指定已安装 Serena 的绝对路径。 |
| `Pool__IdleTimeout` | `00:10:00` | 最后一个领用释放且无调用后保留进程的时间。 |
| `Pool__LeaseTimeout` | `01:00:00` | 回收无活动领用的时间。排队和执行期间暂停计时。 |
| `SerenaPool__Token` | 当前用户凭证文件 | 可由启动环境显式提供访问令牌。 |

进程退出后，旧领用失效。调用已发出时连接中断，管理器返回“执行结果未知”，并由调用方决定如何处理。Serena 和它启动的语言服务归入同一个 Windows Job Object，管理器异常退出时由 Windows 回收。

## 发布与验证

发布框架依赖的 win-x64 单文件程序，管理页嵌入可执行文件：

```sh
dotnet publish src/SerenaPool -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

在项目根目录执行端到端检查，参数是可写的临时目录：

```sh
dotnet run --project tests/SerenaPool.Smoke -- "C:/Users/QYJ/source/repos/temp"
```

检查会创建临时 Git worktree 和独立的 Serena 配置，使用真实 MCP 与语言服务验证会话隔离、队列释放、热复用、故障传播和 Windows 进程树清理。完成后删除本次夹具。
