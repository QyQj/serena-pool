using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace SerenaPool;

internal sealed record Acquisition(string instance_id, string workdir, string instructions);
internal sealed record LeaseView(long Number, DateTimeOffset Acquired, DateTimeOffset LastActivity);
internal sealed record WorkerView(long Number, string Workdir, int? Pid, string State, DateTimeOffset Started,
    DateTimeOffset? RecycleAt, bool Running, int Queued, string? LastError, LeaseView[] Leases);
internal sealed record CallLog(DateTimeOffset At, string Workdir, long Lease, string Status,
    double ElapsedMs, Dictionary<string, int>? Tools);
internal sealed record CallHistory(int Capacity, CallLog[] Calls);

internal sealed class InstancePool(PoolSettings settings, IHostApplicationLifetime lifetime, ILogger<InstancePool> log) : BackgroundService
{
    private sealed class Worker(long number, string path)
    {
        public readonly long Number = number;
        public readonly string Path = path;
        public readonly SemaphoreSlim Gate = new(1);
        public readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
        public readonly Dictionary<Guid, Lease> Leases = [];
        public readonly List<string> RetiredSessions = [];
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

    private sealed class CallActivity
    {
        public readonly DateTimeOffset At = DateTimeOffset.UtcNow;
        public readonly long Timestamp = Stopwatch.GetTimestamp();
        public string Status = "failed";
        public ReplUsage? Usage;
    }

    private const int CallHistoryCapacity = 200;
    private readonly Lock sync = new();
    private readonly Dictionary<string, Worker> workers = new(PoolSettings.PathComparer);
    private readonly Dictionary<Guid, Lease> leases = [];
    private readonly Queue<CallLog> calls = new(CallHistoryCapacity);
    private long nextNumber;

    public bool IsHostIdle()
    {
        lock (sync) return workers.Count == 0;
    }

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
                log.LogInformation("Worker {Worker} started (PID {Pid})", worker.Number, backend.Pid);
            }
            var (session, instructions) = await worker.Backend.OpenSessionAsync(path, lifetime.ApplicationStopping);
            lock (sync)
            {
                if (token.IsCancellationRequested)
                {
                    worker.RetiredSessions.Add(session);
                    token.ThrowIfCancellationRequested();
                }
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
        if (string.IsNullOrWhiteSpace(code)) throw new McpException(LocalText("EmptyCode"));
        Lease lease;
        lock (sync)
        {
            if (!leases.TryGetValue(id, out lease!)) throw new McpException(LocalText("ExpiredLease"));
            lease.Pending++;
            lease.Worker.Queued++;
        }
        var activity = settings.WebDashboard ? new CallActivity() : null;
        return RunAsync(lease.Worker, lease, token, async () =>
        {
            var (result, usage) = await lease.Worker.Backend!.CallAsync(lease.Session, code, lifetime.ApplicationStopping);
            if (activity is not null)
            {
                activity.Usage = usage;
                activity.Status = result.IsError == true || usage?.Failed == true ? "failed" :
                    usage is null ? "unverified" : "succeeded";
            }
            if (result.IsError == true)
                lock (sync) lease.Worker.Error = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
            return result;
        }, activity);
    }

    private async Task<T> RunAsync<T>(
        Worker worker, Lease? lease, CancellationToken request, Func<Task<T>> operation, CallActivity? activity = null)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(
            request, lease?.Released.Token ?? default, lifetime.ApplicationStopping);
        bool entered = false, started = false;
        try
        {
            await worker.Gate.WaitAsync(waiting.Token);
            entered = true;
            string[] retired;
            lock (sync)
            {
                waiting.Token.ThrowIfCancellationRequested();
                if (worker.Failed) throw new McpException(LocalText("FailedWorker"));
                worker.Queued--;
                worker.Running = started = true;
                retired = worker.RetiredSessions.ToArray();
                worker.RetiredSessions.Clear();
            }
            if (worker.Backend?.HasExited == true) throw new IOException(LocalText("SerenaDisconnected"));
            if (retired.Length > 0)
                await worker.Backend!.CloseSessionsAsync(retired, lifetime.ApplicationStopping);
            return await operation();
        }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (activity is not null) activity.Status = "canceled";
            if (lease?.Released.IsCancellationRequested == true) throw new McpException(LocalText("ReleasedLease"));
            throw;
        }
        catch (Exception ex) when (started)
        {
            if (activity is not null) activity.Status = "unknown";
            SerenaProcess? backend;
            lock (sync)
            {
                worker.Error = lease is null ? ex.Message : LocalText("UnknownOutcome", ex.Message);
                worker.Failed = true;
                foreach (var id in worker.Leases.Keys.ToArray()) RemoveLease(id);
                backend = worker.Backend;
                worker.Backend = null;
            }
            if (backend is null) log.LogWarning(ex, "Worker {Worker} could not start", worker.Number);
            else
            {
                log.LogWarning("Worker {Worker} failed ({ErrorType})", worker.Number, ex.GetType().Name);
                await backend.DisposeAsync();
            }
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
                if (lease is not null && activity is not null)
                {
                    if (calls.Count == CallHistoryCapacity) calls.Dequeue();
                    calls.Enqueue(new(activity.At, worker.Path, lease.Number, activity.Status,
                        Stopwatch.GetElapsedTime(activity.Timestamp).TotalMilliseconds, activity.Usage?.Tools));
                }
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
        lease.Worker.RetiredSessions.Add(lease.Session);
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

    public CallHistory RecentCalls()
    {
        lock (sync) return new(CallHistoryCapacity, calls.Reverse().ToArray());
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
        log.LogInformation("Worker {Worker} recycled by the dashboard", number);
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
                            w.Error = LocalText("WorkerExited");
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
