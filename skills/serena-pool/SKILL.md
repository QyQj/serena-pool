---
name: serena-pool
description: Use the local Serena Pool MCP server to inspect symbols, references, and source structure in a project or worktree. Apply when Serena-backed semantic navigation will help with the current coding task.
---

# Serena Pool

First inspect the MCP tools available in this session. Find the four tools published by the Serena Pool server. Tool names can be prefixed by the host. If the server is absent, report that Serena Pool is unavailable and use the project's existing code navigation workflow.

- Use `list()` to inspect local workers, project paths, public lease numbers, and queued calls.
- Call `acquire(workdir)` with the absolute path of the current checkout. Verify the returned `workdir`, read `instructions`, and keep the returned `instance_id` private. Use a separate lease for each agent and checkout.
- Call `call(instance_id, code)` with Python REPL code. The Serena symbol interface is `s.lsp`. Use `s.info("lsp.find_symbol", "lsp.find_referencing_symbols")` for signatures. Resolve the exact overload and relative path before looking up references. Reuse the lease for the same checkout and keep memory tools disabled.
- Call `release(instance_id)` when done or before switching checkouts. If a lease expires or its worker restarts, acquire a new lease and read its instructions. REPL variables are lost with the old session. If a call might already have run, check the resulting state before repeating it.
