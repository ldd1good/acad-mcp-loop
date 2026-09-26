using System.Collections.Concurrent;

namespace LoopEngine;

/// <summary>
/// R27（2026-09-15）引擎侧异步作业面：受理（宿主 act.run_command_async）+
/// 块状等待（宿主 act.job_status 免槽轮询，2s 间隔）。
/// 动机（2026-09-14 会话实测）：长命令（TEST_T12_FIXTURE 6~9min）撞 55s RPC 读超时，
/// 引擎拿到 TIMEOUT 误报而命令仍在跑，agent 只能 sleep+grep 磁盘日志盲等。
/// 合同：
///   · Submit 秒回 job_id（宿主受理即返）；宿主不支持（TOOL_NOT_FOUND）时由调用方回退旧同步路径；
///   · Wait 终态返回 DONE/FAIL；到 wait_ms 仍 RUNNING 返回 RUNNING（不是错误）——
///     "块状续等"与 run_iteration 同范式（wait_job 同 id 再调即续）；
///   · 轮询中宿主不可达视为瞬态（作业仍在宿主主线程跑），继续轮询到预算耗尽。
/// </summary>
public static class JobRunner
{
    public const int DefaultWaitMs = 50_000;   // 旧同步路径的体感上限：小于典型客户端超时
    public const int MaxWaitMs = 600_000;      // 10min：覆盖最长门禁 TEST_T12_FIXTURE 实测上界
    private const int PollIntervalMs = 2_000;  // 终态检测延迟 ≤2s（D3 开销上限的余量来源）

    private sealed class Reg
    {
        public string Command = "";
        public long SubmittedMs;
    }

    private static readonly ConcurrentDictionary<string, Reg> _regs = new();

    public sealed class SubmitOutcome
    {
        public bool Ok;
        public string? JobId;
        public string? ErrorCode;      // TOOL_NOT_FOUND = 旧宿主，调用方可回退同步路径
        public string? ErrorMessage;
    }

    public static SubmitOutcome Submit(string command)
    {
        var r = HostClient.Call("act.run_command_async",
            new JsonObject { ["command"] = command }, 15_000);
        if (!r.Ok)
            return new SubmitOutcome { Ok = false, ErrorCode = r.ErrorCode, ErrorMessage = r.ErrorMessage };
        var jobId = r.Data?["job_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(jobId))
            return new SubmitOutcome { Ok = false, ErrorCode = "BAD_RESPONSE", ErrorMessage = "host accepted but no job_id" };
        _regs[jobId] = new Reg { Command = command, SubmittedMs = Environment.TickCount64 };
        while (_regs.Count > 64)
        {
            foreach (var k in _regs.Keys)
            {
                _regs.TryRemove(k, out _);
                if (_regs.Count <= 64) break;
            }
        }
        return new SubmitOutcome { Ok = true, JobId = jobId };
    }

    public sealed class WaitOutcome
    {
        public string State = "RUNNING";   // RUNNING | DONE | FAIL | UNKNOWN
        public JsonNode? Result;           // DONE：业务返回（含业务自己的 ok:false 载荷）
        public string? ErrorText;          // FAIL / UNKNOWN 附注
        public long ElapsedMs;
        public int Polls;
        public string? LastTransportError;
    }

    /// <summary>轮询直到终态或预算耗尽。宿主不可达是瞬态：作业在宿主侧继续，不判死。</summary>
    public static WaitOutcome Wait(string jobId, long waitMs)
    {
        var outcome = new WaitOutcome();
        long deadline = Environment.TickCount64 + waitMs;
        while (true)
        {
            var st = HostClient.Call("act.job_status",
                new JsonObject { ["job_id"] = jobId }, 10_000);
            if (st.Ok && st.Data is JsonObject d)
            {
                var state = d["state"]?.GetValue<string>() ?? "UNKNOWN";
                outcome.ElapsedMs = d["elapsed_ms"]?.GetValue<long>() ?? 0;
                if (state == "DONE")
                {
                    outcome.State = "DONE";
                    outcome.Result = d["result"]?.DeepClone();
                    return outcome;
                }
                if (state == "FAIL")
                {
                    outcome.State = "FAIL";
                    outcome.ErrorText = d["error"]?.GetValue<string>();
                    return outcome;
                }
                outcome.LastTransportError = null;
            }
            else
            {
                // JOB_NOT_FOUND 说明宿主重启/淘汰——作业不可恢复，立即返回
                if (string.Equals(st.ErrorCode, "JOB_NOT_FOUND", StringComparison.Ordinal))
                {
                    outcome.State = "UNKNOWN";
                    outcome.ErrorText = "host lost the job (restart or evicted): " + (st.ErrorMessage ?? "");
                    return outcome;
                }
                outcome.LastTransportError = st.ErrorCode + ": " + (st.ErrorMessage ?? "");
            }
            outcome.Polls++;
            if (Environment.TickCount64 + PollIntervalMs > deadline) return outcome;
            Thread.Sleep(PollIntervalMs);
        }
    }

    /// <summary>run_command MCP 响应组装：终态回业务结果；超时回 RUNNING（可续等）。</summary>
    public static JsonObject SubmitAndWaitResponse(string command, int waitMs)
    {
        var submit = Submit(command);
        if (!submit.Ok)
            return new JsonObject
            {
                ["ok"] = false,
                ["async_capable"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = submit.ErrorCode ?? "SUBMIT_FAILED",
                    ["message"] = submit.ErrorMessage ?? ""
                }
            };
        return Finish(new JsonObject { ["ok"] = true, ["job_id"] = submit.JobId }, submit.JobId!, waitMs);
    }

    /// <summary>wait_job MCP 响应组装（块状续等：同 job_id 重复调用）。</summary>
    public static JsonObject WaitResponse(string jobId, int waitMs)
        => Finish(new JsonObject { ["ok"] = true, ["job_id"] = jobId }, jobId, waitMs);

    private static JsonObject Finish(JsonObject res, string jobId, int waitMs)
    {
        var w = Wait(jobId, waitMs);
        res["state"] = w.State;
        res["waited_ms"] = waitMs;
        switch (w.State)
        {
            case "DONE":
                res["data"] = w.Result?.DeepClone() ?? new JsonObject();
                res["ok"] = w.Result is JsonObject ro && ro["ok"] is JsonNode flag
                    ? flag.GetValue<bool>() : true;
                return res;
            case "FAIL":
                res["ok"] = false;
                res["error"] = new JsonObject
                {
                    ["code"] = "JOB_FAILED",
                    ["message"] = w.ErrorText ?? "command threw on main thread"
                };
                return res;
            case "UNKNOWN":
                res["ok"] = false;
                res["error"] = new JsonObject
                {
                    ["code"] = "JOB_LOST",
                    ["message"] = w.ErrorText ?? "job no longer known by host"
                };
                return res;
            default:
                // RUNNING：受理成功、仍在跑——不是错误。续等 = 同 job_id 再调 wait_job。
                res["ok"] = true;
                res["note"] = "命令受理后在宿主主线程执行中；wait_job(job_id, wait_ms) 续等（块状，每轮 ≤wait_ms）";
                if (w.LastTransportError != null)
                    res["transport_note"] = "轮询期间宿主暂不可达（瞬态）: " + w.LastTransportError;
                return res;
        }
    }
}
