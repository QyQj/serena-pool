# Serena Pool

Serena Pool 是 Windows、Linux 和 macOS 的本机 Serena 多实例管理器。每个工作目录按需启动一个 Serena 进程，每次领用建立独立的 REPL 会话。stdio 客户端、HTTP 客户端和 Web 管理页共享同一实例池。

## 启动与连接

部署需要对应平台和架构的 ASP.NET Core 10 运行时，以及支持 REPL 的 Serena。源码开发使用 .NET 10 SDK。Windows 运行 `SerenaPool.exe`，Linux 和 macOS 运行 `./SerenaPool`。从 Windows 复制到 Unix 系统后，先执行 `chmod +x ./SerenaPool`。

### 配置 stdio

MCP 客户端使用可执行文件的绝对路径作为 `command`，参数为 `["stdio"]`。例如在 Linux 或 macOS 的安装目录中向 Codex 注册：

```sh
codex mcp add serena-pool -- "$(pwd)/SerenaPool" stdio
```

或在支持 `mcpServers` 的客户端中配置：

```json
{
  "mcpServers": {
    "serena-pool": {
      "command": "/absolute/path/to/SerenaPool",
      "args": ["stdio"]
    }
  }
}
```

Windows 的 `command` 填写 `SerenaPool.exe` 的绝对路径。每个客户端通过自己的 stdin/stdout 收发 MCP 消息，本机管道校验操作系统用户身份。首次连接会自动启动管理器，后续客户端复用它。最后一个客户端退出且池内实例全部回收后，自动启动的管理器会在下一次空闲检查时退出。默认空闲检查周期为 15 秒。

### 前台运行与 Web

运行 `SerenaPool serve` 可以将管理器保持在前台，按 Ctrl+C 关闭。无参数启动也进入前台模式。源码运行命令为：

```sh
dotnet run --project src/SerenaPool -- serve
```

两种启动方式都提供 [Web 管理页](http://127.0.0.1:47321/)。运行 `SerenaPool token-path` 查看当前系统的默认凭证路径。Windows 上可在 Niubash 中复制令牌并粘贴到管理页：

```sh
clip.exe < "$LOCALAPPDATA/SerenaPool/access-token"
```

HTTP 客户端使用 Streamable HTTP 连接 `http://127.0.0.1:47321/mcp`，请求头使用 `Authorization: Bearer <令牌>`。当前用户的 stdio 客户端和持有该令牌的 HTTP 客户端均可运行 Serena REPL 和管理本机实例。网页令牌仅在当前页面内存中保存。自定义端口时，给 `serve` 与 `stdio` 传入相同的 `--urls http://127.0.0.1:端口`。

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
| `Pool__Executable` | Windows 为 `serena.exe`，Unix 为 `serena` | 从管理器的 PATH 查找。桌面客户端的 PATH 缺少 Serena 时，配置此变量为可执行文件的绝对路径。 |
| `Pool__IdleTimeout` | `00:10:00` | 最后一个领用释放且无调用后保留进程的时间。 |
| `Pool__LeaseTimeout` | `01:00:00` | 回收无活动领用的时间。排队和执行期间暂停计时。 |
| `SerenaPool__Token` | 当前用户凭证文件 | 可由启动环境显式提供访问令牌。 |

进程退出后，旧领用失效。调用已发出时连接中断，管理器返回“执行结果未知”。Windows 用 Job Object 回收子进程树。Linux 和 macOS 将每个 Serena 放入独立的 POSIX 进程组，监督进程在管理器或 Serena 退出后清理组内进程。自行脱离进程组的第三方子进程超出该机制的回收范围。

## 发布与验证

按目标平台发布框架依赖的单文件程序，管理页已嵌入。可选 RID 包括 `win-x64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`：

```sh
dotnet publish src/SerenaPool -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

在项目根目录执行端到端检查，参数是可写的临时目录：

```sh
dotnet run --project tests/SerenaPool.Smoke -- "C:/Users/QYJ/source/repos/temp"
```

在 Linux 和 macOS 上，将测试参数换成可写的临时目录绝对路径。检查会创建临时 Git worktree 和独立的 Serena 配置，通过真实 stdio、HTTP 和语言服务验证共享池、会话隔离、队列释放、热复用、故障传播和进程清理。完成后删除本次夹具。
