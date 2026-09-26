using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;

namespace LoopHost
{

    public sealed class PipeServer
    {

        public const string BasePipeName = "HotReloadCAD.LoopHost.v1";
        public static string PipeName
        {
            get { return BasePipeName + "." + System.Diagnostics.Process.GetCurrentProcess().Id; }
        }
        private const int DefaultCallTimeoutMs = 55000;

        private const int BusyGraceMs = 15000;

        private const int ListenerRecreateMs = 5 * 60 * 1000;

        private const int WatchdogTickMs = 15 * 1000;
        private const int SessionStuckMs = 120 * 1000;
        private const int ForceClearGraceMs = 30 * 1000;
        private const int AcceptStuckMs = 60 * 1000;

        private static readonly PipeServer _instance = new PipeServer();
        public static PipeServer Instance { get { return _instance; } }

        private volatile bool _running;
        private Thread _acceptThread;
        private readonly object _startLock = new object();
        private int _serving;
        private long _nextConnId;
        private string _token = "";

        private volatile NamedPipeServerStream _sessionPipe;
        private long _sessionConnId = -1;
        private long _sessionProgressMs;
        private volatile bool _sessionPipeDisposed;
        private long _sessionGraceUntilMs;
        private volatile NamedPipeServerStream _acceptPhasePipe;
        private long _acceptHeartbeatMs;
        private int _acceptGeneration;
        private long _orphanSessions;

        private PipeServer() { }

        public bool IsRunning { get { return _running; } }

        public void Start()
        {
            lock (_startLock)
            {
                if (_running) return;
                _running = true;

                _token = Guid.NewGuid().ToString("N");
                try
                {
                    var dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "HotReloadCAD");
                    Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(dir, "loop-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".token"),
                        _token);
                }
                catch (Exception ex)
                {
                    RingLogSink.Log("pipe", "token file write failed: " + ex.Message);
                }
                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "LoopHost.PipeAccept"
                };
                _acceptThread.Start();

                System.Threading.Volatile.Write(ref _acceptHeartbeatMs, NowMs());
                var watchdog = new Thread(WatchdogLoop)
                {
                    IsBackground = true,
                    Name = "LoopHost.PipeWatchdog"
                };
                watchdog.Start();
            }
            RingLogSink.Log("pipe", "PipeServer started on " + PipeName);
        }

        private void AcceptLoop()
        {
            int myGen = System.Threading.Interlocked.CompareExchange(ref _acceptGeneration, 0, 0);
            while (_running)
            {
                System.Threading.Volatile.Write(ref _acceptHeartbeatMs, NowMs());
                NamedPipeServerStream pipe = null;
                bool handedOff = false;
                bool retired = false;
                try
                {
                    pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    _acceptPhasePipe = pipe;

                    var createdUtc = DateTime.UtcNow;
                    var waitTask = pipe.WaitForConnectionAsync();
                    bool recreateListener = false;
                    while (!waitTask.Wait(500))
                    {
                        System.Threading.Volatile.Write(ref _acceptHeartbeatMs, NowMs());
                        if (!_running) break;
                        if (System.Threading.Interlocked.CompareExchange(ref _acceptGeneration, 0, 0) != myGen)
                        {
                            retired = true;
                            break;
                        }
                        if ((DateTime.UtcNow - createdUtc).TotalMilliseconds >= ListenerRecreateMs)
                        {
                            recreateListener = true;
                            break;
                        }
                    }
                    if (!_running) break;
                    if (retired) break;
                    if (recreateListener)
                    {

                        RingLogSink.Log("pipe", "listener recreate (watchdog: no accept in "
                            + ListenerRecreateMs / 1000 + "s; heals wedged overlapped accept)");
                        continue;
                    }

                    // N4-1：门禁下移到 HandleClient（按首请求工具判定）——轮询类连接不再被
                    // ACT 长轮询挤掉（此前 HOST_BUSY 拒收导致引擎轮询断流、expect 断言漏检）
                    System.Threading.Volatile.Write(ref _sessionProgressMs, NowMs());

                    var accepted = pipe;
                    handedOff = true;
                    _acceptPhasePipe = null;
                    var serveThread = new Thread(() =>
                    {

                        try { HandleClient(accepted); }
                        catch (Exception ex) { RingLogSink.Log("pipe", "serve session crashed: " + ex.Message); }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref _serving, 0);
                            try { accepted.Dispose(); } catch { }
                        }
                    })
                    {
                        IsBackground = true,
                        Name = "LoopHost.PipeServe"
                    };
                    serveThread.Start();
                }
                catch (Exception ex)
                {
                    if (_running) RingLogSink.Log("pipe", "accept loop error: " + ex.Message);
                }
                finally
                {

                    if (!handedOff)
                    {
                        try { pipe.Dispose(); } catch { }
                        if (ReferenceEquals(_acceptPhasePipe, pipe)) _acceptPhasePipe = null;
                    }
                }
            }
        }

        private void HandleClient(NamedPipeServerStream pipe)
        {
            RingLogSink.Log("pipe", "client connected");
            long connId = System.Threading.Interlocked.Increment(ref _nextConnId);

            // N4-1：按首请求工具决定是否占用 ACT 互斥槽——轮询类（status/echo/tail_log）
            // 并发服务，act.* 仍全局互斥（同一时刻只允许一个 CAD 主线程长任务）
            bool holdsSlot = false;
            try
            {
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    // 首请求：决定槽位归属
                    string firstLine;
                    try { firstLine = reader.ReadLine(); }
                    catch { return; }
                    if (firstLine == null) return;
                    if (firstLine.Length > 0)
                    {
                        System.Threading.Volatile.Write(ref _sessionProgressMs, NowMs());
                        if (!IsPollTool(firstLine))
                        {
                            if (System.Threading.Interlocked.CompareExchange(ref _serving, 1, 0) == 1)
                            {
                                try
                                {
                                    writer.WriteLine(MiniJson.Write(new Dictionary<string, object>
                                    {
                                        { "id", 0 },
                                        { "ok", false },
                                        { "error", new Dictionary<string, object>
                                            {
                                                { "code", "HOST_BUSY_OTHER_CLIENT" },
                                                { "message", "another client session is being served" },
                                                { "holder_pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                                                { "since_utc", HostRuntime.StartedUtc.ToString("o") }
                                            }
                                        }
                                    }));
                                }
                                catch { }
                                return;
                            }
                            holdsSlot = true;
                            _sessionPipe = pipe;
                            System.Threading.Volatile.Write(ref _sessionConnId, connId);
                            _sessionPipeDisposed = false;
                            System.Threading.Volatile.Write(ref _sessionProgressMs, NowMs());
                        }
                        string firstResponse;
                        try { firstResponse = HandleRequest(firstLine, connId); }
                        catch (Exception ex)
                        {
                            firstResponse = MiniJson.Write(new System.Collections.Generic.Dictionary<string, object>
                            {
                                { "id", 0 },
                                { "ok", false },
                                { "error", new System.Collections.Generic.Dictionary<string, object>
                                    {
                                        { "code", "INTERNAL" },
                                        { "message", "dispatch failed: " + ex.GetType().Name + ": " + ex.Message }
                                    }
                                }
                            });
                        }
                        try { writer.WriteLine(firstResponse); }
                        catch { return; }
                    }

                    while (_running && pipe.IsConnected)
                    {
                        string line;
                        try { line = reader.ReadLine(); }
                        catch { break; }
                        if (line == null) break;
                        if (line.Length == 0) continue;
                        System.Threading.Volatile.Write(ref _sessionProgressMs, NowMs());

                        // 非槽位连接上若来了 act 类请求：按忙拒绝（与旧 HOST_BUSY 口径一致）
                        if (!holdsSlot && !IsPollTool(line))
                        {
                            try
                            {
                                writer.WriteLine(MiniJson.Write(new Dictionary<string, object>
                                {
                                    { "id", 0 },
                                    { "ok", false },
                                    { "error", new Dictionary<string, object>
                                        {
                                            { "code", "HOST_BUSY_OTHER_CLIENT" },
                                            { "message", "another client session is being served" },
                                            { "holder_pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                                            { "since_utc", HostRuntime.StartedUtc.ToString("o") }
                                        }
                                    }
                                }));
                            }
                            catch { }
                            continue;
                        }

                        string response;
                        try { response = HandleRequest(line, connId); }
                        catch (Exception ex)
                        {
                            response = MiniJson.Write(new System.Collections.Generic.Dictionary<string, object>
                            {
                                { "id", 0 },
                                { "ok", false },
                                { "error", new System.Collections.Generic.Dictionary<string, object>
                                    {
                                        { "code", "INTERNAL" },
                                        { "message", "dispatch failed: " + ex.GetType().Name + ": " + ex.Message }
                                    }
                                }
                            });
                        }

                        System.Threading.Volatile.Write(ref _sessionProgressMs, NowMs());
                        try { writer.WriteLine(response); }
                        catch { break; }
                    }
                }
            }
            catch (System.IO.IOException)
            {
                // P3-3：客户端读超时后主动断开（引擎 CallOnceCore 每调用一连接）表现为
                // 管道中断 IOException——这是常态不是 teardown 失败，不打噪声行
                // （该行还会进日志环污染 COLLECT 静默判据，见 P1-2）
            }
            catch (Exception ex)
            {

                RingLogSink.Log("pipe", "session teardown failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {

                try { MainThreadPump.Instance.CancelByConnection(connId); } catch { }

                if (holdsSlot)
                {
                    if (System.Threading.Interlocked.CompareExchange(ref _sessionConnId, -1, connId) == connId)
                        System.Threading.Interlocked.Exchange(ref _serving, 0);
                    _sessionPipe = null;
                    _sessionPipeDisposed = false;
                }
                RingLogSink.Log("pipe", "client disconnected");
            }
        }

        /// <summary>N4-1：免 ACT 互斥槽的轮询类工具（只读内存态，可并发）。
        /// host.abort 同样免槽：挂起救援通道，不能被占用槽位的挂死连接挡在门外。
        /// R27：act.job_status 免槽——异步作业状态查询是长命令期间最高频的调用，
        /// 占槽会把自己锁在门外（异步命令经 Post 执行，不占 _serving 槽）。</summary>
        private static bool IsPollTool(string requestLine)
        {
            try
            {
                var req = MiniJson.ParseObject(requestLine);
                var tool = req != null && req.ContainsKey("tool") ? req["tool"] as string : null;
                return tool == "host.status" || tool == "host.echo" || tool == "host.tail_log"
                    || tool == "host.abort" || tool == "act.job_status";
            }
            catch { return false; }
        }

        internal void Beat()
        {
            System.Threading.Volatile.Write(ref _sessionProgressMs, NowMs());
        }

        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        public long SessionIdleMs
        {
            get
            {
                if (System.Threading.Interlocked.CompareExchange(ref _serving, 0, 0) != 1) return 0;
                return NowMs() - System.Threading.Volatile.Read(ref _sessionProgressMs);
            }
        }

        public long AcceptIdleMs
        {
            get { return NowMs() - System.Threading.Volatile.Read(ref _acceptHeartbeatMs); }
        }

        public long OrphanSessions { get { return System.Threading.Interlocked.Read(ref _orphanSessions); } }

        private void WatchdogLoop()
        {
            while (_running)
            {
                Thread.Sleep(WatchdogTickMs);
                if (!_running) break;
                try { WatchdogTick(); }
                catch (Exception ex) { RingLogSink.Log("watchdog", "tick error: " + ex.Message); }
            }
        }

        private void WatchdogTick()
        {
            long now = NowMs();

            if (System.Threading.Interlocked.CompareExchange(ref _serving, 0, 0) == 1)
            {
                long idleMs = now - System.Threading.Volatile.Read(ref _sessionProgressMs);
                var pipe = _sessionPipe;
                if (idleMs > SessionStuckMs && pipe != null && !_sessionPipeDisposed)
                {
                    _sessionPipeDisposed = true;
                    RingLogSink.Log("watchdog", "session stuck " + idleMs + "ms (conn "
                        + System.Threading.Interlocked.Read(ref _sessionConnId)
                        + ") — dispose session pipe to abort stuck IO");
                    try { pipe.Dispose(); } catch { }
                    _sessionGraceUntilMs = now + ForceClearGraceMs;
                }
                else if (_sessionPipeDisposed && idleMs > SessionStuckMs && now >= _sessionGraceUntilMs)
                {

                    long connId = System.Threading.Interlocked.CompareExchange(ref _sessionConnId, 0, 0);
                    if (connId >= 0 &&
                        System.Threading.Interlocked.CompareExchange(ref _sessionConnId, -1, connId) == connId)
                    {
                        System.Threading.Interlocked.Exchange(ref _serving, 0);
                        System.Threading.Interlocked.Increment(ref _orphanSessions);
                        RingLogSink.Log("watchdog", "serve thread still wedged after dispose (non-IO block) — "
                            + "force-cleared serving slot, thread leaked (conn " + connId + ")");
                    }
                    _sessionPipeDisposed = false;
                }
            }

            long accIdleMs = now - System.Threading.Volatile.Read(ref _acceptHeartbeatMs);
            if (accIdleMs > AcceptStuckMs)
            {
                int gen = System.Threading.Interlocked.Increment(ref _acceptGeneration);
                var phasePipe = _acceptPhasePipe;
                _acceptPhasePipe = null;
                RingLogSink.Log("watchdog", "accept loop silent " + accIdleMs + "ms — dispose phase pipe + spawn replacement gen="
                    + gen + (phasePipe != null ? " (had pending/busy pipe)" : ""));
                try { if (phasePipe != null) phasePipe.Dispose(); } catch { }
                new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "LoopHost.PipeAccept.wd" + gen
                }.Start();
            }
        }

        private static string HandleRequest(string line, long connId)
        {
            var request = MiniJson.ParseObject(line);
            if (request == null)
                return Error(0, "BAD_REQUEST", "not a json object");

            var idValue = request.ContainsKey("id") ? request["id"] : 0;
            long id = Convert.ToInt64(idValue ?? 0L);
            var tool = request.ContainsKey("tool") ? request["tool"] as string : null;
            if (string.IsNullOrEmpty(tool))
                return Error(id, "BAD_REQUEST", "missing tool");

            var gotToken = request.ContainsKey("token") ? request["token"] as string : null;
            if (!string.Equals(gotToken, Instance._token, StringComparison.Ordinal))
                return Error(id, "AUTH_FAILED", "missing or wrong token (paired engine/host required)");

            var payload = request.ContainsKey("payload") && request["payload"] is System.Collections.Generic.Dictionary<string, object>
                ? (System.Collections.Generic.Dictionary<string, object>)request["payload"]
                : new System.Collections.Generic.Dictionary<string, object>();

            if (tool == "host.status") return Ok(id, HostStatus());
            if (tool == "host.echo") return Ok(id, payload);
            if (tool == "host.tail_log")
            {
                long since = GetLong(payload, "since_seq", 0);
                int limit = (int)GetLong(payload, "limit", 200);
                string runId = GetString(payload, "run_id", null);
                return Ok(id, RingLogSink.Instance.ReadSince(since, runId, limit));
            }
            // P1-1 安全中止：服务线程直接执行（挂起时等不到主线程泵），IsPollTool 免槽——
            // 挂住的 act 连接还占着 ACT 槽，走槽位判定会被 HOST_BUSY_OTHER_CLIENT 拒掉
            if (tool == "host.abort") return Ok(id, CadAbortTool.Handle());

            // R27 异步命令作业面（详见 CommandJobs 合同）：
            //   act.run_command_async —— 受理即返 {job_id,state:RUNNING}，业务经泵 Post 执行
            //   act.job_status        —— 免槽轮询（IsPollTool 已列），终态附业务结果
            if (tool == "act.job_status")
            {
                var jobId = GetString(payload, "job_id", null);
                var jobRec = CommandJobs.Get(jobId);
                if (jobRec == null)
                    return Error(id, "JOB_NOT_FOUND", "unknown or evicted job_id: " + jobId
                        + " (host keeps last 16; restart clears)");
                return Ok(id, CommandJobs.Snapshot(jobRec));
            }
            if (tool == "act.run_command_async")
            {
                var cmd = GetString(payload, "command", null);
                var guard = GuardCommand(cmd);
                if (guard != null) return Error(id, "TOOL_UNSAFE", guard);
                // P0-2 陈旧防护（2026-09-19）——必须先于 TryGet：冷启动 acad 后首次 act 调用
                // 正是自动装填业务的时机（放后面会被 business gen=0 直接 TOOL_NOT_FOUND）。
                // 动机：两次 run_command"验证"跑在旧 DLL 上（loaded=2400/磁盘=2402）。
                string staleNote;
                bool autoReloaded = ReloadOrchestrator.Instance.AutoReloadIfStale(out staleNote);
                RingLogSink.Log("stale-guard", "run_command_async(" + cmd + "): " + staleNote);
                Func<string, string> handler;
                if (!ToolRegistry.Instance.TryGet("act.run_command", out handler))
                    return Error(id, "TOOL_NOT_FOUND",
                        "act.run_command not registered (business gen=" + ToolRegistry.Instance.Epoch
                        + "; stale-guard said: " + staleNote + ")");
                var submitted = CommandJobs.Submit(cmd, handler, needsDocLock: true);
                if (submitted == null)
                {
                    var inflight = CommandJobs.Inflight();
                    return Error(id, "ACT_JOB_INFLIGHT",
                        "another async command is RUNNING: " + (inflight != null ? inflight.Command + " (" + inflight.Id + ")" : "?")
                        + " — poll act.job_status until terminal, then resubmit");
                }
                var snap = CommandJobs.Snapshot(submitted);
                // 受理响应带重载账目，agent 无需再调 attach_status 核对 loaded_version
                if (snap is System.Collections.Generic.Dictionary<string, object> snapDict)
                    snapDict["auto_reloaded"] = autoReloaded
                        ? (object)("true: " + staleNote)
                        : staleNote;
                return Ok(id, snap);
            }

            bool needsDocLock = tool.StartsWith("act.", StringComparison.Ordinal);

            // R27 串行兜底：异步作业 RUNNING 期间，同步 act.*（含旧 act.run_command）一律拒收。
            // 异步命令经 Post 执行不占 _serving 槽——没有这道闸，旧同步路径会在作业跑着时
            // 排队等 55s 然后超时，回到改造前的盲等。
            if (needsDocLock && CommandJobs.Inflight() != null)
            {
                var inflightJob = CommandJobs.Inflight();
                return Error(id, "ACT_JOB_INFLIGHT",
                    "async job " + (inflightJob != null ? inflightJob.Id + " (" + inflightJob.Command + ")" : "")
                    + " is RUNNING on the main thread — use act.job_status");
            }

            MainThreadResult result = null;
            if (tool == "host.reload")
            {
                string dllPath = GetString(payload, "dll_path", null);
                string expected = GetString(payload, "expected_version", null);
                string expectedGuid = GetString(payload, "expected_guid", null);
                result = DoReload(dllPath, expected, expectedGuid, connId);
            }
            else if (CadStateTools.TryGet(tool, out var hostHandler))
            {

                string payloadJson = MiniJson.Write(payload);
                result = MainThreadPump.Instance.Execute(
                    tool,
                    delegate { return hostHandler(payloadJson); },
                    DefaultCallTimeoutMs, BusyGraceMs, connId, needsDocLock, Instance.Beat);
            }
            else
            {

                // P0-2 陈旧防护前置（同 act.run_command_async 分支的理由：先于 TryGet，
                // 冷启动后首次同步 act 调用即自动装填业务，旧引擎走这条路径）。
                if (tool == "act.run_command")
                {
                    string snSync;
                    ReloadOrchestrator.Instance.AutoReloadIfStale(out snSync);
                    RingLogSink.Log("stale-guard", "run_command(sync)(" + GetString(payload, "command", "?") + "): " + snSync);
                }
                Func<string, string> handler;
                if (!ToolRegistry.Instance.TryGet(tool, out handler))
                {
                    return Error(id, "TOOL_NOT_FOUND",
                        "unknown tool: " + tool + " (business gen=" + ToolRegistry.Instance.Epoch + ")");
                }

                if (tool == "act.run_command")
                {
                    var cmd = GetString(payload, "command", null);
                    var guard = GuardCommand(cmd);
                    if (guard != null) return Error(id, "TOOL_UNSAFE", guard);
                }

                string payloadJson = MiniJson.Write(payload);
                result = MainThreadPump.Instance.Execute(
                    tool,
                    delegate { return handler(payloadJson); },
                    DefaultCallTimeoutMs, BusyGraceMs, connId, needsDocLock, Instance.Beat);
            }

            if (result == null) return Error(id, "INTERNAL", "no result");

            if (result.Ok)
            {
                return MiniJson.Write(new System.Collections.Generic.Dictionary<string, object>
                {
                    { "id", id },
                    { "ok", true },
                    { "data", result.Value ?? new object() },
                    { "elapsed_ms", result.ElapsedMs }
                });
            }

            return MiniJson.Write(new System.Collections.Generic.Dictionary<string, object>
            {
                { "id", id },
                { "ok", false },
                { "error", new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "code", result.ErrorCode ?? "INTERNAL" },
                        { "message", result.ErrorMessage ?? "" }
                    }
                },
                { "elapsed_ms", result.ElapsedMs }
            });
        }

        private static MainThreadResult DoReload(string dllPath, string expected, string expectedGuid, long connId)
        {
            var prep = ReloadOrchestrator.Instance.PrepareBytes(dllPath, expected, expectedGuid);
            if (prep == null)
                return MainThreadResult.Failure("RELOAD_FAILED", "prepare returned no result", 0);
            if (!prep.Ok)
                return MainThreadResult.Failure(prep.ErrorCode, prep.ErrorMessage, 0);

            var result = MainThreadPump.Instance.Execute(
                "host.reload.register",
                delegate { return ReloadOrchestrator.Instance.RegisterReloaded(dllPath, expected, expectedGuid); },
                DefaultCallTimeoutMs, BusyGraceMs, connId, false, Instance.Beat);

            if (!result.Ok && (result.ErrorCode == "CAD_BUSY" || result.ErrorCode == "JOB_SUPERSEDED"))
                ReloadOrchestrator.AbandonReload();
            return result;
        }

        internal static string GuardCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "empty command";

            return System.Text.RegularExpressions.Regex.IsMatch(command,
                    @"^(AGENT_[A-Za-z0-9_\-]+( .+)?|TEST_[A-Za-z0-9_\-]*|AUTO_ANNOTATE)$")
                ? null
                : $"command '{command}' not in [AgentSafe] whitelist (AGENT_*/TEST_*); rejected by host-side guard";
        }

        private static Dictionary<string, object> HostStatus()
        {

            var tools = CadStateTools.ToolNames.Select(n => "native:" + n).ToList();
            tools.AddRange(ToolRegistry.Instance.Names());
            tools.Sort(StringComparer.Ordinal);
            return new Dictionary<string, object>
            {
                { "host", "LoopHost" },
                { "version", typeof(PipeServer).Assembly.GetName().Version.ToString() },
                { "protocol_version", 2 },
                { "acad_pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                { "pipe_name", PipeName },
                { "host_started_utc", HostRuntime.StartedUtc.ToString("o") },
                { "acad_version", AcadVersion() },
                { "epoch", ToolRegistry.Instance.Epoch },
                { "state", ReloadOrchestrator.Instance.StateString },
                { "loaded_version", ReloadOrchestrator.Instance.LoadedBusinessVersion },
                { "build_guid", ReloadOrchestrator.Instance.LoadedBusinessGuid },
                { "pump_started", MainThreadPump.Instance.IsStarted },
                { "pending_jobs", MainThreadPump.Instance.PendingCount },
                { "stalled_ms", MainThreadPump.Instance.StalledMs },
                { "orphan_jobs_total", MainThreadPump.Instance.OrphanJobsTotal },
                // P1-3：排队年龄——取消残留/卡死队列超过阈值时给出确定性"该重启了"信号
                { "oldest_pending_ms", MainThreadPump.Instance.OldestPendingAgeMs },
                { "pending_stale", MainThreadPump.Instance.OldestPendingAgeMs > 120_000 },
                { "serving", Instance._serving },
                { "session_idle_ms", Instance.SessionIdleMs },
                { "accept_idle_ms", Instance.AcceptIdleMs },
                { "orphan_sessions", Instance.OrphanSessions },
                { "live_slots", GenerationLedger.SnapshotAll() },
                // R27：异步命令作业（RUNNING 时非 null；attach_status 据此可见 in-flight 命令）
                { "act_job", CommandJobs.InflightSnapshot() },
                { "log_seq", RingLogSink.Instance.CurrentSeq },
                { "tools", tools },
                { "cad", CadSnapshot() }
            };
        }

        private static Dictionary<string, object> CadSnapshot()
        {
            var cad = new Dictionary<string, object>
            {
                { "attached", false },
                { "doc", null },
                { "quiescent", null }
            };
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    cad["attached"] = true;
                    cad["doc"] = doc.Name;
                    try { cad["quiescent"] = doc.Editor.IsQuiescent; }
                    catch { cad["quiescent"] = "probe_failed"; }
                }
            }
            catch {  }
            // P1-1：队列非空且主线程 >120s 无 Idle → 疑似 regen/命令挂起。引擎据此置
            // cad_state=hung / run_iteration 判 CAD_HUNG 快速失败，并提供 abort_cad 处置
            var pump = MainThreadPump.Instance;
            cad["hung"] = pump.PendingCount > 0 && pump.StalledMs > 120_000;
            return cad;
        }

        private static object AcadVersion()
        {
            try
            {
                var ver = Autodesk.AutoCAD.ApplicationServices.Application.Version;
                if (ver == null) return null;
                var main = ver.Major + "." + ver.Minor;
                var yearMap = new Dictionary<string, string>
                {
                    { "23.0", "2019" }, { "23.1", "2020" },
                    { "24.0", "2021" }, { "24.1", "2022" }, { "24.2", "2023" }, { "24.3", "2024" },
                    { "25.0", "2025" }
                };
                string year;
                return new Dictionary<string, object>
                {
                    { "ver", main },
                    { "year", yearMap.TryGetValue(main, out year) ? year : null }
                };
            }
            catch { return null; }
        }

        private static string GetString(Dictionary<string, object> payload, string key, string fallback)
        {
            object v;
            if (payload != null && payload.TryGetValue(key, out v) && v is string) return (string)v;
            return fallback;
        }

        private static long GetLong(Dictionary<string, object> payload, string key, long fallback)
        {
            object v;
            if (payload != null && payload.TryGetValue(key, out v) && v is long) return (long)v;
            return fallback;
        }

        private static string Ok(long id, object data)
        {
            return MiniJson.Write(new Dictionary<string, object>
            {
                { "id", id }, { "ok", true }, { "data", data ?? new object() }
            });
        }

        private static string Error(long id, string code, string message)
        {
            return MiniJson.Write(new Dictionary<string, object>
            {
                { "id", id },
                { "ok", false },
                { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } }
            });
        }
    }
}

