using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;

namespace LoopHost
{

    public sealed class MainThreadResult
    {
        public bool Ok;
        public object Value;
        public string ErrorCode;
        public string ErrorMessage;
        public long ElapsedMs;

        public static MainThreadResult Success(object value, long ms)
        {
            return new MainThreadResult { Ok = true, Value = value, ElapsedMs = ms };
        }

        public static MainThreadResult Failure(string code, string message, long ms)
        {
            return new MainThreadResult { Ok = false, ErrorCode = code, ErrorMessage = message, ElapsedMs = ms };
        }
    }

    public sealed class MainThreadPump
    {
        private static readonly MainThreadPump _instance = new MainThreadPump();
        public static MainThreadPump Instance { get { return _instance; } }

        private const int MaxJobsPerTick = 8;

        private static readonly object SupersededSentinel = new object();

        private static readonly object BusySentinel = new object();

        private readonly ConcurrentQueue<PumpJob> _queue = new ConcurrentQueue<PumpJob>();
        private readonly ConcurrentDictionary<string, PumpJob> _pendingByName
            = new ConcurrentDictionary<string, PumpJob>(StringComparer.Ordinal);
        private readonly object _supersedeLock = new object();
        private volatile bool _started;
        private int _mainThreadId = -1;
        private long _lastIdleUtcTicks;
        private long _orphanJobsTotal;

        private sealed class PumpJob
        {
            public string Name;
            public Func<object> Work;
            public TaskCompletionSource<object> Completion;
            public Stopwatch Stopwatch;
            public volatile bool Cancelled;
            public volatile bool Started;
            public long ConnId = -1;
            public bool NeedsDocLock;
            public long EnqueuedAtMs;
        }

        private MainThreadPump() { }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _lastIdleUtcTicks = DateTime.UtcNow.Ticks;
            Application.Idle += OnIdle;
            RingLogSink.Log("pump", "MainThreadPump started, mainThreadId=" + _mainThreadId);
        }

        public bool IsStarted { get { return _started; } }

        // net472 无 Environment.TickCount64：与 PipeServer.NowMs 同口径（UTC ms）
        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        // P1-3：准确计数——旧 PendingCount=_queue.Count 把已取消（仅置标记、未出队）的
        // job 也算上，主线程卡死时 OnIdle 无法惰性出队，pending_jobs 虚高误导判读。
        // 队列只装未 Started 的 job（完成即出队），枚举过滤 Cancelled 即准确且廉价。
        public int PendingCount
        {
            get
            {
                int n = 0;
                foreach (var job in _queue)
                    if (!job.Cancelled) n++;
                return n;
            }
        }

        /// <summary>最老未取消排队 job 的年龄（ms）；队列空返回 0。供 pending_stale 判定。</summary>
        public long OldestPendingAgeMs
        {
            get
            {
                long minEnqueued = long.MaxValue;
                foreach (var job in _queue)
                {
                    if (job.Cancelled) continue;
                    if (job.EnqueuedAtMs < minEnqueued) minEnqueued = job.EnqueuedAtMs;
                }
                return minEnqueued == long.MaxValue ? 0 : NowMs() - minEnqueued;
            }
        }

        public long StalledMs
        {
            get
            {
                if (_queue.Count == 0) return 0;
                return (long)((DateTime.UtcNow.Ticks - _lastIdleUtcTicks) / TimeSpan.TicksPerMillisecond);
            }
        }

        private void OnIdle(object sender, EventArgs e)
        {
            _lastIdleUtcTicks = DateTime.UtcNow.Ticks;

            int budget = MaxJobsPerTick;
            while (budget-- > 0)
            {
                string gateReason;
                if (!IsSafeToWork(out gateReason))
                {

                    return;
                }

                PumpJob job;
                if (!_queue.TryDequeue(out job)) return;

                lock (_supersedeLock)
                {

                    if (job.Cancelled)
                    {

                        PumpJob registered;
                        if (_pendingByName.TryGetValue(job.Name, out registered) && ReferenceEquals(registered, job))
                            _pendingByName.TryRemove(job.Name, out _);
                        continue;
                    }
                    PumpJob reg;
                    if (_pendingByName.TryGetValue(job.Name, out reg) && ReferenceEquals(reg, job))
                        _pendingByName.TryRemove(job.Name, out _);
                    job.Started = true;
                }
                RunJob(job);
            }
        }

        private static void RunJob(PumpJob job)
        {
            try
            {

                string gateReason;
                if (!IsSafeToWork(out gateReason))
                {
                    job.Completion.TrySetResult(BusySentinel);
                    return;
                }

                object value;
                if (job.NeedsDocLock)
                {

                    Document doc = null;
                    try { doc = Application.DocumentManager.MdiActiveDocument; }
                    catch { }
                    if (doc == null)
                        throw new InvalidOperationException("no active document for locked job");
                    using (doc.LockDocument())
                    {
                        value = job.Work();
                    }
                }
                else
                {
                    value = job.Work();
                }
                job.Completion.TrySetResult(value ?? new object());
            }
            catch (Exception ex)
            {
                job.Completion.TrySetException(ex);
            }
        }

        public static bool IsSafeToWork(out string reason)
        {
            reason = null;
            try
            {
                Document doc = null;
                try { doc = Application.DocumentManager.MdiActiveDocument; }
                catch (Exception ex) { reason = "docmanager_error:" + ex.GetType().Name; return false; }
                if (doc == null)
                {
                    // Start 页（无活动文档）：acad 必然空闲，放行。裸启动流程（attach_cad
                    // 不带图纸命令行）依赖此状态执行 use_document open；需要文档的任务
                    // （needsDocLock=true）在执行路径自行抛 "no active document"（见 locked 分支）
                    return true;
                }

                try
                {
                    if (!doc.Editor.IsQuiescent) { reason = "not_quiescent"; return false; }
                }
                catch (Exception ex)
                {
                    reason = "quiescent_probe_failed:" + ex.GetType().Name;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = "gate_error:" + ex.GetType().Name;
                return false;
            }
        }

        public MainThreadResult Execute(string name, Func<object> work, int timeoutMs, int busyGraceMs = 0,
            long connId = -1, bool needsDocLock = false, Action heartbeat = null)
        {
            var sw = Stopwatch.StartNew();

            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                try
                {
                    var value = work();
                    return MainThreadResult.Success(value, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    return MainThreadResult.Failure(DescribeError(ex), ex.GetType().Name + ": " + ex.Message, sw.ElapsedMilliseconds);
                }
            }

            var job = new PumpJob
            {
                Name = name,
                Work = work,
                Stopwatch = sw,
                Completion = new TaskCompletionSource<object>(),
                ConnId = connId,
                NeedsDocLock = needsDocLock,
                EnqueuedAtMs = NowMs()
            };

            lock (_supersedeLock)
            {
                PumpJob existing;
                if (_pendingByName.TryGetValue(name, out existing) && !existing.Started)
                {
                    existing.Cancelled = true;
                    existing.Completion.TrySetResult(SupersededSentinel);
                }
                _pendingByName[name] = job;
            }
            _queue.Enqueue(job);

            try
            {
                bool completed = false;
                while (true)
                {
                    int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (remaining <= 0) break;
                    int slice = Math.Min(250, remaining);
                    if (job.Completion.Task.Wait(slice)) { completed = true; break; }

                    if (heartbeat != null) heartbeat();

                    if (job.Cancelled)
                        return MainThreadResult.Failure("JOB_SUPERSEDED",
                            "superseded by newer '" + name + "' request", sw.ElapsedMilliseconds);

                    if (busyGraceMs > 0 && !job.Started && sw.ElapsedMilliseconds >= busyGraceMs)
                    {
                        string reason;
                        if (!IsSafeToWork(out reason))
                        {

                            if (TryCancelJob(job))
                                return MainThreadResult.Failure("CAD_BUSY",
                                    reason + " (busyGrace " + busyGraceMs + "ms exceeded, job cancelled)",
                                    sw.ElapsedMilliseconds);
                        }
                    }
                }

                if (!completed)
                {

                    if (!TryCancelJob(job))
                        return MainThreadResult.Failure("MAIN_THREAD_TIMEOUT",
                            "job already running when caller timed out (result unclaimed, pending="
                            + _queue.Count + ")", sw.ElapsedMilliseconds);
                    string reason;
                    if (!IsSafeToWork(out reason))
                        return MainThreadResult.Failure("CAD_BUSY",
                            reason + " (timeout " + timeoutMs + "ms, job cancelled)", sw.ElapsedMilliseconds);
                    return MainThreadResult.Failure("MAIN_THREAD_STALLED",
                        "stalled_ms=" + StalledMs + ", pending=" + _queue.Count
                        + " (timeout " + timeoutMs + "ms, job cancelled)", sw.ElapsedMilliseconds);
                }

                var value = job.Completion.Task.Result;
                if (ReferenceEquals(value, SupersededSentinel) || job.Cancelled)
                    return MainThreadResult.Failure("JOB_SUPERSEDED",
                        "job cancelled while queued (caller timeout or superseded)", sw.ElapsedMilliseconds);
                if (ReferenceEquals(value, BusySentinel))
                    return MainThreadResult.Failure("CAD_BUSY",
                        "gate closed again at execution time (double-check)", sw.ElapsedMilliseconds);
                return MainThreadResult.Success(value, sw.ElapsedMilliseconds);
            }
            catch (AggregateException agg)
            {
                var ex = agg.InnerException ?? agg;

                return MainThreadResult.Failure(DescribeError(ex), ex.GetType().Name + ": " + ex.Message, sw.ElapsedMilliseconds);
            }
            finally
            {
                lock (_supersedeLock)
                {
                    PumpJob registered;
                    if (_pendingByName.TryGetValue(name, out registered) && ReferenceEquals(registered, job))
                        _pendingByName.TryRemove(name, out _);
                }
            }
        }

        public static string DescribeError(Exception ex)
        {
            var arx = ex as Autodesk.AutoCAD.Runtime.Exception;
            if (arx != null)
            {
                try
                {
                    var status = arx.ErrorStatus.ToString();
                    if (!string.IsNullOrEmpty(status) && status != "OK") return "CAD_ERR_" + status;
                }
                catch { }
            }
            return "INTERNAL";
        }

        private bool TryCancelJob(PumpJob job)
        {
            lock (_supersedeLock)
            {
                if (job.Started) return false;
                job.Cancelled = true;
                job.Completion.TrySetResult(SupersededSentinel);
                return true;
            }
        }

        public void CancelByConnection(long connId)
        {
            if (connId < 0) return;
            foreach (var kv in _pendingByName)
            {
                var job = kv.Value;
                if (job.ConnId != connId) continue;
                if (TryCancelJob(job))
                {
                    // P1-3：取消即同步清注册表——旧版只置标记等 OnIdle 惰性出队，主线程
                    // 卡死时 _queue 永不消化，残留的 cancelled 条目让 pending_jobs 虚高、
                    // 同名新任务被误 supersede（实测 3 个 cancelled reload 残留案例）
                    _pendingByName.TryRemove(kv.Key, out _);
                }
                else
                    System.Threading.Interlocked.Increment(ref _orphanJobsTotal);
            }
        }

        /// <summary>
        /// fire-and-forget 入队（不等待完成）：host.abort 的命令行 \x03 排队兜底等场景用。
        /// 主线程恢复后执行；结果无人认领（TCS 持值至 GC）。
        /// </summary>
        public void Post(string name, Action work, bool needsDocLock = false)
        {
            var job = new PumpJob
            {
                Name = name,
                Work = () => { work(); return null; },
                Stopwatch = Stopwatch.StartNew(),
                Completion = new TaskCompletionSource<object>(),
                ConnId = -1,
                NeedsDocLock = needsDocLock,
                EnqueuedAtMs = NowMs()
            };
            lock (_supersedeLock)
            {
                PumpJob existing;
                if (_pendingByName.TryGetValue(name, out existing) && !existing.Started)
                {
                    existing.Cancelled = true;
                    existing.Completion.TrySetResult(SupersededSentinel);
                }
                _pendingByName[name] = job;
            }
            _queue.Enqueue(job);
        }

        public long OrphanJobsTotal { get { return System.Threading.Interlocked.Read(ref _orphanJobsTotal); } }
    }
}

