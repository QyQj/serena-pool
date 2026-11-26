using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SerenaPool;

[McpServerToolType]
internal sealed class PoolTools
{
    [McpServerTool(Name = "acquire", Title = "SerenaPool Acquire"), LocalizedDescription("AcquireToolDescription")]
    public static Task<Acquisition> Acquire(InstancePool pool, [LocalizedDescription("ToolWorkdir")] string workdir, CancellationToken cancellationToken) =>
        pool.AcquireAsync(workdir, cancellationToken);

    [McpServerTool(Name = "call", Title = "SerenaPool Call"), LocalizedDescription("CallToolDescription")]
    public static Task<CallToolResult> Call(InstancePool pool, [LocalizedDescription("ToolInstanceId")] string instance_id,
        [LocalizedDescription("ToolCode")] string code, CancellationToken cancellationToken) =>
        pool.CallAsync(Parse(instance_id), code, cancellationToken);

    [McpServerTool(Name = "list", Title = "SerenaPool List", ReadOnly = true), LocalizedDescription("ListToolDescription")]
    public static WorkerView[] List(InstancePool pool) => pool.Snapshot();

    [McpServerTool(Name = "release", Title = "SerenaPool Release"), LocalizedDescription("ReleaseToolDescription")]
    public static bool Release(InstancePool pool, [LocalizedDescription("ToolInstanceId")] string instance_id) =>
        pool.Release(Parse(instance_id));

    private static Guid Parse(string id) => Guid.TryParseExact(id, "N", out var value)
        ? value : throw new McpException(LocalText("InvalidInstanceId"));
}
