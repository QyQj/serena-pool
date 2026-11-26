using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SerenaPool;

[McpServerToolType]
internal sealed class PoolTools
{
    [McpServerTool(Name = "acquire", Title = "SerenaPool Acquire"), Description("Acquire a fresh Serena REPL session for an absolute checkout directory. Keep instance_id private, read the returned Serena instructions and use call.")]
    public static Task<Acquisition> Acquire(InstancePool pool, [Description("Absolute path of the project or worktree")] string workdir, CancellationToken cancellationToken) =>
        pool.AcquireAsync(workdir, cancellationToken);

    [McpServerTool(Name = "call", Title = "SerenaPool Call"), Description("Run Python code in the acquired Serena REPL. Use s.lsp for symbols and s.info for API help. Variables persist within this lease.")]
    public static Task<CallToolResult> Call(InstancePool pool, [Description("Private ID returned by acquire")] string instance_id,
        [Description("Serena Python REPL code")] string code, CancellationToken cancellationToken) =>
        pool.CallAsync(Parse(instance_id), code, cancellationToken);

    [McpServerTool(Name = "list", Title = "SerenaPool List", ReadOnly = true), Description("List local Serena workers, checkouts, public lease numbers and queued calls.")]
    public static WorkerView[] List(InstancePool pool) => pool.Snapshot();

    [McpServerTool(Name = "release", Title = "SerenaPool Release"), Description("Release a private instance_id. Queued calls are cancelled and an executing call may finish.")]
    public static bool Release(InstancePool pool, [Description("Private ID returned by acquire")] string instance_id) =>
        pool.Release(Parse(instance_id));

    private static Guid Parse(string id) => Guid.TryParseExact(id, "N", out var value)
        ? value : throw new McpException("instance_id 无效。");
}
