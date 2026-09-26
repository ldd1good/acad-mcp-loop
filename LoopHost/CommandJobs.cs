using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace LoopHost
{
    /// <summary>
    /// R27（2026-09-15）异步命令作业表：act.run_command_async 受理后经 MainThreadPump.Post
    /// 派发，闭包把业务结果/异常写回 Job 记录，act.job_status（免 ACT 槽轮询类）随时可查。
    /// 动机：旧同步路径 act RPC 55s 读超时 ≠ 命令失败——命令仍在主线程跑，引擎却拿到
    /// TIMEOUT 误报，agent 只能 sleep+grep 盘日志盲等（2026-09-14 会话实测 3 轮 ~20min）。
    /// 语义合同：
    ///   · 同一时刻至多一个 RUNNING 作业（Submit 时有 RUNNING 在跑 → ACT_JOB_INFLIGHT 拒收，
    ///     与旧"一连接占槽"的串行口径等价；主线程泵本身也会串行执行）。
    ///   · 提交连接断开不取消作业（Post 的 ConnId=-1，CancelByConnection 不触及）。
    ///   · 记录内存保存最近 16 条（FIFO 淘汰），进程重启即清——job_id 前缀带 pid 便于排查。
    /// </summary>
    public static class CommandJobs
    {
        public sealed class Job
        {
            public string Id;
            public string Command;
            // RUNNING → DONE（业务返回，含业务自己的 ok:false 载荷）/ FAIL（闭包异常）
            public volatile string State;
            public long SubmittedMs;
            public long StartedMs;
            public long FinishedMs;
            public string ResultJson;   // DONE：业务返回 JSON 串
            public string ErrorText;    // FAIL：异常描述
        }

        private const int MaxKept = 16;

        private static readonly ConcurrentDictionary<string, Job> _jobs =
            new ConcurrentDictionary<string, Job>(StringComparer.Ordinal);
        private static readonly object _seqLock = new object();
        private static int _seq;
        private static readonly Queue<string> _fifo = new Queue<string>();

        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        public static Job Inflight()
        {
            foreach (var kv in _jobs)
                if (kv.Value.State == "RUNNING") return kv.Value;
            return null;
        }

        /// <summary>受理并派发：返回 null = 有 RUNNING 作业占线（调用方回 ACT_JOB_INFLIGHT）。</summary>
        public static Job Submit(string command, Func<string, string> handler, bool needsDocLock)
        {
            lock (_seqLock)
            {
                if (Inflight() != null) return null;
                var job = new Job
                {
                    Id = "job-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" +
                         Interlocked.Increment(ref _seq),
                    Command = command,
                    State = "RUNNING",
                    SubmittedMs = NowMs()
                };
                _jobs[job.Id] = job;
                _fifo.Enqueue(job.Id);
                while (_fifo.Count > MaxKept)
                {
                    var evict = _fifo.Dequeue();
                    Job removed;
                    _jobs.TryRemove(evict, out removed);
                    // 兜底：被淘汰的不可能是 RUNNING（Inflight 拒收保证），无需处理
                }
                MainThreadPump.Instance.Post(
                    "act.job." + job.Id,
                    delegate
                    {
                        job.StartedMs = NowMs();
                        try
                        {
                            var result = handler(MiniJson.Write(new Dictionary<string, object>
                            {
                                { "command", command }
                            }));
                            job.ResultJson = result ?? "";
                            job.State = "DONE";
                        }
                        catch (Exception ex)
                        {
                            job.ErrorText = ex.GetType().Name + ": " + ex.Message;
                            job.State = "FAIL";
                        }
                        finally
                        {
                            job.FinishedMs = NowMs();
                        }
                    },
                    needsDocLock);
                return job;
            }
        }

        public static Job Get(string id)
        {
            Job j;
            return (!string.IsNullOrEmpty(id) && _jobs.TryGetValue(id, out j)) ? j : null;
        }

        /// <summary>host.status 快照：RUNNING 作业可见（D4：in-flight 可观测）。</summary>
        public static object InflightSnapshot()
        {
            Job j = Inflight();
            if (j == null) return null;
            return new Dictionary<string, object>
            {
                { "job_id", j.Id },
                { "command", j.Command },
                { "state", j.State },
                { "elapsed_ms", NowMs() - j.SubmittedMs }
            };
        }

        /// <summary>act.job_status 应答体：终态附结果/错误与时长。</summary>
        public static Dictionary<string, object> Snapshot(Job j)
        {
            var d = new Dictionary<string, object>
            {
                { "job_id", j.Id },
                { "command", j.Command },
                { "state", j.State },
                { "queued_ms", Math.Max(0, j.StartedMs - j.SubmittedMs) },
                { "elapsed_ms", (j.State == "RUNNING" ? NowMs() : j.FinishedMs) - j.SubmittedMs }
            };
            if (j.State == "DONE")
            {
                d["result"] = ParseLenient(j.ResultJson);
                d["result_raw"] = j.ResultJson;
            }
            else if (j.State == "FAIL")
            {
                d["error"] = j.ErrorText;
            }
            return d;
        }

        private static object ParseLenient(string json)
        {
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, object>();
            try { return MiniJson.ParseObject(json) ?? (object)json; }
            catch { return json; }
        }
    }
}
