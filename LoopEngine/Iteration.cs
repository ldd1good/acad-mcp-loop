namespace LoopEngine;

public static class Iteration
{

    internal static readonly SemaphoreSlim RunLock = new(1, 1);

    internal const long StaleHolderMs = 10 * 60 * 1000;

    internal static void MarkStaleHolder(JsonObject? holder)
    {
        if (holder == null) return;
        if (holder["elapsed_ms"] is { } e && e.GetValue<long>() > StaleHolderMs)
        {
            holder["stale"] = true;
            holder["stale_note"] = $"holder exceeded {StaleHolderMs / 60000}min — iteration thread likely "
                + "wedged (RunLock is not force-releasable); restart the MCP session to spawn a fresh "
                + "engine and clear it";
            Program.Diag("iteration", "stale holder detected (wedged task thread?) — BUSY responses now advise restart");
        }
    }

    // 宿主环自身的结构化噪声行（RingLogSink.Log 打的 {"ts","host","msg"}：pipe 连接心跳 /
    // pump / reload 状态行）。业务 Serilog 行（Timestamp/Level/Message…）不含 "host" 字段，
    // 以此区分（get_iteration 日志过滤与 COLLECT 静默判据共用）。
    internal static bool IsHostNoiseLine(string line)
        => line.IndexOf("\"host\":\"", StringComparison.Ordinal) >= 0;

    // P2-1：观察者续等——不碰 RunLock，轮询 status.json 直到 DONE 幂等回放，或 waitMs 用尽
    // 返回 null（调用方回 RUNNING 提示继续续等）。
    private static JsonObject? WaitAndReplayTerminal(string runId, int waitMs)
    {
        long deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(2_000);
            try
            {
                var stage = Ledger.TryLoadStage(runId);
                if (stage == null) continue;
                if (stage["stage"]?.GetValue<string>() == "DONE"
                    && Ledger.TryFindIteration(runId, out var existing)
                    && existing["source"]?.GetValue<string>() == "status.json")
                {
                    existing["note"] = "块状续等命中终态（run_id 幂等回放）";
                    return existing;
                }
            }
            catch { }
        }
        return null;
    }

    // P2-2：BUSY 响应的粗估释放时间——按阶段预算上限减已运行时长，是估算不是承诺。
    internal static void AttachEta(JsonObject holder)
    {
        string? phase = holder["phase"]?.GetValue<string>();
        long budgetMs = phase switch
        {
            "BUILD" => 600_000,
            "ARRANGE" => 180_000,
            "ACT" => ActCompletePollMaxMs(),
            "COLLECT" => ActCompletePollMaxMs(),
            _ => 60_000,
        };
        long elapsed = holder["elapsed_ms"]?.GetValue<long>() ?? 0;
        long etaMs = Math.Max(0, budgetMs - elapsed);
        holder["expected_release_at"] = DateTime.UtcNow.AddMilliseconds(etaMs).ToString("o");
        holder["eta_note"] = $"估算（{phase} 预算 {budgetMs / 1000}s - 已运行），非承诺";
    }

    private static readonly object ActiveRunLock = new();
    private static JsonObject? _activeRun;

    private static bool _autoReloadedThisRun;

    private static bool _shadowMode;

    public static JsonObject? ActiveRunSnapshot()
    {
        lock (ActiveRunLock)
        {
            if (_activeRun == null) return null;
            var clone = (JsonObject)_activeRun.DeepClone();
            clone["elapsed_ms"] = (long)(DateTime.UtcNow - DateTime.Parse(clone["started_utc"]!.GetValue<string>(),
                null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()).TotalMilliseconds;
            return clone;
        }
    }

    private static void TouchActiveRun(string runId, string topic, string stage)
    {
        lock (ActiveRunLock)
        {
            if (_activeRun == null || _activeRun["run_id"]?.GetValue<string>() != runId)
            {
                _activeRun = new JsonObject
                {
                    ["run_id"] = runId,
                    ["topic"] = topic,
                    ["started_utc"] = DateTime.UtcNow.ToString("o")
                };
            }
            _activeRun["phase"] = stage;
        }
    }

    private static void ClearActiveRun(string runId)
    {
        lock (ActiveRunLock)
        {
            if (_activeRun?["run_id"]?.GetValue<string>() == runId) _activeRun = null;
        }
    }

    public static JsonObject Run(JsonObject args)
    {

        if (!RunLock.Wait(0))
        {
            var holder = ActiveRunSnapshot();
            MarkStaleHolder(holder);
            // P2-1 块状轮询：对同一 run_id 的重复调用改为观察者续等（每 2s 查 status.json，
            // 终态即幂等回放判决书），把"RUNNING 后靠外部脚本轮询业务日志"收进工具面。
            string requestedRunId = Ledger.SanitizeRunId(OptString(args, "run_id"));
            if (holder != null && requestedRunId.Length > 0
                && holder["run_id"]?.GetValue<string>() == requestedRunId)
            {
                int waitMs0 = Math.Clamp(OptInt(args, "wait_ms") ?? 100_000, 1_000, Repo.MaxWaitMs);
                var resumed = WaitAndReplayTerminal(requestedRunId, waitMs0);
                if (resumed != null) return resumed;
                return TierZero.Make(requestedRunId, "RUNNING", "REPORT", "WAIT_TIMEOUT",
                    new JsonObject { ["wait_ms"] = waitMs0 }, null,
                    "块状续等未到终态：再次调用 run_iteration{run_id:'" + requestedRunId
                    + "'} 继续续等（每轮 ≤wait_ms）",
                    new JsonObject { ["status"] = "running", ["holder"] = holder });
            }
            if (holder != null) AttachEta(holder);
            return TierZero.Make("-", "BUSY", "PREFLIGHT", "ITERATION_BUSY",
                null, null,
                "已有迭代在跑：同 run_id 重复调用 run_iteration 可块状续等终态；或 get_iteration 轮询 holder",
                new JsonObject { ["holder"] = holder });
        }

        _autoReloadedThisRun = false;
        _shadowMode = false;
        var started = false;
        try
        {
            string runId = "-", topic = "adhoc";
            int waitMs;
            string? fixture = null;

            try
            {

                string? provided = Ledger.SanitizeRunId(OptString(args, "run_id"));
                runId = provided.Length > 0 ? provided : Ledger.MintRunId(OptString(args, "topic") ?? "adhoc");
                topic = OptString(args, "topic") ?? "adhoc";

                if (provided.Length > 0)
                {
                    try
                    {
                        var stage = Ledger.TryLoadStage(runId);
                        if (stage != null)
                        {
                            var stageName = stage["stage"]?.GetValue<string>();
                            if (stageName == "DONE")
                            {
                                if (Ledger.TryFindIteration(runId, out var existing)
                                    && existing["source"]?.GetValue<string>() == "status.json")
                                {
                                    existing["note"] = "run_id 已存在（幂等键命中），返回已记录终态；要重跑请换 run_id";
                                    return existing;
                                }
                            }
                            else if (stage["engine_pid"]?.GetValue<int>() == Environment.ProcessId)
                            {

                                return TierZero.Make(runId, "RUNNING", stageName ?? "?", "RUN_ID_IN_PROGRESS",
                                    null, null,
                                    "该 run_id 正在后台执行：稍后 get_iteration{run_id} 轮询终态",
                                    new JsonObject { ["status"] = "running", ["run_id"] = runId });
                            }

                        }
                    }
                    catch {  }
                }

                int defaultWait = OptBool(args, "build") == true ? 15_000 : 100_000;
                waitMs = OptInt(args, "wait_ms") ?? defaultWait;
                waitMs = Math.Clamp(waitMs, 1_000, Repo.MaxWaitMs);

                _shadowMode = OptBool(args, "shadow") == true;
                fixture = OptString(args, "fixture");
            }
            catch (Exception ex)
            {
                return TierZero.Make(runId, "ERROR", "REPORT", "ARG_INVALID",
                    null, new[] { ex.GetType().Name + ": " + ex.Message },
                    "run_iteration 参数解析失败（迭代锁已归还）：修正参数后重试");
            }

            var task = Task.Run(() =>
            {

                // fixture 显式指定时图纸互斥键跟随 fixture(优先级:参数 > 环境变量 > 配置 > 最近图纸)
                var drawingLock = DrawingLock.TryEnter(fixture, out var drawingBusy, out var lockedDrawing);
                try
                {
                    if (drawingLock == null && drawingBusy != null)
                        return DrawingBusyResult(runId, topic, drawingBusy, lockedDrawing);
                    return RunStages(runId, topic, fixture, args);
                }
                catch (Exception ex) { return EngineCrashResult(runId, topic, ex); }
                finally
                {
                    if (drawingLock != null)
                    {
                        drawingLock.ReleaseMutex();
                        drawingLock.Dispose();
                    }
                    RunLock.Release();
                    ClearActiveRun(runId);
                }
            });
            started = true;

            if (task.Wait(waitMs))
                return task.Result;

            return TierZero.Make(runId, "RUNNING", "REPORT", "WAIT_TIMEOUT",
                new JsonObject { ["wait_ms"] = waitMs },
                null,
                "迭代仍在后台执行：get_iteration{run_id, section:'log'} 轮询，或 get_loop_status 看 active_run",
                new JsonObject { ["status"] = "running", ["topic"] = topic, ["run_id"] = runId });
        }
        finally
        {

            if (!started) RunLock.Release();
        }
    }

    private static (string verdict, JsonArray assertDetail, List<string> assertLines, bool diskFallbackUsed)
        AssertOnePass(string runId, string topic, string? command, string[] mustContain,
            List<string> logLines, bool dropped, DateTime actStartUtc, string? fixture,
            JsonObject timings, bool isShadowPass)
    {
        var assertDetail = new JsonArray();
        List<string> assertLines = logLines;
        bool diskFallbackUsed = false;

        if (dropped)
        {
            var diskLines = DiskLogFallback(actStartUtc, runId);
            if (diskLines.Count > 0)
            {
                assertLines = diskLines;
                diskFallbackUsed = true;
                if (!isShadowPass)
                    Ledger.SaveRunLog(runId + "-disk", diskLines);
            }
            else
            {

                return ("ERROR", assertDetail, assertLines, false);
            }
        }

        string[] blacklist = { "eDuplicateKey", "eLockViolation", "MissingMethod", "InvalidCast", "Unhandled", "FATAL" };
        foreach (var pattern in blacklist)
        {
            // 黑名单只认错误/警告级行（或无 Level 字段的行，如宿主环行/非 JSON）：
            // 业务 Information 行里出现 "Unhandled count: 0" 之类文案不再假 FAIL
            var hits = assertLines
                .Where(l => l.Contains(pattern, StringComparison.OrdinalIgnoreCase) && BlacklistLineCandidate(l))
                .Take(3).ToList();
            if (hits.Count > 0)
            {
                assertDetail.Add(new JsonObject
                {
                    ["pattern"] = pattern,
                    ["blacklist"] = true,
                    ["found"] = true,
                    ["hits"] = hits.Count
                });
                return ("FAIL", assertDetail, assertLines, diskFallbackUsed);
            }
        }

        bool allHit = true;
        foreach (var pattern in mustContain)
        {
            int hits = assertLines.Count(l =>
                l.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (hits == 0) allHit = false;
            assertDetail.Add(new JsonObject
            {
                ["pattern"] = pattern,
                ["found"] = hits > 0,
                ["hits"] = hits
            });
        }
        return (allHit ? "PASS" : "FAIL", assertDetail, assertLines, diskFallbackUsed);
    }

    private static JsonObject? BuildConsistency(string? verdictA, JsonArray detailA,
        string? verdictB, JsonArray detailB, JsonObject timings, long actMsB)
    {
        if (!_shadowMode || verdictB == null) return null;

        bool verdictsMatch = verdictA == verdictB;
        var block = new JsonObject
        {
            ["verdicts_match"] = verdictsMatch,
            ["a"] = new JsonObject { ["verdict"] = verdictA, ["assert"] = detailA.DeepClone() },
            ["b"] = new JsonObject
            {
                ["verdict"] = verdictB,
                ["assert"] = detailB.DeepClone(),
                ["act_ms_b"] = actMsB
            }
        };

        bool assertSame = AssertDetailEqual(detailA, detailB);
        block["assert_match"] = assertSame;

        JsonArray? diff = null;
        string level;
        string overrideCode = verdictA ?? "ERROR";
        if (verdictA == "PASS" && verdictB == "PASS") { level = "stable"; }
        else if (verdictA == "FAIL" && verdictB == "FAIL" && assertSame) { level = "stable_fail"; }
        else if (verdictA == "FAIL" && verdictB == "FAIL")
        {
            level = "minor_diff";
            diff = BuildAssertDiff(detailA, detailB);
        }
        else
        {

            level = "major_diff";
            diff = BuildAssertDiff(detailA, detailB);
            overrideCode = "INCONSISTENT_VERDICT";
        }
        block["level"] = level;
        if (diff != null) block["diff"] = diff;

        if (level == "major_diff")
            block["hint"] = "判决不稳：A=" + verdictA + " B=" + verdictB + "。先查环境/并发/race condition，不要盲目改代码";
        return block;
    }

    private static bool AssertDetailEqual(JsonArray a, JsonArray b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var ai = a[i] as JsonObject; var bi = b[i] as JsonObject;
            if (ai == null || bi == null) return false;
            if (ai["pattern"]?.GetValue<string>() != bi["pattern"]?.GetValue<string>()) return false;
            if (ai["hits"]?.GetValue<int>() != bi["hits"]?.GetValue<int>()) return false;
        }
        return true;
    }

    private static JsonArray BuildAssertDiff(JsonArray a, JsonArray b)
    {
        var diff = new JsonArray();

        var bMap = new Dictionary<string, int>();
        foreach (var n in b)
        {
            if (n is JsonObject o)
                bMap[o["pattern"]?.GetValue<string>() ?? ""] = o["hits"]?.GetValue<int>() ?? 0;
        }
        foreach (var n in a)
        {
            if (n is not JsonObject o) continue;
            var pat = o["pattern"]?.GetValue<string>() ?? "";
            var hitsA = o["hits"]?.GetValue<int>() ?? 0;
            bMap.TryGetValue(pat, out var hitsB);
            if (hitsA != hitsB)
                diff.Add(new JsonObject { ["pattern"] = pat, ["a_hits"] = hitsA, ["b_hits"] = hitsB });
        }
        return diff;
    }

    private static JsonObject DrawingBusyResult(string runId, string topic, string busy, string? drawing)
    {
        Program.Diag("drawing-lock", $"DRAWING_BUSY: {busy}");
        var result = TierZero.Make(runId, "ERROR", "PREFLIGHT", "DRAWING_BUSY",
            null, new[] { busy },
            "同图纸迭代正被另一引擎实例执行：等它跑完再重试；若确认对方已死（如 ZCode 会话已关），"
            + "重试会经 abandoned 接手自动恢复",
            new JsonObject { ["drawing"] = drawing ?? "-" });
        try
        {
            Ledger.AppendIteration(new JsonObject
            {
                ["run_id"] = runId,
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["topic"] = topic,
                ["verdict"] = "BUSY",
                ["phase"] = "PREFLIGHT",
                ["code"] = "DRAWING_BUSY"
            });
            Ledger.SaveStage(runId, "DONE", "ERROR", "PREFLIGHT", "DRAWING_BUSY");
        }
        catch { }
        return result;
    }

    private static JsonObject EngineCrashResult(string runId, string topic, Exception ex)
    {
        var result = TierZero.Make(runId, "ERROR", "REPORT", "ENGINE_CRASH",
            null, new[] { ex.GetType().Name + ": " + ex.Message },
            "引擎内部异常兜底：get_iteration{run_id, section:'log'} 下钻，或换 run_id 重跑");
        try
        {
            Ledger.AppendIteration(new JsonObject
            {
                ["run_id"] = runId,
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["topic"] = topic,
                ["verdict"] = "ERROR",
                ["phase"] = "REPORT",
                ["code"] = "ENGINE_CRASH"
            });
            Ledger.SaveStage(runId, "DONE", "ERROR", "REPORT", "ENGINE_CRASH");
        }
        catch { }

        if (_autoReloadedThisRun) result["auto_reloaded"] = true;
        return result;
    }

    private static bool? OptBool(JsonObject args, string key)
    {
        if (args[key] is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var b2)) return b2;
        }
        return null;
    }

    private static int? OptInt(JsonObject args, string key)
    {
        if (args[key] is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var i2)) return i2;
        }
        return null;
    }

    private static string? OptString(JsonObject args, string key)
    {
        return args[key] is JsonValue v ? OptStringNode(v) : null;
    }

    private static string? OptStringNode(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<bool>(out _)) return v.ToJsonString();
        if (v.TryGetValue<long>(out _)) return v.ToJsonString();
        if (v.TryGetValue<double>(out _)) return v.ToJsonString();
        return null;
    }

    private static JsonObject RunStages(string runId, string topic, string? fixture, JsonObject args)
    {
        var timings = new JsonObject();
        var swAll = System.Diagnostics.Stopwatch.StartNew();

        string? command = OptString(args, "command");
        bool doBuild = OptBool(args, "build") ?? false;
        int settleMs = OptInt(args, "settle_ms") ?? 1500;
        settleMs = Math.Clamp(settleMs, 0, 10_000);
        var expect = args["expect"] as JsonObject;
        var mustContain = (expect?["log_must_contain"] as JsonArray)?
            .Select(OptStringNode)
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .ToArray() ?? Array.Empty<string>();

        JsonObject? result = null;
        long seqBeforeAct = -1;
        long seqAtStart = -1;
        DateTime actStartUtc = DateTime.UtcNow;

        TouchActiveRun(runId, topic, "PREFLIGHT");
        Ledger.SaveStage(runId, "PREFLIGHT");
        var status = HostClient.Call("host.status", null);
        if (status.Ok)
        {

            seqAtStart = status.Data?["log_seq"]?.GetValue<long>() ?? -1;
        }
        if (!status.Ok)
        {
            result = TierZero.Make(runId, "ERROR", "PREFLIGHT",
                status.ErrorCode == "CAD_NOT_ATTACHED" ? "CAD_NOT_ATTACHED" : "HOST_UNREACHABLE",
                null, new[] { status.ErrorMessage ?? "host pipe not reachable" },
                "启动 acad（宿主自启），或 NETLOAD LoopHost.dll 后运行 LOOPHOST；多实例时设 LOOP_ACAD_PID");
        }
        else
        {
            var cad = status.Data?["cad"] as JsonObject;
            Repo.RememberDrawing(cad?["doc"]?.GetValue<string>());
            bool attached = cad?["attached"]?.GetValue<bool>() ?? false;
            int epoch = status.Data?["epoch"]?.GetValue<int>() ?? 0;
            if (!attached)
            {
                result = TierZero.Make(runId, "ERROR", "PREFLIGHT", "NO_DOCUMENT",
                    null, new[] { "host reachable but no active document" },
                    "在 acad 中打开 fixture 图纸");
            }
            else if (CadLooksHung(status.Data as JsonObject))
            {
                // P1-1 fail-fast：主线程疑似挂起时不再白跑（旧路径会在 ACT/COLLECT 挂满
                // 8min 轮询上限）。先 abort_cad 尝试 Esc 安全中止，无效再重启 acad。
                long stalled = status.Data?["stalled_ms"]?.GetValue<long>() ?? 0;
                long pending = status.Data?["pending_jobs"]?.GetValue<long>() ?? 0;
                result = TierZero.Make(runId, "ERROR", "PREFLIGHT", "CAD_HUNG",
                    null, new[] { $"stalled_ms={stalled}, pending_jobs={pending}" },
                    "CAD 主线程疑似挂起（regen/命令未返回）：先 abort_cad 尝试安全中止，"
                    + "30s 后 attach_status 复查仍 hung 则重启 acad");
            }
            else if (epoch <= 0)
            {

                TouchActiveRun(runId, topic, "RELOAD(auto)");
                Ledger.SaveStage(runId, "RELOAD(auto)");
                var reloadPayload = new JsonObject { ["dll_path"] = Repo.BusinessDll };
                string? disk = ToolSurface.ReadDiskVersion();
                if (disk != null) reloadPayload["expected_version"] = disk;
                string? guid = Repo.ReadDiskBuildGuid();
                if (guid != null) reloadPayload["expected_guid"] = guid;
                var autoReload = HostClient.Call("host.reload", reloadPayload);
                if (autoReload.Ok)
                {

                    epoch = (autoReload.Data as JsonObject)?["epoch"]?.GetValue<int>() ?? 0;
                    if (epoch <= 0)
                    {
                        status = HostClient.Call("host.status", null);
                        epoch = status.Data?["epoch"]?.GetValue<int>() ?? 0;
                    }
                }
                if (autoReload.Ok && epoch > 0)
                {
                    timings["auto_reload_ms"] = autoReload.ElapsedMs ?? 0;

                    _autoReloadedThisRun = true;
                }
                else
                {

                    var bizCode = (autoReload.Data as JsonObject)?["code"]?.GetValue<string>();
                    result = TierZero.Make(runId, "ERROR", "PREFLIGHT", "BUSINESS_NOT_REGISTERED",
                        null,
                        new[] { $"epoch={epoch} after auto-reload: {autoReload.ErrorMessage
                            ?? (bizCode != null ? "business reload failed: " + bizCode : "reload did not register tools")}" },
                        "auto-reload 未生效：调 reload_plugins 下钻，或 run_iteration 带 build:true 重跑");
                }
            }
        }

        string? dllVersion = null;
        if (result == null && doBuild)
        {
            TouchActiveRun(runId, topic, "BUILD");
            Ledger.SaveStage(runId, "BUILD");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var build = BuildRunner.Run();
            timings["build_ms"] = sw.ElapsedMilliseconds;
            if (!build.Ok)
            {
                result = TierZero.Make(runId, "FAIL", "BUILD", "BUILD_FAILED",
                    null,
                    (build.Errors.Count > 0 ? build.Errors : build.Tail).ToArray(),
                    "修复编译错误（证据含 file:line）",
                    new JsonObject { ["exit_code"] = build.ExitCode });
            }
            else
            {
                dllVersion = build.DllVersion;
            }
        }

        if (result == null)
        {
            TouchActiveRun(runId, topic, "RELOAD");
            Ledger.SaveStage(runId, "RELOAD");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var reloadPayload = new JsonObject { ["dll_path"] = Repo.BusinessDll };

            string? diskVersion = ToolSurface.ReadDiskVersion();
            reloadPayload["expected_version"] = diskVersion ?? "";
            string? guid = Repo.ReadDiskBuildGuid();
            if (guid != null) reloadPayload["expected_guid"] = guid;
            var reload = HostClient.Call("host.reload", reloadPayload);
            timings["reload_ms"] = sw.ElapsedMilliseconds;
            if (!reload.Ok)
            {
                result = TierZero.Make(runId, "ERROR", "RELOAD",
                    reload.ErrorCode ?? "RELOAD_FAILED", null,
                    new[] { reload.ErrorMessage ?? "reload failed" },
                    reload.ErrorCode == "CAD_BUSY"
                        ? "CAD 忙（模态/长命令）：稍后重试或关闭模态框"
                        : "reload 失败见 get_loop_status / tail_log；AGENT_* COM 兜底可用");
            }
            else if (reload.Data is JsonObject rd && rd["ok"]?.GetValue<bool>() == false)
            {

                result = TierZero.Make(runId, "ERROR", "RELOAD",
                    rd["code"]?.GetValue<string>() ?? "RELOAD_FAILED", null,
                    new[] { rd["message"]?.GetValue<string>() ?? "business reload failed" },
                    "热重载失败：get_iteration 下钻 / get_loop_status 看 state");
            }
            else
            {
                timings["reload_ms"] = reload.ElapsedMs ?? timings["reload_ms"];
            }
        }

        if (result == null && !string.IsNullOrEmpty(fixture))
        {
            // ARRANGE：fixture 实装（此前是空 if 块——参数收了不办事，跑在当前活动文档上）。
            // use_document open 自带"已打开→激活"语义：图纸已开则激活，未开则打开（P3D 挂载 20-45s）
            TouchActiveRun(runId, topic, "ARRANGE");
            Ledger.SaveStage(runId, "ARRANGE");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var arrange = HostClient.Call("use_document",
                new JsonObject { ["action"] = "open", ["path"] = fixture }, 65_000);
            if (!arrange.Ok && IsTimeoutCode(arrange.ErrorCode))
            {
                // 宿主 Idle 泵单任务 55s 上限：大图 open 超限时任务仍在主线程跑。
                // 轮询文档表等 fixture 出现，避免把"慢"误判成"失败"。
                long deadline = Environment.TickCount64 + 90_000;
                while (Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(3000);
                    var docs = HostClient.Call("query.documents", null, 10_000);
                    bool appeared = (docs.Data?["documents"] as JsonArray)?.Any(d =>
                        (d as JsonObject)?["path"]?.GetValue<string>() is { } p &&
                        string.Equals(p, fixture, StringComparison.OrdinalIgnoreCase)) == true;
                    if (appeared)
                    {
                        arrange = new HostClient.CallResult { Ok = true, Data = docs.Data };
                        break;
                    }
                }
            }
            timings["arrange_ms"] = sw.ElapsedMilliseconds;
            if (!arrange.Ok)
            {
                result = TierZero.Make(runId, "ERROR", "ARRANGE", "FIXTURE_OPEN_FAILED",
                    null,
                    new[] { arrange.ErrorCode + ": " + (arrange.ErrorMessage ?? "use_document open failed") },
                    "fixture 打开失败：核对路径/文件占用；P3D 大图挂载需 20-45s，可重试");
            }
            else
            {
                Repo.RememberDrawing(fixture);
            }
        }

        string? actError = null;
        string? actErrorB = null;
        long actMsB = 0;
        long seqBeforeActB = -1;
        DateTime actStartUtcB = DateTime.UtcNow;
        if (result == null && !string.IsNullOrEmpty(command))
        {
            TouchActiveRun(runId, topic, "ACT");
            Ledger.SaveStage(runId, "ACT");
            var guard = ToolSurface.CommandGuard(command);
            if (guard != null)
            {
                result = TierZero.Make(runId, "ERROR", "ACT", "TOOL_UNSAFE",
                    null, new[] { guard }, "命令须在 [AgentSafe] 白名单（AGENT_* / TEST_*）");
            }
            else
            {

                var status2 = HostClient.Call("host.status", null);
                seqBeforeAct = status2.Data?["log_seq"]?.GetValue<long>() ?? 0;
                actStartUtc = DateTime.UtcNow;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                // R27：ACT 异步化——受理即返 + act.job_status 免槽轮询到终态/预算顶。
                // 根治两类误报：①长命令撞 55s RPC 读超时被记 ACT_FAILED（命令实际在跑）；
                // ②actError 在 tail_log 正常应答路径永不清除（旧恢复块只救管道断开场景）。
                // 预算 = ActCompletePollMaxMs（默认 8min）；外层 wait_ms 先到则由 task.Wait
                // 出 RUNNING 判决书（后台任务继续收账，复用 run_id 幂等回放）。
                var act = ActSubmitAndWait(command);
                timings["act_ms"] = act.ms;
                if (!act.done) actError = act.error;

                if (_shadowMode)
                {
                    var status2b = HostClient.Call("host.status", null);
                    seqBeforeActB = status2b.Data?["log_seq"]?.GetValue<long>() ?? 0;
                    actStartUtcB = DateTime.UtcNow;
                    var swB = System.Diagnostics.Stopwatch.StartNew();
                    var actB = ActSubmitAndWait(command);
                    actMsB = actB.ms;
                    if (!actB.done) actErrorB = actB.error;
                }
            }
        }

        List<string> logLines = new();

        List<string> logLinesB = new();
        bool dropped = false;
        bool droppedB = false;
        if (result == null)
        {
            TouchActiveRun(runId, topic, "COLLECT");
            Ledger.SaveStage(runId, "COLLECT");
            Thread.Sleep(settleMs);

            // 翻页追赶：单次 tail 上限 500 行，长命令（如 TEST_T12_TRIALRUN ~1000 行）会被截头，
            // 判决行落在窗口外导致 EXPECT_NOT_MET。按 next_seq 续页直到追平或到达安全页数上限。
            (List<string> lines, bool droppedFlag, string? errCode, string? errMsg) TailPaged(long startSeq)
            {
                var collected = new List<string>();
                bool droppedPage = false;
                long cursor = startSeq;
                for (int page = 0; page < 20; page++)
                {
                    var tail = HostClient.Call("host.tail_log",
                        new JsonObject { ["since_seq"] = cursor, ["limit"] = 500 });
                    if (!tail.Ok)
                        return (collected, droppedPage,
                            tail.ErrorCode == "CAD_NOT_ATTACHED" ? "CAD_NOT_ATTACHED" : "HOST_LOG_UNREACHABLE",
                            tail.ErrorMessage ?? "host.tail_log failed");
                    int got = 0;
                    if (tail.Data?["lines"] is JsonArray arr)
                    {
                        collected.AddRange(arr.Select(n => n?.GetValue<string>() ?? ""));
                        got = arr.Count;
                    }
                    droppedPage |= tail.Data?["dropped"]?.GetValue<bool>() == true;
                    // 宿主 next_seq 曾是环写游标而非读位置：拿它当续读游标会整段跳过
                    // [本页尾, 写游标) 的日志（终局判决块正好落在跳过区 → EXPECT_NOT_MET
                    // 假判）。按本页行数推进，对旧/新宿主语义都正确。
                    if (got == 0 || got < 500) break;
                    cursor += got;
                }
                return (collected, droppedPage, null, null);
            }

            long seqA = seqBeforeAct >= 0 ? seqBeforeAct : seqAtStart >= 0 ? seqAtStart : 0;
            var pagedA = TailPaged(seqA);
            logLines = pagedA.lines;
            dropped = pagedA.droppedFlag;
            if (pagedA.errCode != null && actError != null
                && actError.StartsWith("TIMEOUT", StringComparison.Ordinal))
            {
                // 命令长于 act 读超时（默认 55s）：命令仍在 acad 主线程执行，宿主暂不接受
                // 新管道连接——tail_log 连不上 ≠ CAD 失联。轮询等宿主恢复应答（命令跑完），
                // 再收全窗口日志做真断言，避免把长命令误记成 ERROR/CAD_NOT_ATTACHED。
                long pollStart = Environment.TickCount64;
                long pollDeadline = pollStart + ActCompletePollMaxMs();
                bool recovered = false;
                while (Environment.TickCount64 < pollDeadline)
                {
                    Thread.Sleep(ActPollIntervalMs);
                    var probe = HostClient.Call("host.tail_log",
                        new JsonObject { ["since_seq"] = seqA, ["limit"] = 1 });
                    if (probe.Ok) { recovered = true; break; }
                }
                timings["act_poll_ms"] = Environment.TickCount64 - pollStart;
                if (recovered)
                {
                    // 管道恢复应答 ≠ 日志写完：实测 TEST_T12_V2ONLY 在终局行落盘前 ~5s 管道
                    // 已可应答，立即整窗重收会把"没收完"误判成 EXPECT_NOT_MET。
                    // 【根因已闭环 2026-09-12】当时"终局块只进磁盘不进环"实为引擎翻页游标
                    // bug（旧宿主 next_seq=写游标，续页整段跳过终局块），7467ad63 已修；
                    // 复核实测（1.2.4，R-20260912-010733）判决行现入环。双源扫描保留为
                    // 防御层：环溢出（>8192 行 wrap）时磁盘窗仍是唯一证据源。
                    long settleStart = Environment.TickCount64;
                    int quietIters = 0;
                    int quietMaxIters = Math.Max(1, (int)(ActCollectQuietMs() / ActPollIntervalMs));
                    int prevBizRing = -1;
                    int prevBizDisk = -1;
                    // P1-2：静默/尾限判据只认业务行。宿主噪声行（"host":"pipe" 连接心跳）由
                    // 本循环自己的轮询产生——旧判据统计环总行数，噪声行让行数永远在涨，
                    // 30s 静默窗永不达成，实测拖满 8min 轮询上限（R-20260912-194821 等 3 例）。
                    long lastBizGrowthTick = Environment.TickCount64;
                    long tailDeadlineMs = CollectTailDeadlineMs();
                    while (true)
                    {
                        var pagedRetry = TailPaged(seqA);
                        if (pagedRetry.errCode != null)
                        {
                            result = TierZero.Make(runId, "ERROR", "COLLECT", pagedRetry.errCode,
                                null, new[] { pagedRetry.errMsg },
                                "命令完成后日志汇仍不可达：get_loop_status 确认 CAD 在线后重跑（可复用本 run_id）");
                            break;
                        }
                        logLines = pagedRetry.lines;
                        dropped = pagedRetry.droppedFlag;

                        var diskLines = DiskLogFallback(actStartUtc, runId);
                        bool ringHasAll = mustContain.All(m =>
                            logLines.Any(l => l.Contains(m, StringComparison.OrdinalIgnoreCase)));
                        bool diskHasAll = mustContain.All(m =>
                            diskLines.Any(l => l.Contains(m, StringComparison.OrdinalIgnoreCase)));
                        if (mustContain.Length > 0 && diskHasAll && !ringHasAll)
                        {
                            // 标记只在磁盘窗（终局块不进环）：改用磁盘窗口作断言证据
                            logLines = diskLines;
                            dropped = false;
                        }

                        if (mustContain.Length > 0 && (ringHasAll || diskHasAll)) break;
                        int bizRing = logLines.Count(l => !IsHostNoiseLine(l));
                        int bizDisk = diskLines.Count(l => !IsHostNoiseLine(l));
                        if (bizRing != prevBizRing || bizDisk != prevBizDisk)
                            lastBizGrowthTick = Environment.TickCount64;
                        quietIters = bizRing == prevBizRing && bizDisk == prevBizDisk
                            ? quietIters + 1 : 0;
                        prevBizRing = bizRing;
                        prevBizDisk = bizDisk;
                        if (quietIters >= quietMaxIters
                            || Environment.TickCount64 - lastBizGrowthTick >= tailDeadlineMs
                            || Environment.TickCount64 >= pollDeadline) break;
                        Thread.Sleep(ActPollIntervalMs);
                    }
                    timings["act_settle_ms"] = Environment.TickCount64 - settleStart;
                    if (result == null)
                    {
                        actError = null;   // 管道已恢复，按日志断言判决
                        if (logLines.Count > 0) Ledger.SaveRunLog(runId, logLines);
                    }
                }
                else
                {
                    // 轮询到顶仍未恢复：命令超时未完成或 acad 挂死。记 ACT_TIMEOUT（不记
                    // CAD_NOT_ATTACHED——管道不通是命令占线的结果），业务日志尾部落盘作证据。
                    var diskTail = DiskLogFallback(actStartUtc, runId);
                    if (diskTail.Count > 0) Ledger.SaveRunLog(runId, diskTail);
                    logLines = diskTail;
                    result = TierZero.Make(runId, "ERROR", "ACT", "ACT_TIMEOUT",
                        timings,
                        new[] { actError, $"polled {timings["act_poll_ms"]}ms without host reconnection" },
                        "命令超过轮询上限未完成：以业务日志人工判定（get_iteration 看 run 日志），"
                        + "或拆小命令 / 检查 acad 是否挂死");
                }
            }
            else if (pagedA.errCode != null)
            {
                result = TierZero.Make(runId, "ERROR", "COLLECT", pagedA.errCode,
                    null, new[] { pagedA.errMsg },
                    "宿主日志汇不可达：get_loop_status 确认 CAD 在线后重跑（可复用本 run_id）");
            }
            else if (logLines.Count > 0)
            {
                Ledger.SaveRunLog(runId, logLines);
            }

            if (_shadowMode && result == null)
            {
                Thread.Sleep(settleMs);
                var pagedB = TailPaged(seqBeforeActB >= 0 ? seqBeforeActB : 0);
                logLinesB = pagedB.lines;
                droppedB = pagedB.droppedFlag;
                if (pagedB.errCode != null)
                {
                    result = TierZero.Make(runId, "ERROR", "COLLECT", pagedB.errCode,
                        null, new[] { "(shadow B) " + pagedB.errMsg },
                        "shadow pass B 日志汇不可达：上游 attach 可能断开，rerun 同参");
                }
                else if (logLinesB.Count > 0)
                {
                    Ledger.SaveRunLog(runId + "-b", logLinesB);
                }
            }
        }

        string? verdictA = null, verdictB = null;
        JsonArray assertDetailA = new(), assertDetailB = new();
        List<string> assertLines = logLines;
        bool diskFallbackUsed = false, diskFallbackUsedB = false;

        if (result == null)
        {
            TouchActiveRun(runId, topic, "ASSERT");
            Ledger.SaveStage(runId, "ASSERT");
            if (actError != null)
            {
                // R27：actError 现只来自真实失败（提交被拒/JOB_FAILED/JOB_LOST/超限 RUNNING）——
                // 55s RPC 读超时误报已由异步受理根治，不再是本分支的常客
                result = TierZero.Make(runId, "ERROR", "ACT", "ACT_FAILED",
                    null, new[] { actError }, "ACT 阶段失败：看 evidence 判定（被拒=白名单/占线；"
                    + "JOB_FAILED=业务命令抛异常；TIMEOUT=超过轮询上限仍在跑，wait_job 续等）");
            }
            else
            {

                var (vA, dA, lA, fbA) = AssertOnePass(runId, topic, command, mustContain,
                    logLines, dropped, actStartUtc, fixture, timings, isShadowPass: false);
                verdictA = vA; assertDetailA = dA; assertLines = lA; diskFallbackUsed = fbA;
                if (vA == "ERROR" && result == null) result = null;

                if (_shadowMode && actErrorB == null && vA != "ERROR")
                {
                    var (vB, dB, lB, fbB) = AssertOnePass(runId, topic, command, mustContain,
                        logLinesB, droppedB, actStartUtcB, fixture, timings, isShadowPass: true);
                    verdictB = vB; assertDetailB = dB; diskFallbackUsedB = fbB;
                }
                else if (_shadowMode && actErrorB != null)
                {
                    verdictB = "ERROR";
                }
            }
        }

        bool pass = verdictA == "PASS";
        var assertDetail = assertDetailA;

        TouchActiveRun(runId, topic, "REPORT");
        Ledger.SaveStage(runId, "REPORT");
        timings["total_ms"] = swAll.ElapsedMilliseconds;

        string finalVerdict = pass == true ? "PASS" : "FAIL";
        string finalCode = pass == true ? "OK" : "EXPECT_NOT_MET";
        string? finalHint = null;
        JsonObject? consistency = BuildConsistency(verdictA, assertDetailA, verdictB, assertDetailB, timings, actMsB);
        if (consistency != null)
        {
            string level = consistency["level"]?.GetValue<string>() ?? "";
            if (level == "major_diff")
            {
                finalVerdict = "ERROR";
                finalCode = "INCONSISTENT_VERDICT";
                finalHint = consistency["hint"]?.GetValue<string>();
            }

            else if (level == "minor_diff")
            {
                finalHint = "shadow 双跑 FAIL 一致但 assert 详情有差异：" + consistency["diff"]?.ToJsonString();
            }
        }

        result ??= TierZero.Make(runId, finalVerdict,
            "REPORT", finalCode,
            timings,
            assertLines.Take(3).Select(TierZero.TrimLine),
            finalHint ?? (pass == true ? null : "下钻 get_iteration{run_id} 查看全量日志"),
            new JsonObject
            {
                ["topic"] = topic,
                ["assert"] = assertDetail,
                ["log_lines"] = assertLines.Count,
                ["dropped"] = dropped,
                ["arrange"] = string.IsNullOrEmpty(fixture) ? "none" : "ARRANGE(open|activate: " + fixture + ")"
            });

        // P3-4：引擎 PASS 只覆盖"命令执行完成"；证据窗出现业务终局 FAIL 标记（DONE:FAIL
        // 样式）时给通用提示——非门禁，业务断言正道仍是 expect.log_must_contain
        if (result["verdict"]?.GetValue<string>() == "PASS"
            && assertLines.Any(l => l.Contains("DONE:FAIL", StringComparison.Ordinal)))
        {
            result["business_hint"] = "evidence window contains DONE:FAIL — engine PASS covers "
                + "execution only; inspect business verdict via get_iteration{section:'log'}";
        }

        var ledgerRow = new JsonObject
        {
            ["run_id"] = runId,
            ["ts"] = DateTime.UtcNow.ToString("o"),
            ["topic"] = topic,
            ["command"] = command,
            ["build"] = doBuild,
            ["verdict"] = result["verdict"]?.DeepClone() ?? "ERROR",
            ["phase"] = result["phase"]?.DeepClone() ?? "REPORT",
            ["code"] = result["code"]?.DeepClone() ?? "?",
            ["timings_ms"] = timings.DeepClone(),
            ["log_lines"] = assertLines.Count,
            ["hypothesis"] = args["hypothesis"]?.DeepClone()
        };
        if (_shadowMode) ledgerRow["shadow"] = true;
        if (consistency != null) ledgerRow["consistency"] = consistency.DeepClone();
        Ledger.AppendIteration(ledgerRow);
        Ledger.SaveStage(runId, "DONE",
            result["verdict"]?.GetValue<string>(), result["phase"]?.GetValue<string>(),
            result["code"]?.GetValue<string>());

        if (_autoReloadedThisRun) result["auto_reloaded"] = true;

        if (consistency != null) result["consistency"] = consistency;
        return result;
    }

    private const int ActPollIntervalMs = 2000;

    private static bool IsTimeoutCode(string? code)
        => code is "MAIN_THREAD_TIMEOUT" or "TIMEOUT";

    // act 读超时后等待长命令跑完的轮询上限。默认 8min（最长门禁 TEST_T12_FIXTURE
    // ~201s 的 24 倍），可用配置 act_complete_poll_max_ms 覆盖（下限 10s）。
    private static long ActCompletePollMaxMs()
        => long.TryParse(Repo.ConfigString("act_complete_poll_max_ms"), out var ms) && ms >= 10_000
            ? ms : 480_000;

    /// <summary>R27 ACT 异步执行：受理（act.run_command_async）+ 免槽轮询（act.job_status）
    /// 到终态或 ActCompletePollMaxMs。旧宿主（无异步面）回退旧同步 RPC（含 55s 误报语义，
    /// 由既有 TIMEOUT 恢复块兜底）。done=true 仅代表拿到终态 DONE；FAIL/超时走 error 通道。</summary>
    private static (bool done, string? error, long ms) ActSubmitAndWait(string command)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var submit = JobRunner.Submit(command);
        if (!submit.Ok)
        {
            if (string.Equals(submit.ErrorCode, "TOOL_NOT_FOUND", StringComparison.Ordinal))
            {
                // 旧宿主：无 act.run_command_async——逐位保留旧路径
                var legacy = HostClient.Call("act.run_command", new JsonObject { ["command"] = command });
                sw.Stop();
                return legacy.Ok
                    ? (true, null, sw.ElapsedMilliseconds)
                    : (false, legacy.ErrorCode + ": " + (legacy.ErrorMessage ?? ""), sw.ElapsedMilliseconds);
            }
            return (false, submit.ErrorCode + ": " + (submit.ErrorMessage ?? ""), sw.ElapsedMilliseconds);
        }
        var w = JobRunner.Wait(submit.JobId!, ActCompletePollMaxMs());
        sw.Stop();
        return w.State switch
        {
            "DONE" => (true, null, w.ElapsedMs),
            "FAIL" => (false, "JOB_FAILED: " + (w.ErrorText ?? "command threw on main thread"), w.ElapsedMs),
            "UNKNOWN" => (false, "JOB_LOST: " + (w.ErrorText ?? "host lost the job"), sw.ElapsedMilliseconds),
            // 预算（默认 8min）耗尽仍在跑：命令超限——沿用 TIMEOUT 语义（外层 wait_ms
            // 通常更先到，由 task.Wait 出 RUNNING 判决书；走到这里说明调用方给了超大预算）
            _ => (false, "TIMEOUT: async job " + submit.JobId + " still RUNNING after "
                + ActCompletePollMaxMs() + "ms — wait_job(job_id) 续等或检查 acad 是否挂死",
                sw.ElapsedMilliseconds)
        };
    }

    // 恢复后判定"日志写完"的静默窗：整窗连续无增长达此时长才接受当前窗口断言。
    // 实测 TEST_T12_V2ONLY 终局行前有 ~21s 无日志的收尾段，默认 30s 盖住它；
    // 可用配置 act_collect_quiet_ms 覆盖（下限 10s）。
    private static long ActCollectQuietMs()
        => long.TryParse(Repo.ConfigString("act_collect_quiet_ms"), out var ms) && ms >= 10_000
            ? ms : 30_000;

    // P1-2 收尾硬上限：最后一条业务日志出现后再等此时长即强制接受当前窗口（正常路径
    // 30s 静默窗会更早触发，这里是兜底）。可用配置 collect_tail_deadline_ms 覆盖（下限 30s）。
    private static long CollectTailDeadlineMs()
        => long.TryParse(Repo.ConfigString("collect_tail_deadline_ms"), out var ms) && ms >= 30_000
            ? ms : 90_000;

    // P1-1：宿主主线程疑似挂起判定。新宿主直接给 cad.hung；旧宿主用等价口径
    // （队列非空且 >120s 无 Idle）在引擎侧兜底判定。
    internal static bool CadLooksHung(JsonObject? hostStatus)
    {
        if (hostStatus == null) return false;
        if (hostStatus["cad"] is JsonObject cad && cad["hung"]?.GetValue<bool>() == true) return true;
        long stalled = hostStatus["stalled_ms"]?.GetValue<long>() ?? 0;
        long pending = hostStatus["pending_jobs"]?.GetValue<long>() ?? 0;
        return pending > 0 && stalled > 120_000;
    }

    private static List<string> DiskLogFallback(DateTime actStartUtc, string runId)
    {
        var result = new List<string>();
        try
        {
            var dir = Repo.ConfigString("disk_log_dir") ?? @"C:\Logs\Plant3D";
            var windowStart = actStartUtc.AddSeconds(-1);

            foreach (var date in new[] { DateTime.Now, DateTime.Now.AddDays(-1) })
            {
                var path = Path.Combine(dir, Repo.DiskLogFilePrefix + date.ToString("yyyyMMdd") + ".json");
                if (!File.Exists(path)) continue;

                var tail = new Queue<string>(5000);
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length == 0) continue;
                    tail.Enqueue(line);
                    while (tail.Count > 5000) tail.Dequeue();
                }

                foreach (var line in tail)
                {
                    string? ts = ExtractJsonString(line, "Timestamp");
                    if (ts != null && DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    {
                        if (t.ToUniversalTime() >= windowStart) result.Add(line);
                    }
                    else if (line.Contains($"\"run_id\":\"{runId}\"", StringComparison.Ordinal))
                    {
                        result.Add(line);
                    }
                }
            }
        }
        catch { }
        return result;
    }

    private static bool BlacklistLineCandidate(string line)
    {
        var level = ExtractJsonString(line, "Level");
        if (level == null) return true;
        return level.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || level.Equals("Fatal", StringComparison.OrdinalIgnoreCase)
            || level.Equals("Warning", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractJsonString(string line, string key)
    {
        var needle = "\"" + key + "\":\"";
        int idx = line.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) return null;
        int start = idx + needle.Length;
        int end = line.IndexOf('"', start);
        return end < 0 ? null : line.Substring(start, end - start);
    }
}

