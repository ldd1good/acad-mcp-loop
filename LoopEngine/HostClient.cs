using System.IO.Pipes;

namespace LoopEngine;

public sealed class HostClient
{
    public const string BasePipeName = "HotReloadCAD.LoopHost.v1";

    private const int BusyProbeRetries = 6;
    private const int BusyProbeDelayMs = 200;
    private const int BusyCallRetries = 40;
    private const int BusyCallDelayMs = 250;

    private const int PipeIoRetries = 4;
    private const int PipeIoDelayMs = 200;

    private const int ConnectTimeoutMs = 2000;
    private const int ConnectRetries = 8;
    private const int ConnectRetryDelayMs = 250;

    public sealed class CallResult
    {
        public bool Ok;
        public JsonNode? Data;
        public string? ErrorCode;
        public string? ErrorMessage;
        public long? ElapsedMs;
    }

    public sealed class HostInstance
    {
        public int Pid;
        public string PipeName = "";
        public CallResult? Status;

        public bool Degraded;
    }

    public static bool IsReachable(int connectTimeoutMs = 1500)
    {
        var gate = TryEnterHostGate(out var lease);
        if (gate != null) return false;
        try { return Discover(connectTimeoutMs).Count > 0; }
        finally { ExitHostGate(lease); }
    }

    private static long _gateOwnerTick = 0;

    private const int HostGateWaitMs = 45000;

    private const int HostGateMaxHoldMs = 120000;

    public static CallResult? TryEnterHostGate(out long lease)
    {
        lease = 0;
        long deadline = Environment.TickCount64 + HostGateWaitMs;
        while (true)
        {
            long now = Environment.TickCount64;
            long owner = Interlocked.Read(ref _gateOwnerTick);

            if (owner == 0 || now - owner > HostGateMaxHoldMs)
            {
                if (Interlocked.CompareExchange(ref _gateOwnerTick, now, owner) == owner)
                {
                    if (owner != 0)
                        Program.Diag("gate", $"watchdog preempted holder (lease held {now - owner}ms > {HostGateMaxHoldMs}ms)");
                    lease = now;
                    return null;
                }
                continue;
            }
            if (Environment.TickCount64 >= deadline)
            {
                Program.Diag("gate", $"queue expired after {HostGateWaitMs}ms (holder lease age {now - owner}ms)");

                return Fail("HOST_BUSY_SERIALIZE",
                    $"cad-loop host channel is busy: a host call is still in flight after queueing " +
                    $"{HostGateWaitMs / 1000}s. Calls auto-serialize and queue; on expiry, serialize " +
                    "your calls (await each tool before issuing the next), or poll get_iteration / " +
                    "the get_loop_status busy fast-path instead.");
            }
            Thread.Sleep(50);
        }
    }

    public static void ExitHostGate(long lease)
    {
        if (lease != 0) Interlocked.CompareExchange(ref _gateOwnerTick, 0, lease);
    }

    public static CallResult Call(string tool, JsonNode? payload, int timeoutMs = 55000)
    {

        var gate = TryEnterHostGate(out var lease);
        if (gate != null) return gate;
        try
        {
            return CallCore(tool, payload, timeoutMs);
        }
        finally { ExitHostGate(lease); }
    }

    private static CallResult CallCore(string tool, JsonNode? payload, int timeoutMs)
    {
        var instances = DiscoverCached();
        for (int retry = 0; instances.Count == 0 && retry < 2; retry++)
        {
            Thread.Sleep(300);
            instances = Discover();
        }
        if (instances.Count == 0)
            return Fail("CAD_NOT_ATTACHED", DescribeOffline());

        var chosen = SelectBest(instances);
        var result = CallOn(chosen.PipeName, tool, payload, timeoutMs);

        int budget = RetryBudgetFor(result);
        for (int retry = 0; retry < budget; retry++)
        {
            Thread.Sleep(IsBusy(result) ? BusyCallDelayMs : PipeIoDelayMs);
            result = CallOn(chosen.PipeName, tool, payload, timeoutMs);
            if (result.Ok || RetryBudgetFor(result) == 0) break;
        }
        if (!result.Ok) InvalidateDiscoverCache();
        return result;
    }

    // 发现结果短缓存：CallCore 每次调用都枚举整个 \\.\pipe\ 目录（可达百 ms 级），
    // 而一次迭代的 10+ 次宿主调用几乎全打同一实例。2s TTL + 失败即失效；
    // attach/recovery 流程仍走无缓存的 Discover()。空结果不缓存——离线时保持逐次探测
    private static readonly object _discCacheLock = new();
    private static List<HostInstance>? _discCache;
    private static long _discCacheTick;
    private const long DiscoverCacheTtlMs = 2000;

    private static List<HostInstance> DiscoverCached()
    {
        List<HostInstance>? cached;
        lock (_discCacheLock)
        {
            cached = _discCache != null && Environment.TickCount64 - _discCacheTick < DiscoverCacheTtlMs
                ? _discCache : null;
        }
        if (cached != null) return cached;

        var fresh = Discover();
        if (fresh.Count > 0)
        {
            lock (_discCacheLock)
            {
                _discCache = fresh;
                _discCacheTick = Environment.TickCount64;
            }
        }
        return fresh;
    }

    private static void InvalidateDiscoverCache()
    {
        lock (_discCacheLock) { _discCache = null; }
    }

    private static bool IsBusy(CallResult r)
        => string.Equals(r.ErrorCode, "HOST_BUSY_OTHER_CLIENT", StringComparison.Ordinal);

    private static bool IsPipeIo(CallResult r)
        => string.Equals(r.ErrorCode, "PIPE_IO", StringComparison.Ordinal);

    private static int RetryBudgetFor(CallResult r)
        => IsBusy(r) ? BusyCallRetries : (IsPipeIo(r) ? PipeIoRetries : 0);

    public static CallResult CallOn(string pipeName, string tool, JsonNode? payload, int timeoutMs = 55000)
    {
        var result = CallOnce(pipeName, tool, payload, timeoutMs);
        if (string.Equals(result.ErrorCode, "AUTH_FAILED", StringComparison.Ordinal)
            && TokenStore.Evict(PidFromPipeName(pipeName)))
        {

            result = CallOnce(pipeName, tool, payload, timeoutMs);
        }
        return result;
    }

    private static CallResult CallOnce(string pipeName, string tool, JsonNode? payload, int timeoutMs)
    {

        int budget = timeoutMs + (ConnectRetries + 1) * ConnectTimeoutMs
            + ConnectRetries * ConnectRetryDelayMs + 5000;
        var task = System.Threading.Tasks.Task.Run(
            () => CallOnceCore(pipeName, tool, payload, timeoutMs));
        if (!task.Wait(budget))
        {
            Program.Diag("hostcall", $"{tool} on {pipeName}: TOTAL budget {budget}ms exhausted — "
                + "native pipe call wedged (kernel-level); TIMEOUT returned, worker thread leaked");
            return Fail("TIMEOUT",
                $"host call exceeded total budget {budget}ms (native pipe hang suspected; "
                + "retry once, then check acad / restart the MCP session)");
        }
        return task.GetAwaiter().GetResult();
    }

    private static CallResult CallOnceCore(string pipeName, string tool, JsonNode? payload, int timeoutMs)
    {
        var request = new JsonObject
        {
            ["id"] = Random.Shared.Next(1, int.MaxValue),
            ["tool"] = tool,
            ["payload"] = payload?.DeepClone() ?? new JsonObject()
        };
        var token = TokenStore.Get(PidFromPipeName(pipeName));
        if (token != null) request["token"] = token;

        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            ConnectWithRetry(pipe);
            pipe.ReadMode = PipeTransmissionMode.Byte;

            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            {
                writer.WriteLine(request.ToJsonString());
            }

            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            {

                var sb = new StringBuilder();
                var buffer = new char[8192];
                while (sb.Length < 4 * 1024 * 1024)
                {
                    var readTask = reader.ReadAsync(buffer, 0, buffer.Length);
                    if (!readTask.Wait(timeoutMs))
                    {
                        Program.Diag("hostcall", $"{tool} on {pipeName}: read timeout after {timeoutMs}ms");
                        return Fail("TIMEOUT", $"host call exceeded {timeoutMs}ms");
                    }
                    int n = readTask.Result;
                    if (n == 0) break;
                    // 一连接一问一答：首行即响应，缓冲区里 '\n' 之后的内容按协议不存在
                    int nl = Array.IndexOf(buffer, '\n', 0, n);
                    if (nl >= 0)
                    {
                        sb.Append(buffer, 0, nl);
                        break;
                    }
                    sb.Append(buffer, 0, n);
                }
                var line = sb.ToString().TrimEnd('\r');
                return ParseResponse(line);
            }
        }
        catch (TimeoutException)
        {
            Program.Diag("hostcall", $"{tool} on {pipeName}: connect timeout after {ConnectRetries + 1} tries");
            return Fail("CAD_NOT_ATTACHED", $"cannot connect to LoopHost pipe '{pipeName}'");
        }
        catch (IOException ex)
        {
            Program.Diag("hostcall", $"{tool} on {pipeName}: PIPE_IO {Truncate(ex.Message)}");
            return Fail("PIPE_IO", ex.Message);
        }
        catch (Exception ex)
        {
            Program.Diag("hostcall", $"{tool} on {pipeName}: {ex.GetType().Name} {Truncate(ex.Message)}");
            return Fail("PIPE_ERROR", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string Truncate(string? s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 120 ? s : s[..120] + "...");

    private static void ConnectWithRetry(NamedPipeClientStream pipe)
    {
        for (int i = 0; ; i++)
        {
            try { pipe.Connect(ConnectTimeoutMs); return; }
            catch (TimeoutException)
            {
                if (i >= ConnectRetries) throw;
                Thread.Sleep(ConnectRetryDelayMs);
            }
        }
    }

    public static List<HostInstance> Discover(int probeTimeoutMs = 1200)
    {
        var result = new List<HostInstance>();
        int? envPid = null;
        if (int.TryParse(Environment.GetEnvironmentVariable("LOOP_ACAD_PID"), out var p) && p > 0)
            envPid = p;

        foreach (var pid in ListCandidatePids())
        {
            if (envPid is { } want && pid != want) continue;
            var pipeName = $"{BasePipeName}.{pid}";
            var status = CallOn(pipeName, "host.status", null, probeTimeoutMs);
            if (status.Ok)
            {
                result.Add(new HostInstance { Pid = pid, PipeName = pipeName, Status = status });
                continue;
            }
            if (RetryBudgetFor(status) == 0) continue;

            var retried = status;
            for (int i = 0; i < BusyProbeRetries && !retried.Ok && RetryBudgetFor(retried) > 0; i++)
            {
                Thread.Sleep(BusyProbeDelayMs);
                retried = CallOn(pipeName, "host.status", null, probeTimeoutMs);
            }
            result.Add(new HostInstance
            {
                Pid = pid,
                PipeName = pipeName,
                Status = retried.Ok ? retried : status,
                Degraded = !retried.Ok
            });
        }
        return result;
    }

    public static HostInstance SelectBest(List<HostInstance> instances)
    {
        HostInstance? best = null;
        int bestScore = -1;
        foreach (var inst in instances)
        {
            var cad = inst.Status?.Data?["cad"] as JsonObject;
            bool attached = cad?["attached"]?.GetValue<bool>() ?? false;
            int epoch = inst.Status?.Data?["epoch"]?.GetValue<int>() ?? 0;
            int score = (attached ? 2 : 0) + (epoch > 0 ? 1 : 0);
            if (best == null || score > bestScore
                || (score == bestScore && inst.Pid > best.Pid))
            {
                best = inst;
                bestScore = score;
            }
        }
        return best!;
    }

    private static List<int> ListCandidatePids()
    {
        var pids = new List<int>();
        try
        {
            foreach (var path in Directory.GetFiles(@"\\.\pipe\"))
            {
                var name = path.StartsWith(@"\\.\pipe\", StringComparison.Ordinal)
                    ? path[@"\\.\pipe\".Length..] : path;
                if (!name.StartsWith(BasePipeName + ".", StringComparison.Ordinal)) continue;
                var pidPart = name[(BasePipeName.Length + 1)..];
                if (pidPart.Length == 0 || !pidPart.All(char.IsDigit)) continue;
                if (!int.TryParse(pidPart, out var pid) || pid <= 0) continue;
                pids.Add(pid);
            }
        }
        catch {  }

        var alive = new List<int>();
        foreach (var pid in pids.Distinct())
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                if (!proc.HasExited && string.Equals(proc.ProcessName, "acad", StringComparison.OrdinalIgnoreCase))
                    alive.Add(pid);
            }
            catch {  }
        }
        return alive;
    }

    public static bool LegacyPipePresent()
    {
        try
        {
            foreach (var path in Directory.GetFiles(@"\\.\pipe\"))
            {
                var name = path.StartsWith(@"\\.\pipe\", StringComparison.Ordinal)
                    ? path[@"\\.\pipe\".Length..] : path;
                if (string.Equals(name, BasePipeName, StringComparison.Ordinal)) return true;
            }
        }
        catch { }
        return false;
    }

    public sealed class AcadProcessInfo
    {
        public int Pid;
        public string Title = "";
        public bool Responding;
        public string? StartedUtc;
        public long WorkingSetMb;
        public bool ReadOnly;
        public bool HostOnline;
        public string? PipeName;
    }

    public static List<AcadProcessInfo> ListAcadProcesses()
    {
        var list = new List<AcadProcessInfo>();
        System.Diagnostics.Process[] procs;
        try { procs = System.Diagnostics.Process.GetProcessesByName("acad"); }
        catch { return list; }

        foreach (var p in procs)
        {
            try
            {
                if (p.HasExited) continue;
                string title = "";
                try { title = p.MainWindowTitle ?? ""; } catch { }
                string? started = null;
                try { started = p.StartTime.ToUniversalTime().ToString("o"); } catch { }
                long wsMb = 0;
                try { wsMb = p.WorkingSet64 / (1024 * 1024); } catch { }
                string lower = title.ToLowerInvariant();
                list.Add(new AcadProcessInfo
                {
                    Pid = p.Id,
                    Title = title,
                    Responding = p.Responding,
                    StartedUtc = started,
                    WorkingSetMb = wsMb,
                    ReadOnly = title.Contains("只读") || lower.Contains("read-only")
                });
            }
            catch { }
        }
        list.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        return list;
    }

    public static bool PipePresent(string pipeName)
    {
        try
        {
            foreach (var path in Directory.GetFiles(@"\\.\pipe\"))
            {
                var name = path.StartsWith(@"\\.\pipe\", StringComparison.Ordinal)
                    ? path[@"\\.\pipe\".Length..] : path;
                if (string.Equals(name, pipeName, StringComparison.Ordinal)) return true;
            }
        }
        catch { }
        return false;
    }

    public static bool AnyHostPipePresent()
    {
        try
        {
            foreach (var path in Directory.GetFiles(@"\\.\pipe\"))
            {
                var name = path.StartsWith(@"\\.\pipe\", StringComparison.Ordinal)
                    ? path[@"\\.\pipe\".Length..] : path;
                if (name.StartsWith(BasePipeName, StringComparison.Ordinal)) return true;
            }
        }
        catch { }
        return false;
    }

    private static string DescribeOffline()
    {
        var env = Environment.GetEnvironmentVariable("LOOP_ACAD_PID");
        var msg = env is null
            ? "no acad process serving " + BasePipeName + ".<pid>"
            : $"LOOP_ACAD_PID={env} has no live host pipe";
        if (LegacyPipePresent())
            msg += "; legacy LoopHost (pipe without PID) detected — restart acad to load the new host";
        return msg;
    }

    private static class TokenStore
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> Cache = new();

        public static string? Get(int pid)
        {
            if (pid <= 0) return null;
            if (Cache.TryGetValue(pid, out var cached)) return cached;

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HotReloadCAD", $"loop-{pid}.token");
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var t = File.ReadAllText(path).Trim();
                        if (t.Length > 0) { Cache[pid] = t; return t; }
                    }
                }
                catch { }
                Thread.Sleep(150);
            }
            return null;
        }

        public static bool Evict(int pid)
        {
            return pid > 0 && Cache.TryRemove(pid, out _);
        }
    }

    private static int PidFromPipeName(string pipeName)
    {
        var prefix = BasePipeName + ".";
        if (pipeName.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(pipeName[prefix.Length..], out var pid)) return pid;
        return 0;
    }

    private static CallResult ParseResponse(string line)
    {
        if (line.Length == 0) return Fail("EMPTY_RESPONSE", "host closed connection without response");
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (Exception ex) { return Fail("BAD_RESPONSE", "unparseable response: " + ex.Message); }
        if (node is not JsonObject obj) return Fail("BAD_RESPONSE", "response is not an object");

        var result = new CallResult { Ok = obj["ok"]?.GetValue<bool>() ?? false };
        result.ElapsedMs = obj["elapsed_ms"]?.GetValue<long>();
        if (result.Ok)
        {
            result.Data = obj["data"];
        }
        else if (obj["error"] is JsonObject err)
        {
            result.ErrorCode = err["code"]?.GetValue<string>() ?? "UNKNOWN";
            result.ErrorMessage = err["message"]?.GetValue<string>() ?? "";
        }
        else
        {
            result.ErrorCode = "BAD_RESPONSE";
            result.ErrorMessage = "missing error object";
        }
        return result;
    }

    private static CallResult Fail(string code, string message) =>
        new() { Ok = false, ErrorCode = code, ErrorMessage = message };
}

