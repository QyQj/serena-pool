using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace SerenaPool;

internal sealed record Acquisition(string instance_id, string workdir, string instructions);
internal sealed record LeaseView(long Number, DateTimeOffset Acquired, DateTimeOffset LastActivity);
internal sealed record WorkerView(long Number, string Workdir, int? Pid, string State, DateTimeOffset Started,
    DateTimeOffset? RecycleAt, bool Running, int Queued, string? LastError, LeaseView[] Leases);

internal sealed class InstancePool(PoolSettings settings, IHostApplicationLifetime lifetime) : BackgroundService
{
    private sealed class Worker(long number, string path)
    {
        public readonly long Number = number;
        public readonly string Path = path;
        public readonly SemaphoreSlim Gate = new(1);
        public readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
        public readonly Dictionary<Guid, Lease> Leases = [];
        public DateTimeOffset LastActivity = DateTimeOffset.UtcNow;
        public SerenaProcess? Backend;
        public int Queued;
        public bool Running, Failed;
        public string? Error;
    }

    private sealed class Lease(Worker worker, string session, long number)
    {
        public Worker Worker => worker;
        public string Session => session;
        public long Number => number;
        public readonly CancellationTokenSource Released = new();
        public readonly DateTimeOffset Acquired = DateTimeOffset.UtcNow;
        public DateTimeOffset LastActivity = DateTimeOffset.UtcNow;
        public int Pending;
    }

    private readonly Lock sync = new();
    private readonly Dictionary<string, Worker> workers = new(PoolSettings.PathComparer);
    private readonly Dictionary<Guid, Lease> leases = [];
    private long nextNumber;

    public Task<Acquisition> AcquireAsync(string workdir, CancellationToken token)
    {
        string path;
        try { path = PoolSettings.ResolveWorkdir(workdir); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { throw new McpException(ex.Message); }
        Worker worker;
        lock (sync)
        {
            if (!workers.TryGetValue(path, out var existing) || existing.Failed)
                workers[path] = worker = new(++nextNumber, path);
            else worker = existing;
            worker.Queued++;
        }
        return RunAsync(worker, null, token, async () =>
        {
            if (worker.Backend is null)
            {
                var backend = await SerenaProcess.StartAsync(path, settings, lifetime.ApplicationStopping);
                lock (sync) worker.Backend = backend;
            }
            var (session, instructions) = await worker.Backend.OpenSessionAsync(path, lifetime.ApplicationStopping);
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                var id = Guid.NewGuid();
                var lease = new Lease(worker, session, ++nextNumber);
                worker.Leases.Add(id, lease);
                leases.Add(id, lease);
                return new Acquisition(id.ToString("N"), path, instructions);
            }
        });
    }

    public Task<CallToolResult> CallAsync(Guid id, string code, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new McpException("code 不能为空。");
        Lease lease;
        lock (sync)
        {
            if (!leases.TryGetValue(id, out lease!)) throw new McpException("领用已失效，请重新 acquire。");
            lease.Pending++;
            lease.Worker.Queued++;
        }
        return RunAsync(lease.Worker, lease, token, async () =>
        {
            var result = await lease.Worker.Backend!.CallAsync(lease.Session, code, lifetime.ApplicationStopping);
            if (result.IsError == true)
                lock (sync) lease.Worker.Error = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
            return result;
        });
    }

    private async Task<T> RunAsync<T>(Worker worker, Lease? lease, CancellationToken request, Func<Task<T>> operation)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(
            request, lease?.Released.Token ?? default, lifetime.ApplicationStopping);
        bool entered = false, started = false;
        try
        {
            await worker.Gate.WaitAsync(waiting.Token);
            entered = true;
            lock (sync)
            {
                waiting.Token.ThrowIfCancellationRequested();
                if (worker.Failed) throw new McpException("Serena 进程已失效，请重新 acquire。");
                worker.Queued--;
                worker.Running = started = true;
            }
            if (worker.Backend?.HasExited == true) throw new IOException("Serena 已断开连接。");
            return await operation();
        }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (lease?.Released.IsCancellationRequested == true) throw new McpException("领用已释放，排队的调用已取消。");
            throw;
        }
        catch (Exception ex) when (started)
        {
            SerenaProcess? backend;
            lock (sync)
            {
                worker.Error = lease is null ? ex.Message : $"Serena 连接中断，执行结果未知：{ex.Message}";
                worker.Failed = true;
                foreach (var id in worker.Leases.Keys.ToArray()) RemoveLease(id);
                backend = worker.Backend;
                worker.Backend = null;
            }
            if (backend is not null) await backend.DisposeAsync();
            throw new McpException(worker.Error, ex);
        }
        finally
        {
            lock (sync)
            {
                if (started) worker.Running = false;
                else worker.Queued--;
                worker.LastActivity = DateTimeOffset.UtcNow;
                if (lease != null) { lease.Pending--; lease.LastActivity = worker.LastActivity; }
            }
            if (entered) worker.Gate.Release();
        }
    }

    public bool Release(Guid id)
    {
        lock (sync) return RemoveLease(id);
    }

    private bool RemoveLease(Guid id)
    {
        if (!leases.Remove(id, out var lease)) return false;
        lease.Worker.Leases.Remove(id);
        lease.Released.Cancel();
        lease.Worker.LastActivity = DateTimeOffset.UtcNow;
        return true;
    }

    public bool ReleaseByNumber(long number)
    {
        lock (sync) return RemoveLease(leases.FirstOrDefault(pair => pair.Value.Number == number).Key);
    }

    public WorkerView[] Snapshot()
    {
        lock (sync)
            return workers.Values.OrderBy(w => w.Number).Select(w =>
            {
                var state = w.Failed ? "failed" : w.Backend == null ? "starting" :
                    w.Leases.Count == 0 && !w.Running && w.Queued == 0 ? "idle" : "active";
                return new WorkerView(w.Number, w.Path, w.Backend?.Pid, state, w.Started,
                    state == "idle" ? w.LastActivity + settings.IdleTimeout : null, w.Running, w.Queued, w.Error,
                    w.Leases.Values.Select(l => new LeaseView(l.Number, l.Acquired, l.LastActivity)).ToArray());
            }).ToArray();
    }

    public async Task<bool> RecycleAsync(long number)
    {
        Worker? worker;
        lock (sync)
        {
            worker = workers.Values.FirstOrDefault(w => w.Number == number);
            if (worker == null || worker.Leases.Count != 0 || worker.Queued != 0 || worker.Running) return false;
            workers.Remove(worker.Path);
        }
        if (worker.Backend != null) await worker.Backend.DisposeAsync();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                List<SerenaProcess> closed = [];
                lock (sync)
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var (id, lease) in leases.ToArray())
                        if (lease.Pending == 0 && now - lease.LastActivity >= settings.LeaseTimeout) RemoveLease(id);
                    foreach (var w in workers.Values.ToArray())
                    {
                        if (w.Running || w.Queued != 0) continue;
                        if (w.Backend?.HasExited == true)
                        {
                            foreach (var id in w.Leases.Keys.ToArray()) RemoveLease(id);
                            closed.Add(w.Backend);
                            w.Backend = null;
                            w.Failed = true;
                            w.Error = "Serena 进程已退出，此进程的领用已失效。";
                            w.LastActivity = now;
                        }
                        if (w.Leases.Count == 0 && now - w.LastActivity >= settings.IdleTimeout)
                        {
                            workers.Remove(w.Path);
                            if (w.Backend != null) closed.Add(w.Backend);
                        }
                    }
                }
                foreach (var backend in closed) await backend.DisposeAsync();
            }
        }
        finally
        {
            Worker[] remaining;
            lock (sync) { remaining = workers.Values.ToArray(); workers.Clear(); }
            foreach (var worker in remaining)
            {
                await worker.Gate.WaitAsync();
                try { if (worker.Backend != null) await worker.Backend.DisposeAsync(); }
                finally { worker.Gate.Release(); }
            }
        }
    }
}
