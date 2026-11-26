# Serena Pool

**English** | [简体中文](https://github.com/QyQj/serena-pool/blob/main/README.zh-CN.md)

Serena Pool lets multiple local MCP clients and coding agents share [Serena](https://github.com/oraios/serena) processes on the same machine. It runs at most one Serena process per project or Git worktree directory, gives each caller an isolated Python REPL session, exposes both `stdio` and Streamable HTTP MCP transports, and includes a local Web dashboard for inspecting workers and leases. Supports Windows, Linux, and macOS.

## Features

- **One worker per checkout / worktree**: Resolves `workdir` to its canonical physical path (including symlinks and junctions), so clients targeting the same repository reuse one warm Serena and language server process while separate Git worktrees run concurrently.
- **Isolated REPL sessions (`leases`)**: Every `acquire(workdir)` opens a separate Serena REPL session with a private `instance_id`, keeping Python variables isolated per agent without duplicating language servers.
- **Serialized per-worker execution**: Calls targeting the same directory are queued and executed sequentially through a per-worker gate, preventing concurrent REPL or LSP state collisions.
- **Shared `stdio` and Streamable HTTP pool**. The background manager serves both transports from one pool. Each `stdio` connection uses its own bridge to the manager over a local named pipe.
- **One-command setup for 7 clients**: `serena-pool setup` registers the `serena-pool` MCP server and installs the companion `serena-pool` skill with pre-write conflict checks and atomic rollback.
- **Local Web dashboard**: Monitor worker status, PIDs, queues, errors, and leases at `http://127.0.0.1:32980/`, release stale leases, and recycle idle workers. On Windows, open the dashboard from the notification area.
- **Process-tree cleanup**: Contains spawned Serena and language server processes in a Windows Job Object (`KILL_ON_JOB_CLOSE`) on Windows or a dedicated POSIX process group with a parent-watching launcher on Linux and macOS.

## How It Works

```text
  MCP Client
      │ stdio
      ▼
  Client Bridge Process (serena-pool mcp)
      │ named pipe
      ▼
  ┌──────────────────────────────────────────────────────┐
  │ ASP.NET Core Background Process (--background-serve) │
  │ LocalPipeServer ─► PoolTools ─► InstancePool         │──► Serena Worker Process ─► Language Server
  └──────────────────────────────────────────────────────┘
```

1. **On-demand background host**. All `serena-pool mcp` bridges default to `http://127.0.0.1:32980`. Each bridge tries the pipe `serena-pool-<user>-<port>` using the configured port. If the connection times out, it attempts to start a detached `--background-serve` host at the same address and port. When concurrent starts target the same loopback IP address and TCP port, only one host can bind that endpoint. Different loopback addresses on the same port still use the same pipe name for a given user. Use different ports for independent pools.
2. **Per-directory workers**. The pipe server and instance pool run inside the ASP.NET Core background process. When `PoolTools` handles `acquire(workdir)`, `InstancePool` starts a Serena worker (`serena start-mcp-server --transport stdio --project <workdir> --context ide --agent-interface repl --add-mode no-memories --enable-web-dashboard false --enable-gui-log-window false`) if the directory has no available worker. Each acquisition opens a new REPL session on that worker.
3. **Failure isolation**: If a Serena worker exits unexpectedly or disconnects mid-call, only that worker transitions to `failed` and invalidates its leases; any in-flight call fails with an unknown-outcome error so the caller can inspect state before retrying, while workers for other directories stay alive.
4. **Automatic recycling & shutdown**: Inactive leases expire after `Pool__LeaseTimeout` (paused while calls are queued or executing). Idle workers with zero leases and zero pending calls shut down after `Pool__IdleTimeout`. Once all `stdio` clients disconnect and all workers are recycled, the auto-started background host exits cleanly (pipe idle check runs every 15 seconds).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or newer (includes the ASP.NET Core 10 runtime).
- [Serena](https://github.com/oraios/serena) with REPL interface support (`--agent-interface repl`, exposing `initial_instructions` and `serena_repl`).
- Serena Pool reads the required session parameter from the `serena_repl` schema at worker startup. Serena 2.0.0 declares `session_id` and Serena 2.1.0 declares `session`. MCP clients pass the Pool lease as `instance_id`.
- Install `serena` (`serena.exe` on Windows). `setup` checks `PATH` and user installation directories, or accepts `Pool__Executable` as an explicit executable path or containing directory.

## Installation & Quick Setup

Run the preview package from [NuGet](https://www.nuget.org/packages/SerenaPool) directly with `dnx` or `dotnet tool exec` (the example below configures Codex):

```sh
dnx SerenaPool --prerelease -- setup codex
```

Or with `dotnet tool exec`:

```sh
dotnet tool exec SerenaPool --prerelease -- setup codex
```

You can also install it as a global .NET tool and run `serena-pool` directly:

```sh
dotnet tool install --global SerenaPool --prerelease
serena-pool setup codex
```

> [!NOTE]
> When running `dotnet tool exec` interactively on .NET 11 or newer SDKs, pass `--yes` before `--` to skip the confirmation prompt. Inside generated MCP client configs, `serena-pool setup` automatically includes `--yes` when it detects .NET 11+.

### Upgrading

Run `setup` from the new package to update the pinned version in each configured client. For Codex:

```sh
dotnet tool exec SerenaPool@0.1.0-preview.7 -- setup codex
```

Include all installed clients that share the same skill directory in the same command, for example `setup codex cursor`. If you prefer using the global command, first run `dotnet tool update --global SerenaPool --version 0.1.0-preview.7`, then run `serena-pool setup codex`.

Release active leases and close clients connected to the old shared pool. After the old background process exits, start a new client session. Updating the installed command alone does not change the pinned client configurations.

### Supported Clients (`setup`)

Pass one or more client IDs after `setup` (e.g., `serena-pool setup codex cursor`), or omit arguments in an interactive terminal to choose from a detected list:

| Client ID | MCP Registration Target | Skill Path | Notes |
| --- | --- | --- | --- |
| `codex` | `$CODEX_HOME/config.toml` (default `~/.codex/config.toml`) | `~/.agents/skills/serena-pool/SKILL.md` | Writes `[mcp_servers.serena-pool]`. |
| `claude-code` | `$CLAUDE_CONFIG_DIR/.claude.json` (default `~/.claude.json`) | `$CLAUDE_CONFIG_DIR/skills/serena-pool/SKILL.md` (default `~/.claude/skills/serena-pool/SKILL.md`) | Runs `claude mcp add --transport stdio --scope user`; requires native `claude` on `PATH`. |
| `cursor` | `~/.cursor/mcp.json` | `~/.agents/skills/serena-pool/SKILL.md` | Writes `mcpServers.serena-pool` (`type: "stdio"`). |
| `copilot` | `$COPILOT_HOME/mcp-config.json` (default `~/.copilot/mcp-config.json`) | `~/.agents/skills/serena-pool/SKILL.md` | Shared by Copilot CLI and VS Code native Copilot. |
| `opencode` | `$OPENCODE_CONFIG`, or `$XDG_CONFIG_HOME/opencode/opencode.json[c]` (default `~/.config/opencode/opencode.json`) | `~/.agents/skills/serena-pool/SKILL.md` | Writes `mcp.serena-pool` with `type: "local"` and a `command` array. |
| `antigravity` | `~/.gemini/config/mcp_config.json` | `~/.gemini/config/skills/serena-pool/SKILL.md` | Writes `mcpServers.serena-pool`. |
| `gemini-cli` | `$GEMINI_CLI_HOME/.gemini/settings.json` (default `~/.gemini/settings.json`) | `~/.agents/skills/serena-pool/SKILL.md` | Writes `mcpServers.serena-pool`. |

`setup` checks all selected targets before modifying any files:
- Deduplicates shared skill paths (for example, `codex`, `cursor`, `copilot`, `opencode`, and `gemini-cli` share `~/.agents/skills/serena-pool/SKILL.md`) and tracks managed state in `~/.serena-pool/setup.json`.
- Preserves existing unrelated MCP entries and any custom `--urls` flags already configured for `serena-pool`. In Codex, setup adds the Serena Pool child table under the existing `[mcp_servers]` table.
- Aborts before writing if it finds a conflicting external `serena-pool` entry, a locally edited skill file, or another registered client sharing the same skill path on a different `SerenaPool` version (in which case, upgrade those clients together).
- Runs the selected Serena executable to check REPL support before writing client files.
- Writes the absolute Serena path as `Pool__Executable` in each MCP entry and removes legacy Serena Pool credential settings from existing managed entries.
- Backs up an existing MCP configuration before modifying it. Backup filenames end in `.SerenaPool` and receive a numbered suffix before that extension if the name is taken.
- Rolls back atomically if any file write fails mid-operation.

After setup finishes, it prints the package version, each registered config and skill path, the managed MCP settings written, any backup path, and the runtime addresses. Start a new session in your client to load the MCP server and skill.

## Connecting & Managing

### CLI Commands

```sh
serena-pool mcp [--urls http://127.0.0.1:32980]
serena-pool setup [harness ...]
serena-pool --help
```

### MCP Transports

- **stdio**: For manual client configuration, set the command to `serena-pool` (after global install) with `["mcp"]`, or use the pinned `dotnet tool exec SerenaPool@<version> -- mcp` command generated by `setup`:

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

- **Streamable HTTP**: Local clients can connect to `http://127.0.0.1:32980/mcp` directly. Requests must come from loopback and use a loopback Host. Browser requests must have a matching Origin.

- **Custom ports**: Pass the same `--urls http://127.0.0.1:<port>` flag to `serena-pool mcp` for every stdio client that should share a pool, and use that port for the Web dashboard and `/mcp` endpoint. `--urls` must be a loopback `http` or `https` URL with a fixed non-zero port and no path, userinfo, query string, or fragment.

### MCP Tools & REPL Workflow

Clients that display MCP tool titles show names such as `SerenaPool Acquire`. The call identifiers are listed below.

| MCP Tool | Description |
| --- | --- |
| `list()` | Read-only snapshot of workers (`number`, `workdir`, `pid`, `state`, `started`, `recycleAt`, `running`, `queued`, `lastError`) and public lease numbers (never exposes private `instance_id`s). |
| `acquire(workdir)` | Takes an existing absolute project or worktree path, ensures a worker is running for that directory, and returns `{ instance_id, workdir, instructions }`. Keep `instance_id` private per agent/session. |
| `call(instance_id, code)` | Runs Python code inside the acquired Serena REPL session. Variables persist within the lease. Use `s.lsp` for symbol operations and `s.info(...)` for API signatures. |
| `release(instance_id)` | Releases the lease and cancels any queued calls belonging to it (an already-executing call finishes). Returns `true` if released, or `false` if already gone. |

Example REPL calls inside `call(instance_id, code)`:

```python
# Inspect available Serena LSP signatures
s.info("lsp.find_symbol", "lsp.find_referencing_symbols")
```

```python
# Query symbols in the acquired checkout (variables persist across call() invocations)
symbols = s.lsp.find_symbol(
    "InstancePool",
    relative_path="src/SerenaPool/InstancePool.cs",
    include_body=False,
)
symbols
```

If a lease expires or its worker restarts, call `acquire(workdir)` again and read the returned `instructions` before making further calls (previous REPL variables are lost with the old session).

### Web Dashboard

Open [http://127.0.0.1:32980/](http://127.0.0.1:32980/) in your browser (replace the port if using `--urls`).

- **Local-only access**: Only loopback connections, `127.0.0.1`/`localhost` hosts, and matching `Origin` headers are accepted; non-local or cross-origin requests receive `403 Forbidden`.
- **No token required**: Viewing worker/lease status, releasing leases (`DELETE /api/leases/{number}`), and recycling idle or failed workers (`DELETE /api/workers/{number}`) require no token and prompt for confirmation before acting.
- **Call log and REPL usage**: `/api/calls` lists the last 200 completed REPL requests in memory with the working directory, public lease number, status, and elapsed time including queueing. The dashboard ranks the Serena facade and REPL methods actually invoked across those requests and reports how many calls provided traces. The log retains only these metadata fields and the invoked tool names and counts. Restarting the pool clears the history.
- **Live updates**: Polls worker status and call history every 2.5 seconds while the page is visible. Closing the browser tab does not stop the pool host.

The dashboard is available on Windows, macOS, and Linux. On Windows, select the Serena Pool notification-area icon to open it. Each shared pool has one notification-area icon. The dashboard uses the project icon in the browser and stays enabled by default.

To disable the page, its management API, call tracing, and the tray icon, add `Pool__WebDashboard = "false"` to your MCP server's environment configuration. Both MCP transports remain available. To open the enabled page automatically when the shared pool starts, set `Pool__WebDashboardOpenOnLaunch = "true"`.

## Environment Variables

Serena Pool uses the system UI language for CLI output, native diagnostics, and MCP messages. Set `SERENA_POOL_LANGUAGE` to `en` or `zh-CN` to override it. The dashboard uses the browser language and offers a switch in its header.

Set these variables in your MCP client configuration. The shared background host reads them at startup. Restart the background host after changing its configuration or upgrading the package.

| Environment Variable | Default | Description |
| --- | --- | --- |
| `SERENA_POOL_LANGUAGE` | System UI language (English for unsupported languages) | Selects English or Simplified Chinese for CLI output and pool messages. |
| `Pool__Executable` | Searches `PATH`, `UV_TOOL_BIN_DIR`, `XDG_BIN_HOME`, and `~/.local/bin` in order | Absolute path to the Serena executable or its containing directory. |
| `Pool__IdleTimeout` | `00:10:00` | How long a worker stays alive after all leases are released and no calls are running or queued (must be `> 0`). |
| `Pool__WebDashboard` | `true` | Enables the dashboard, management API, and Windows tray icon. |
| `Pool__WebDashboardOpenOnLaunch` | `false` | Opens the dashboard in the browser on shared pool startup when enabled. |
| `Pool__LeaseTimeout` | `01:00:00` | Inactivity timeout for an open lease; timer pauses while calls are queued or executing (must be `> 0`). |

## Building, Testing & Packaging from Source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (see `global.json`). Running the smoke test also requires `git` and a working `serena` executable on `PATH`.

```sh
# Build the solution
dotnet build SerenaPool.slnx -c Release

# Pack the NuGet tool package (outputs to src/SerenaPool/bin/Release)
dotnet pack src/SerenaPool -c Release

# Verify the locally packed tool
dotnet tool exec SerenaPool@0.1.0-preview.7 --source src/SerenaPool/bin/Release -- --help

# Run the end-to-end smoke test against a writable temporary directory
dotnet run --project tests/SerenaPool.Smoke -c Release -- "<absolute-path-to-writable-temp-dir>"
```

The smoke test creates temporary Git worktrees and an isolated `SERENA_HOME` inside the directory you pass, then verifies real Serena language servers, `stdio` and Streamable HTTP transports, lease isolation, queued call cancellation, dashboard APIs, worker crash isolation, process-tree cleanup, and automatic idle shutdown.

To publish a framework-dependent single-file executable (replace `win-x64` with `linux-x64`, `linux-arm64`, `osx-x64`, or `osx-arm64`):

```sh
dotnet publish src/SerenaPool -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## License

[MIT](https://github.com/QyQj/serena-pool/blob/main/LICENSE)
