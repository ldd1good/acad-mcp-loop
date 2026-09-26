namespace LoopEngine;

internal sealed class MutationGateLease : IDisposable
{
    internal static readonly MutationGateLease Instance = new();
    private MutationGateLease() { }
    public void Dispose() => Iteration.RunLock.Release();
}

internal static class MutationGate
{
    public static MutationGateLease? TryAcquire(out JsonObject? busy)
    {
        if (Iteration.RunLock.Wait(0))
        {
            busy = null;
            return MutationGateLease.Instance;
        }
        var holder = Iteration.ActiveRunSnapshot();
        Iteration.MarkStaleHolder(holder);
        busy = new JsonObject
        {
            ["ok"] = false,
            ["code"] = "BUSY",
            ["message"] = "mutation tool rejected: another iteration/reload holds the mutation lock",
            ["holder"] = holder != null ? holder.DeepClone() : null
        };
        return null;
    }
}

public static class ToolSurface
{
    public static readonly (string Name, string Desc, string Schema)[] Tools =
    {

        ("run_iteration",
         "[iterate] 一次调用跑完 8 阶段（PREFLIGHT→REPORT），返回 ≤2KB Tier-0 判决书。epoch=0 自动补 reload（判决书带 auto_reloaded:true）。shadow:true 双跑对账（顶层 consistency 字段）。build=true 时 wait_ms 默认 15s；上限=客户端超时-5s；超时返回 RUNNING+run_id。BUSY 时 holder 带 expected_release_at（估算）；对同一 run_id 重复调用=块状续等（每轮 ≤wait_ms，终态幂等回放），不再需要外部轮询脚本。",
         @"{""type"":""object"",""properties"":{""topic"":{""type"":""string"",""description"":""任务主题（必填，进台账）""},""run_id"":{""type"":""string"",""description"":""幂等键；已存在回放终态；在跑时重复调用=块状续等""},""command"":{""type"":""string"",""description"":""白名单命令 AGENT_* / TEST_*""},""build"":{""type"":""boolean"",""description"":""先编译（build.ps1）""},""shadow"":{""type"":""boolean"",""description"":""双跑对账（暴露裁决稳定性，cost 近似翻倍）""},""settle_ms"":{""type"":""integer"",""description"":""COMMAND 后等日志窗口（ms）""},""wait_ms"":{""type"":""integer"",""description"":""总等待上限（ms）""},""fixture"":{""type"":""string"",""description"":""fixture 图纸路径；ARRANGE 阶段自动 open（已打开则激活）后再执行命令，失败判 FIXTURE_OPEN_FAILED""},""hypothesis"":{""type"":""string"",""description"":""先写假设再跑（repro-before-fix）""},""expect"":{""type"":""object"",""description"":""断言规则，如 {log_must_contain:['PASS']}""}},""required"":[""topic""]}"),
        ("get_iteration",
         "[iterate] 按 run_id 下钻：section=full（判决+末 20 行日志）/ log（过滤后末 limit 行纯日志）。日志默认排除宿主 pipe 心跳噪声行，contains/level 可过滤；运行中的 run 返回 phase/elapsed_ms + 磁盘业务日志实时尾 live_tail。",
         @"{""type"":""object"",""properties"":{""run_id"":{""type"":""string"",""description"":""迭代 run_id（必填）""},""section"":{""type"":""string"",""description"":""full | log""},""limit"":{""type"":""integer"",""description"":""log 模式行数上限（取过滤后末尾）""},""contains"":{""type"":""string"",""description"":""子串过滤（不区分大小写）""},""level"":{""type"":""string"",""description"":""按行内 Level 字段过滤（Error/Warning/Information…）""},""include_host_noise"":{""type"":""boolean"",""description"":""默认 false 排除宿主 pipe 心跳行；true 看全量""}},""required"":[""run_id""]}"),
        ("diagnose",
         "[iterate] 失败诊断包：root_cause + 同 topic 历史 PASS 率 + next_actions（确定性规则引擎）。FAIL/ERROR 后第一选择。",
         @"{""type"":""object"",""properties"":{""run_id"":{""type"":""string"",""description"":""要诊断的 run_id（必填）""},""depth"":{""type"":""string"",""description"":""full | summary（默认 full）""}},""required"":[""run_id""]}"),
        ("list_iterations",
         "[iterate] 迭代台账过滤：topic? / verdict? / limit?，返回 verdict 计数汇总。",
         @"{""type"":""object"",""properties"":{""topic"":{""type"":""string"",""description"":""主题过滤""},""verdict"":{""type"":""string"",""description"":""PASS / FAIL / ERROR / RUNNING / BUSY""},""limit"":{""type"":""integer"",""description"":""返回条数上限""}}}"),

        ("attach_status",
         "[attach] read-only: cad 状态机（online/busy/starting/offline）+ 多实例清单 + infra 块（基建二进制 vs 源码陈旧检测：常驻进程占用会让 build-mcp.ps1 跳过构建，stale=true 时先修部署再信工具面）。要拉起/挂接查 attach_cad；要重载查 attach_reload。",
         @"{""type"":""object"",""properties"":{}}"),
        ("get_loop_status",
         "[attach·deprecated→attach_status] read-only: cad 状态机 + 多实例清单。",
         @"{""type"":""object"",""properties"":{}}"),
        ("attach_cad",
         "[attach] 拉起/挂接 AutoCAD Plant 3D：服务端等到宿主应答才返回 ready:true（wait_ms 默认 60s 上限 120s，2026-09-03 起不再让 agent 主动轮询）。返回值带 decision/cad_state；acad 已在跑时绝不拉新实例。",
         @"{""type"":""object"",""properties"":{""wait_ms"":{""type"":""integer"",""description"":""服务端等到 ready 的最长时间（默认 60000，上限 120000）""}}}"),
        ("start_or_attach_cad",
         "[attach·deprecated→attach_cad] 拉起/挂接 AutoCAD Plant 3D，服务端等到 ready。",
         @"{""type"":""object"",""properties"":{""wait_ms"":{""type"":""integer""}}}"),
        ("attach_reload",
         "[attach] 触发业务 DLL 热重载（不编译），返回 host_after 回读。run_iteration 在 epoch=0 也会自动补 reload，agent 看到 auto_reloaded:true 不必再调。",
         @"{""type"":""object"",""properties"":{}}"),
        ("reload_plugins",
         "[attach·deprecated→attach_reload] 触发业务 DLL 热重载。",
         @"{""type"":""object"",""properties"":{}}"),

        ("find_entities",
         "[query] 按 layer/type/text_contains 定位实体，返回句柄+摘要。space=model（默认，模型空间）/paper（布局/图纸空间——正交图标注常在此）。模型空间 count=0 时响应带 note 提醒勿直接下'未落图'结论。",
         @"{""type"":""object"",""properties"":{""layer"":{""type"":""string"",""description"":""图层名""},""type"":{""type"":""string"",""description"":""实体类型（Pipe/Equipment…）""},""text_contains"":{""type"":""string"",""description"":""文本子串匹配""},""space"":{""type"":""string"",""description"":""model（默认）| paper（布局/图纸空间）""},""limit"":{""type"":""integer"",""description"":""返回上限""}}}"),
        ("get_entity",
         "[query] 按句柄取 CAD 属性 + P3D 数据，支持 handles[] 批量。",
         @"{""type"":""object"",""properties"":{""handle"":{""type"":""string"",""description"":""单句柄""},""handles"":{""type"":""array"",""items"":{""type"":""string""},""description"":""批量句柄""}}}"),
        ("tail_log",
         "[query] 游标语义日志查询：since_seq? / run_id? / limit?。",
         @"{""type"":""object"",""properties"":{""since_seq"":{""type"":""integer"",""description"":""日志序号游标""},""run_id"":{""type"":""string"",""description"":""按迭代 run_id 过滤""},""limit"":{""type"":""integer"",""description"":""返回行数上限""}}}"),
        ("list_commands",
         "[query] 命令清单 + 白名单标记 + 调用频次（晋升纪律数据）。",
         @"{""type"":""object"",""properties"":{}}"),

        ("run_command",
         "[escape] 执行 [AgentSafe] 白名单命令（AGENT_*/TEST_*）；非白名单 TOOL_UNSAFE 拒绝。受理前自动陈旧防护：磁盘业务 DLL buildguid 与宿主内存不符时自动热重载（响应带 auto_reload）。R27 受理语义：命令异步提交到宿主主线程，本调用阻塞至多 wait_ms（默认 50s）——终态返回业务结果（state=DONE/FAIL）；仍在跑返回 {ok:true, state:RUNNING, job_id}（不是错误），用 wait_job 续等，零盲等零 TIMEOUT 误报。长命令（TEST_T12_FIXTURE 6~9min）传 wait_ms=600000 一次等到终态。旧宿主自动回退同步路径（async_capable:false）。prompt_risk=true 命令拖主线程慎用。",
         @"{""type"":""object"",""properties"":{""command"":{""type"":""string"",""description"":""AGENT_* 可带参数；TEST_* 裸名""},""wait_ms"":{""type"":""integer"",""description"":""本次阻塞等待上限（默认 50000，上限 600000）；RUNNING 后可 wait_job 续等""}},""required"":[""command""]}"),
        ("wait_job",
         "[escape·R27] 续等异步命令作业直至终态：同 job_id 重复调用=块状续等（每轮 ≤wait_ms）。终态返回 {state:DONE,data:业务结果} 或 {state:FAIL,error}；仍在跑返回 {ok:true,state:RUNNING}。配合 run_command 的受理语义消灭 sleep 盲等。",
         @"{""type"":""object"",""properties"":{""job_id"":{""type"":""string"",""description"":""run_command 受理返回的 job_id""},""wait_ms"":{""type"":""integer"",""description"":""本轮阻塞等待上限（默认 50000，上限 600000）""}},""required"":[""job_id""]}"),

        ("abort_cad",
         "[escape] acad 挂起时的安全中止：优先宿主 host.abort（Esc PostMessage + 命令行 \\x03 排队），宿主不可用则引擎侧直接 PostMessage VK_ESCAPE 到 acad 主窗口。异步投递不占主线程，挂起态可送达。降级顺序：abort_cad → 30s 后 attach_status 复查 → 仍 hung 才重启。",
         @"{""type"":""object"",""properties"":{}}"),

        ("list_documents",
         "[host] 列出 acad 当前打开的文档：路径/活动/只读/未命名。",
         @"{""type"":""object"",""properties"":{}}"),
        ("get_workspace",
         "[host] 活动文档 + 布局（tilemode/CTAB/cvport）+ 当前层 + 视图概貌（view_context: model|paper|viewport——viewport=双击进了浮动视口，view 是模型视图非纸面视图）。",
         @"{""type"":""object"",""properties"":{}}"),
        ("get_scene_summary",
         "[host] 模型空间摘要：实体总数 / 按类型计数 / 按图层 Top20 / 包围盒（上限 20 万实体）。",
         @"{""type"":""object"",""properties"":{}}"),
        ("get_sysvars",
         "[host] 批量读 AutoCAD 系统变量（最多 32 个；版本差异诊断首选）。",
         @"{""type"":""object"",""properties"":{""names"":{""type"":""array"",""items"":{""type"":""string""},""description"":""变量名数组""},""name"":{""type"":""string"",""description"":""单变量名（备选）""}},""required"":[""names""]}"),
        ("use_document",
         "[host] open | close | activate。open 需 path（P3D 挂载 20-45s）；close 需 save:true|false；activate 需 name 子串或 path。",
         @"{""type"":""object"",""properties"":{""action"":{""type"":""string"",""enum"":[""open"",""close"",""activate""]},""path"":{""type"":""string""},""name"":{""type"":""string""},""save"":{""type"":""boolean""},""read_only"":{""type"":""boolean""}},""required"":[""action""]}"),
        ("get_screenshot",
         "[host] 当前视图出图（业务无关基础能力，CapturePreviewImage 一步位图，不走 PLOT）。width 默认 3840（宽=高×~1.4126 锁定纵横比）；crop_w/crop_h 可选中心裁剪；crop_entity_prefix 按 xdata 前缀实体包围盒裁剪（如 'mlprobe:'，取证小实体一步到位）；crop_mm {x,y,w,h} 按纸面坐标矩形裁剪（返回 mm_per_px 换算率）。file 基名默认 shot。落 reports/agent-shots/，返回 path/w/h/latest(+frame/crop/ent/mm 路径)。",
         @"{""type"":""object"",""properties"":{""width"":{""type"":""integer""},""crop_w"":{""type"":""integer""},""crop_h"":{""type"":""integer""},""crop_entity_prefix"":{""type"":""string"",""description"":""按实体 xdata 1000 值含此前缀的包围盒并集裁剪""},""crop_mm"":{""type"":""object"",""description"":""纸面坐标矩形 {x,y,w,h}（当前视图图纸单位）""},""file"":{""type"":""string""},""dir"":{""type"":""string""}}}"),
        ("list_viewports",
         "[host] 活动布局视口清单（2026-09-19）：number/纸面矩形/on/locked/比例/视图方向/图层 + 布局打印信息（纸宽高/原点/旋转/纸面有效区）+ 幅外判定（off_sheet 视口的纸面标注不渲染不打印——正交图幅外视口排障首选，替代现写业务探针）。",
         @"{""type"":""object"",""properties"":{}}")
    };

    // [AgentSafe] 白名单正则——与宿主侧 LoopHost/PipeServer.cs GuardCommand 双道拦截，
    // 两处必须同步修改（引擎侧拦 MCP 入口，宿主侧拦管道入口）
    private const string AgentSafePattern =
        @"^(AGENT_[A-Za-z0-9_\-]+( .+)?|TEST_[A-Za-z0-9_\-]*|AUTO_ANNOTATE)$";

    public static string? CommandGuard(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "empty command";

        return System.Text.RegularExpressions.Regex.IsMatch(command, AgentSafePattern)
            ? null
            : $"command '{command}' not in [AgentSafe] whitelist (AGENT_*/TEST_*); PromptGuard lands in S6";
    }

    public static JsonObject? Call(string name, JsonObject args)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = CallCore(name, args);

        bool err = (res?["ok"] is JsonNode n && !n.GetValue<bool>()) || res?["error"] != null;
        Program.Diag("tool", $"{name} {(err ? "err" : "ok")} {sw.ElapsedMilliseconds}ms");

        if (res != null && _deprecatedToolNames.TryGetValue(name, out var preferred))
        {
            res["deprecated_alias"] = true;
            res["preferred"] = preferred;
            Program.Diag("legacy-tool-call", $"{name} → use {preferred} instead (deprecated since 2026-09-03, remove in v2.0.0)");
        }
        return res;
    }

    private static readonly Dictionary<string, string> _deprecatedToolNames = new()
    {
        ["get_loop_status"] = "attach_status",
        ["start_or_attach_cad"] = "attach_cad",
        ["reload_plugins"] = "attach_reload"
    };

    private static JsonObject? CallCore(string name, JsonObject args)
    {

        switch (name)
        {
            case "run_iteration":
                return Iteration.Run(args);

            case "diagnose":
            {
                var runId = args["run_id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(runId)) return Err("BAD_ARGS", "run_id required");
                return BuildDiagnostic(runId);
            }

            case "get_iteration":
            {
                var runId = args["run_id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(runId)) return Err("BAD_ARGS", "run_id required");
                int limit = args["limit"]?.GetValue<int>() ?? 200;
                string? contains = args["contains"] is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : null;
                string? level = args["level"] is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : null;
                bool includeNoise = args["include_host_noise"] is JsonValue nv
                    && nv.TryGetValue<bool>(out var nb) && nb;
                var row = Ledger.TryFindIteration(runId, out var found)
                    ? (JsonNode)found.DeepClone() : null;
                // P3-1：尾部语义 + 过滤（contains/level；默认排除宿主 pipe 心跳噪声行）
                var log = Ledger.LoadRunLog(runId, limit, contains, level, includeNoise);
                var section = args["section"]?.GetValue<string>() ?? "full";
                var res = new JsonObject
                {
                    ["run_id"] = runId,
                    ["found"] = row != null,
                    ["iteration"] = section == "log" ? null : row,
                    ["log_lines"] = log.Count
                };
                if (contains != null) res["filtered_by_contains"] = contains;
                if (level != null) res["filtered_by_level"] = level;

                if (row == null)
                {
                    var stage = Ledger.TryLoadStage(runId);
                    if (stage != null && stage["stage"]?.GetValue<string>() != "DONE")
                    {
                        var stagePid = stage["engine_pid"]?.GetValue<int>() ?? 0;
                        if (stagePid == Environment.ProcessId)
                        {
                            res["running"] = true;
                            res["stage"] = stage["stage"]?.DeepClone();
                            // P3-1：运行中可查——active_run 进度 + 磁盘业务日志实时尾
                            var snap = Iteration.ActiveRunSnapshot();
                            if (snap?["run_id"]?.GetValue<string>() == runId)
                            {
                                res["elapsed_ms"] = snap["elapsed_ms"]?.DeepClone();
                                res["phase"] = snap["phase"]?.DeepClone();
                            }
                            var live = Ledger.LiveTailFromDisk(runId, 20);
                            if (live.Count > 0)
                                res["live_tail"] = new JsonArray(live
                                    .Select(TierZero.TrimLine).Select(l => (JsonNode)l).ToArray());
                            else
                                res["live_note"] = "磁盘窗暂无带本 run_id 的业务行（命令可能尚未起跑）";
                            res["note"] = "迭代仍在后台执行，稍后再查";
                        }
                        else
                        {

                            res["abandoned"] = true;
                            res["stage"] = stage["stage"]?.DeepClone();
                            res["note"] = $"status.json 停在 {stage["stage"]}，但写入引擎 pid={stagePid} 已退出（半程轮）——复用本 run_id 重跑可覆盖";
                        }
                    }
                    else
                    {
                        res["note"] = "run_id not found in ledger or progress";
                    }
                }
                if (section == "log")
                {
                    res["log"] = new JsonArray(log.Take(limit).Select(TierZero.TrimLine)
                        .Select(l => (JsonNode)l).ToArray());
                }
                else
                {

                    res["log_tail"] = new JsonArray(log.TakeLast(20).Select(TierZero.TrimLine)
                        .Select(l => (JsonNode)l).ToArray());
                }
                return res;
            }

            case "list_iterations":
            {
                string? topic = args["topic"]?.GetValue<string>();
                string? verdict = args["verdict"]?.GetValue<string>();
                int limit = Math.Max(1, args["limit"]?.GetValue<int>() ?? 50);
                // 全量扫描后再截页：summary/count 是匹配总数，不再随 limit 截尾失真
                var rows = Ledger.ListIterations(topic, verdict, int.MaxValue);
                var verdicts = rows
                    .Select(n => n?["verdict"]?.GetValue<string>())
                    .Where(v => v != null).GroupBy(v => v)
                    .ToDictionary(g => g.Key!, g => g.Count());
                return new JsonObject
                {
                    ["count"] = rows.Count,
                    ["returned"] = Math.Min(limit, rows.Count),
                    ["verdict_summary"] = new JsonObject(
                        verdicts.Select(kv => new KeyValuePair<string, JsonNode?>(
                            kv.Key, kv.Value))),
                    ["iterations"] = new JsonArray(rows.Skip(Math.Max(0, rows.Count - limit))
                        .Select(o => (JsonNode)o.DeepClone()).ToArray())
                };
            }

            case "attach_status":
            case "get_loop_status":
            {

                var gate = HostClient.TryEnterHostGate(out var gateLease);
                if (gate != null)
                {
                    bool pipePresent = HostClient.AnyHostPipePresent();

                    return new JsonObject
                    {
                        ["engine"] = new JsonObject
                        {
                            ["name"] = "LoopEngine",
                            ["version"] = typeof(ToolSurface).Assembly.GetName().Version?.ToString(),
                            ["repo"] = Repo.Root,
                            ["business_dll_exists"] = File.Exists(Repo.BusinessDll)
                        },
                        ["active_run"] = Iteration.ActiveRunSnapshot() ?? null,
                        ["cad_online"] = pipePresent,
                        ["cad_state"] = pipePresent ? "busy" : "offline",
                        ["busy"] = true,
                        ["code"] = pipePresent ? "HOST_BUSY_SERIALIZE" : "HOST_PIPE_GONE",
                        ["note"] = pipePresent
                            ? "another cad-loop call is in flight; host pipe is present. "
                              + "cad-loop is a single-session channel — serialize calls "
                              + "(await each tool before issuing the next)."
                            : "host pipe vanished from the pipe namespace while a call was in flight "
                              + "— host likely died (was the business DLL reloaded?). Check acad, "
                              + "then start_or_attach_cad / restart acad.",
                        ["processes"] = BuildProcessInventory(0)
                    };
                }
                try
                {

                var instances = HostClient.Discover();

                var live = instances.Where(i => !i.Degraded).ToList();
                var res = new JsonObject
                {
                    ["engine"] = new JsonObject
                    {
                        ["name"] = "LoopEngine",
                        ["version"] = typeof(ToolSurface).Assembly.GetName().Version?.ToString(),
                        ["repo"] = Repo.Root,
                        ["business_dll_exists"] = File.Exists(Repo.BusinessDll)
                    },

                    ["active_run"] = Iteration.ActiveRunSnapshot() ?? null,
                    ["cad_online"] = live.Count > 0,

                    ["cad_state"] = (string?)null,
                    ["infra"] = InfraStaleness(),
                    ["processes"] = BuildProcessInventory(live.Count > 0 ? HostClient.SelectBest(live).Pid : 0)
                };
                if (live.Count > 0)
                {
                    var pick = HostClient.SelectBest(live);
                    res["host"] = pick.Status!.Data?.DeepClone();

                    if (pick.Status.Data?["protocol_version"] == null)
                        res["protocol_note"] = "host has no protocol_version field — legacy host; restart acad to upgrade";

                    Repo.RememberDrawing(pick.Status.Data?["cad"]?["doc"]?.GetValue<string>());

                    bool quiescent = pick.Status.Data?["cad"]?["quiescent"]?.GetValue<bool>() ?? true;
                    // P1-1：挂起态优先于 busy 透出（新宿主给 cad.hung，旧宿主引擎侧等价判定），
                    // 让挂起可见且可处置，而不是永久 RUNNING/busy
                    bool hung = Iteration.CadLooksHung(pick.Status.Data as JsonObject);
                    res["cad_state"] = hung ? "hung" : quiescent ? "online" : "busy";
                    if (hung)
                    {
                        res["hung"] = true;
                        res["hung_note"] = "主线程长时间无 Idle 且队列非空——疑似 regen/命令挂起："
                            + "先 abort_cad（Esc 安全中止），30s 后复查；仍 hung 则重启 acad";
                    }

                    if (instances.Count > 1)
                    {
                        var arr = new JsonArray();
                        foreach (var inst in instances)
                        {
                            var d = inst.Status!.Data;
                            arr.Add(new JsonObject
                            {
                                ["acad_pid"] = inst.Pid,
                                ["degraded"] = inst.Degraded,
                                ["epoch"] = d?["epoch"]?.DeepClone(),
                                ["attached"] = d?["cad"]?["attached"]?.DeepClone(),
                                ["doc"] = d?["cad"]?["doc"]?.DeepClone(),
                                ["state"] = d?["state"]?.DeepClone()
                            });
                        }
                        res["instances"] = arr;
                        res["note"] = $"multiple acad instances; selected pid={pick.Pid}; set env LOOP_ACAD_PID to pin one";
                    }
                }
                else if (instances.Count > 0)
                {

                    res["cad_state"] = "starting";
                    res["error"] = new JsonObject
                    {
                        ["code"] = "HOST_DEGRADED",
                        ["reason"] = "host_pipe_present_not_answering",
                        ["message"] = "host pipe(s) present (pid: " + string.Join(",", instances.Select(i => i.Pid)) +
                            ") but host.status not answering after retries — host hung or half-dead. " +
                            "Check acad responsiveness; restart acad if stuck."
                    };
                    res["processes"] = BuildProcessInventory(0);
                }
                else
                {

                    var inv = BuildProcessInventory(0);
                    int acadCount = inv["acad_count"]!.GetValue<int>();
                    bool noAcad = acadCount == 0;
                    var msg = noAcad
                        ? "no acad process is running — AutoCAD/Plant 3D is not started, or was closed (call start_or_attach_cad to launch)"
                        : $"acad process(es) running ({acadCount}) but no LoopHost pipe reachable — host not loaded yet or not auto-loaded (check bundle/registry auto-load)";
                    if (HostClient.LegacyPipePresent())
                        msg += "; legacy LoopHost (pipe without PID) detected — restart acad to upgrade";
                    res["cad_state"] = noAcad ? "offline" : "starting";
                    res["error"] = new JsonObject
                    {
                        ["code"] = "CAD_NOT_ATTACHED",
                        ["reason"] = noAcad ? "no_acad_process" : "acad_running_host_missing",
                        ["message"] = msg
                    };
                    res["processes"] = inv;
                }
                return res;
                }
                finally { HostClient.ExitHostGate(gateLease); }
            }

            case "run_command":
            {
                var command = args["command"]?.GetValue<string>();
                var guard = CommandGuard(command ?? "");
                if (guard != null) return Err("TOOL_UNSAFE", guard);
                // P0-2（2026-09-19）：受理前陈旧防护——比对磁盘 buildguid vs 宿主内存 guid，
                // 失配自动 host.reload。动机：本日两次 run_command"验证"跑在旧 DLL 上
                // （宿主 2400 / 磁盘 2402），修复被误判无效。新宿主受理侧另有同款防线（幂等）。
                var stale = EnsureFreshBusinessDll("run_command:" + command);
                if (stale != null && stale["ok"]?.GetValue<bool>() == false)
                    return stale; // 重载失败必须显式失败，不许带病执行
                // R27 受理语义：异步提交 + 块状等待。旧宿主（无 act.run_command_async）
                // 回退旧同步路径，行为与改造前逐位一致。
                var waitMs = Math.Clamp(args["wait_ms"]?.GetValue<int>() ?? JobRunner.DefaultWaitMs,
                    1_000, JobRunner.MaxWaitMs);
                var res = JobRunner.SubmitAndWaitResponse(command!, waitMs);
                if (stale != null) res["auto_reload"] = stale.DeepClone();
                if (res["async_capable"]?.GetValue<bool>() == false
                    && res["error"] is JsonObject e
                    && e["code"]?.GetValue<string>() == "TOOL_NOT_FOUND")
                {
                    var legacy = Passthrough("act.run_command",
                        new JsonObject { ["command"] = command });
                    legacy["legacy_sync"] = true;
                    legacy["note"] = "宿主未升级异步面（act.run_command_async 缺失），走旧同步路径：>55s 的命令会 TIMEOUT";
                    return legacy;
                }
                return res;
            }

            case "wait_job":
            {
                var jobId = args["job_id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(jobId)) return Err("BAD_ARGS", "job_id required");
                var waitMs = Math.Clamp(args["wait_ms"]?.GetValue<int>() ?? JobRunner.DefaultWaitMs,
                    1_000, JobRunner.MaxWaitMs);
                return JobRunner.WaitResponse(jobId!, waitMs);
            }

            case "list_viewports":
                return Passthrough("query.viewports", args ?? new JsonObject());

            case "abort_cad":
            {
                // P1-1 安全中止。逃生通道不走 MutationGate——迭代挂住时恰恰最需要它。
                // 优先宿主 host.abort（LoopHost 升级后自带 stalled 上下文 + 命令行 \x03
                // 排队兜底）；宿主未升级 / 管道不应答时引擎侧直接 PostMessage Esc。
                var via = HostClient.Call("host.abort", null, 15_000);
                if (via.Ok && via.Data is JsonObject hd && hd["ok"]?.GetValue<bool>() == true)
                {
                    return new JsonObject { ["ok"] = true, ["via"] = "host.abort", ["data"] = hd.DeepClone() };
                }
                int? preferredPid = null;
                try
                {
                    var inst = HostClient.Discover();
                    preferredPid = inst.Count > 0 ? inst[0].Pid : null;
                }
                catch { }
                var fb = AcadEscAbort.TrySendEscape(preferredPid);
                fb["via"] = "engine-fallback";
                if (via.ErrorCode != null)
                    fb["host_abort_error"] = via.ErrorCode + ": " + (via.ErrorMessage ?? "");
                return fb;
            }

            case "find_entities":
                return Passthrough("query.find_entities", args);
            case "get_entity":
                return Passthrough("query.get_entity", args);

            case "attach_cad":
            case "start_or_attach_cad":
                return EnsureCad(args);
            case "tail_log":
                return Passthrough("host.tail_log", args);
            case "list_commands":
                return Passthrough("query.list_commands", new JsonObject());

            case "list_documents":
                return Passthrough("query.documents", new JsonObject());
            case "get_workspace":
                return Passthrough("query.workspace", new JsonObject());
            case "get_scene_summary":
                return Passthrough("query.scene_summary", new JsonObject());
            case "get_sysvars":
                return Passthrough("query.sysvars", args);
            case "use_document":
            {
                var lease = MutationGate.TryAcquire(out var busy);
                if (lease == null) return busy;
                using (lease)
                {
                    return Passthrough("use_document", args);
                }
            }

            case "get_screenshot":
                return Passthrough("query.screenshot", args);

            case "attach_reload":
            case "reload_plugins":
            {

                var lease = MutationGate.TryAcquire(out var busy);
                if (lease == null) return busy;
                using (lease)
                {
                    if (!File.Exists(Repo.BusinessDll))
                        return Err("DLL_NOT_FOUND", Repo.BusinessDll);
                    var payload = new JsonObject { ["dll_path"] = Repo.BusinessDll };
                    string? diskVersion = ReadDiskVersion();
                    if (diskVersion != null) payload["expected_version"] = diskVersion;
                    string? guid = Repo.ReadDiskBuildGuid();
                    if (guid != null) payload["expected_guid"] = guid;
                    var res = Passthrough("host.reload", payload);
                    if (res["ok"]?.GetValue<bool>() == true)
                    {

                        var status = HostClient.Call("host.status", null, 5000);
                        if (status.Ok) res["host_after"] = status.Data?.DeepClone();
                    }
                    return res;
                }
            }

            default:
                return Err("UNKNOWN_TOOL", name);
        }
    }

    internal static string? ReadDiskVersion()
    {
        try
        {
            if (!File.Exists(Repo.BusinessDll)) return null;

            return System.Reflection.AssemblyName.GetAssemblyName(Repo.BusinessDll).Version?.ToString();
        }
        catch { return null; }
    }

    /// <summary>P0-2（2026-09-19）：受理前业务 DLL 陈旧检测。宿主内存 guid 与磁盘 sidecar
    /// 失配 → 自动 host.reload 并返回结果对象（auto_reloaded=true）；新鲜/无法判定返回 null
    /// （不阻塞——宿主不在线等后续 submit 自然报错）。动机：本日两次"验证"跑在旧 DLL 上。</summary>
    internal static JsonObject? EnsureFreshBusinessDll(string reason)
    {
        try
        {
            if (!File.Exists(Repo.BusinessDll)) return null;
            string? diskGuid = Repo.ReadDiskBuildGuid();
            if (string.IsNullOrEmpty(diskGuid)) return null;
            var st = HostClient.Call("host.status", null, 5000);
            if (!st.Ok) return null;
            string? loaded = st.Data?["host"]?["build_guid"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(loaded)
                && string.Equals(loaded, diskGuid, StringComparison.OrdinalIgnoreCase))
                return null;
            string shortGuid = diskGuid!.Length > 8 ? diskGuid[..8] : diskGuid;
            Program.Diag("stale-guard", $"{reason}: host loaded={(loaded ?? "none")} disk={shortGuid} → auto host.reload");
            var payload = new JsonObject { ["dll_path"] = Repo.BusinessDll, ["expected_guid"] = diskGuid };
            string? diskVersion = ReadDiskVersion();
            if (diskVersion != null) payload["expected_version"] = diskVersion;
            var res = Passthrough("host.reload", payload);
            res["auto_reloaded"] = true;
            res["reason"] = $"{reason}: loaded={(loaded ?? "none")} disk={diskVersion}@{shortGuid}";
            return res;
        }
        catch (Exception ex)
        {
            Program.Diag("stale-guard", "check failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>基建部署偏差检测（2026-09-19 根因：运行 LoopEngine 1.2.1（9/7 构建）而源码已
    /// 1.2.5+R27——wait_job/get_screenshot/space 参数"已实现但从未部署"，agent 分析了不存在的
    /// 缺口）。以 exe 构建时间 vs 源码最新修改时间判陈旧；build-mcp.ps1 锁跳过时留下的
    /// STALE-SKIP 标记一并透出。</summary>
    internal static JsonObject InfraStaleness()
    {
        var o = new JsonObject();
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            o["engine_exe"] = exe;
            DateTime exeTime = File.GetLastWriteTimeUtc(exe ?? "");
            o["engine_built_utc"] = exeTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            DateTime newest = exeTime;
            string? newestFile = null;
            foreach (var dir in new[] { "tools/LoopEngine", "LoopHost" })
            {
                var full = Path.Combine(Repo.Root, dir);
                if (!Directory.Exists(full)) continue;
                foreach (var f in Directory.EnumerateFiles(full, "*.cs", SearchOption.TopDirectoryOnly))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > newest) { newest = t; newestFile = dir + "/" + Path.GetFileName(f); }
                }
            }
            o["source_newest_utc"] = newest.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            if (newestFile != null) o["source_newest_file"] = newestFile;
            bool stale = newestFile != null; // 有任一源码晚于 exe 构建 → 二进制落后
            o["stale"] = stale;
            if (stale)
            {
                o["note"] = "基建二进制落后于源码（常驻进程占用致 build-mcp.ps1 跳过）。"
                    + "修复：结束占用会话后运行 build-mcp.ps1；acad 需重启加载新 LoopHost。"
                    + "此状态下的工具面可能缺 wait_job/get_screenshot/list_viewports 等新能力。";
            }
        }
        catch (Exception ex) { o["error"] = ex.Message; }
        // build-mcp.ps1 锁跳过标记（若有）
        try
        {
            var marker = Path.Combine(Repo.Root, "tools", "LoopEngine", "bin", "STALE-SKIP.marker");
            if (File.Exists(marker))
                o["stale_skip_marker"] = File.ReadAllText(marker).Trim();
        }
        catch { }
        return o;
    }

    private static readonly object _procSnapLock = new();
    private static int[] _lastPids = Array.Empty<int>();
    private static bool _hasProcSnapshot;

    private static JsonObject BuildProcessInventory(int selectedPid)
    {
        var procs = HostClient.ListAcadProcesses();
        var arr = new JsonArray();
        var warnings = new JsonArray();
        int onlineCount = 0;
        int readOnlyCount = 0;

        foreach (var p in procs)
        {
            p.PipeName = $"{HostClient.BasePipeName}.{p.Pid}";
            p.HostOnline = HostClient.PipePresent(p.PipeName);
            if (p.HostOnline) onlineCount++;
            if (p.ReadOnly) readOnlyCount++;

            arr.Add(new JsonObject
            {
                ["pid"] = p.Pid,
                ["title"] = p.Title,
                ["read_only"] = p.ReadOnly,
                ["responding"] = p.Responding,
                ["started_utc"] = p.StartedUtc,
                ["working_set_mb"] = p.WorkingSetMb,
                ["host_online"] = p.HostOnline,
                ["pipe"] = p.HostOnline ? p.PipeName : null,
                ["selected"] = selectedPid > 0 && p.Pid == selectedPid
            });
        }

        if (procs.Count > 1)
            warnings.Add($"multiple acad instances ({procs.Count}); engine selected pid={selectedPid}; set env LOOP_ACAD_PID to pin one");
        if (onlineCount > 1)
            warnings.Add("multiple live hosts — pipe routing may pick an unintended instance");
        if (readOnlyCount > 0)
            warnings.Add($"{readOnlyCount} acad instance(s) opened the drawing read-only (another instance already holds it)");
        if (procs.Count > 0 && onlineCount == 0)
            warnings.Add("acad running but no host pipe — LoopHost not loaded (check bundle/registry auto-load) or still starting");

        var nowPids = new List<int>();
        foreach (var p in procs) nowPids.Add(p.Pid);

        var prevArr = new JsonArray();
        var newArr = new JsonArray();
        var goneArr = new JsonArray();
        bool firstCheck;
        lock (_procSnapLock)
        {
            firstCheck = !_hasProcSnapshot;
            if (!firstCheck)
            {
                foreach (var pid in _lastPids) prevArr.Add(pid);
                foreach (var pid in nowPids)
                    if (Array.IndexOf(_lastPids, pid) < 0) newArr.Add(pid);
                foreach (var pid in _lastPids)
                    if (!nowPids.Contains(pid)) goneArr.Add(pid);
            }
            _lastPids = nowPids.ToArray();
            _hasProcSnapshot = true;
        }

        var self = System.Diagnostics.Process.GetCurrentProcess();
        string? selfStart = null;
        try { selfStart = self.StartTime.ToUniversalTime().ToString("o"); } catch { }

        string? deltaNote = firstCheck
            ? "first check in this engine process — no baseline yet; call again to get deltas"
            : goneArr.Count > 0
                ? $"{goneArr.Count} acad process(es) exited since last check — CAD was closed (by the user, or crashed)"
                : newArr.Count > 0
                    ? $"{newArr.Count} new acad process(es) since last check"
                    : "no change since last check";

        return new JsonObject
        {
            ["acad_count"] = procs.Count,
            ["acad"] = arr,
            ["host_online_count"] = onlineCount,
            ["loop_engine"] = new JsonObject
            {
                ["pid"] = self.Id,
                ["started_utc"] = selfStart
            },
            ["warnings"] = warnings,
            ["since_last_check"] = new JsonObject
            {
                ["first_check"] = firstCheck,
                ["previous_pids"] = prevArr,
                ["new_pids"] = newArr,
                ["gone_pids"] = goneArr,
                ["note"] = deltaNote
            }
        };
    }

    private static JsonObject Passthrough(string hostTool, JsonObject args)
    {
        var r = HostClient.Call(hostTool, args);
        if (r.Ok) return new JsonObject { ["ok"] = true, ["data"] = r.Data?.DeepClone() };
        return Err(r.ErrorCode ?? "HOST_ERROR", r.ErrorMessage ?? "host call failed");
    }

    private static JsonObject EnsureCad(JsonObject args)
    {
        var lease = MutationGate.TryAcquire(out var busy);
        if (lease == null) return busy;
        using (lease)
        {
            return EnsureCadCore(args);
        }
    }

    private static JsonObject EnsureCadCore(JsonObject args)
    {
        JsonObject Attached(List<HostClient.HostInstance> live, string decision)
        {
            var attached = HostClient.SelectBest(live);
            var r = CadAttached(new JsonObject
            {
                ["ok"] = true,
                ["decision"] = decision,
                ["acad_pid"] = attached.Pid
            });
            r["ready"] = true;
            r["cad_state"] = attached.Status!.Data?["cad"]?["quiescent"]?.GetValue<bool>() ?? true
                ? "online" : "busy";
            return r;
        }

        var liveExisting = HostClient.Discover().Where(i => !i.Degraded).ToList();
        if (liveExisting.Count > 0)
            return Attached(liveExisting, "attached-existing");

        var existingAcad = HostClient.ListAcadProcesses();
        if (existingAcad.Count > 0)
        {

            var pids = existingAcad.Select(a => a.Pid).ToList();
            // 恢复等待期持续清扫弹窗（2026-09-07：degraded 期间"代理信息"弹窗曾堵死 Idle 泵）。
            // 每 ~2s 探测一次：宿主恢复应答即返回，不再固定等满 30s（弹窗清扫节奏保持 1s）
            for (int s = 0; s < 30; s++)
            {
                foreach (var p in pids) AcadModalDismiss.DismissAll((uint)p);
                if (s > 0 && s % 2 == 1)
                {
                    var live = HostClient.Discover().Where(i => !i.Degraded).ToList();
                    if (live.Count > 0)
                        return Attached(live, "attached-existing-recovered");
                }
                Thread.Sleep(1000);
            }
            var live2 = HostClient.Discover().Where(i => !i.Degraded).ToList();
            if (live2.Count > 0)
                return Attached(live2, "attached-existing-recovered");
            return new JsonObject
            {
                ["ok"] = true,
                ["decision"] = "launched-skipped",
                ["reason"] = "acad_already_running_no_host",
                ["ready"] = false,
                ["acad_pids"] = new JsonArray(pids.Select(p => JsonValue.Create(p)).ToArray()),
                ["cad_state"] = "starting",
                ["note"] = "acad process(es) already running but no responsive LoopHost pipe. "
                    + "Do NOT launch another acad (would create duplicate instances). "
                    + "Check: (1) LoopHost.dll auto-load (bundle/registry); (2) acad modals blocking Idle self-start; "
                    + "(3) legacy host without PID-pipe — restart acad to upgrade."
            };
        }

        var (acadExe, productArgs) = ResolveAcad();
        if (acadExe == null)
            return Err("ACAD_EXE_NOT_RESOLVED",
                "set env LOOP_ACAD_EXE / loop.config.json acad_exe, or install AutoCAD (registry probe failed)");

        var drawing = Repo.LastSeenDrawing
            ?? Environment.GetEnvironmentVariable("LOOP_DRAWING")
            ?? Repo.ConfigString("drawing");

        // 图纸不再上命令行（2026-09-07 实测教训）：冷启动即开图时 Plant 3D 环境
        // （PnPDwg2dAnno 等 ARX demand-load）尚未就绪，正交图 26 个代理对象弹
        // "代理信息"对话框风暴（dismissed=152 仍有残余堵死宿主 Idle 泵）。
        // 改为裸启 acad → 宿主就绪 → CadAttached 尾部 use_document 延后开图。
        var arguments = "/nossm";
        if (!string.IsNullOrWhiteSpace(productArgs)) arguments += " " + productArgs;
        int pid;
        try
        {
            // R27 D7：注入仓库根给子进程——热重载下业务 DLL 的 ResolveRepoRoot 拿不到
            // Assembly.Location，兜底路径又非必在；不注入时 TEST_T12_FIXTURE 等写盘命令
            // 在 6min 求解后才 ABORT（2026-09-14 实测）。UseShellExecute 子进程继承本进程
            // 环境块，故先设在引擎进程上再启动（值即本仓库根，常驻无害）。
            Environment.SetEnvironmentVariable("HRCAD_REPO_ROOT", Repo.Root);
            var psi = new System.Diagnostics.ProcessStartInfo(acadExe, arguments)
            {
                UseShellExecute = true
            };
            pid = System.Diagnostics.Process.Start(psi)?.Id ?? 0;
        }
        catch (Exception ex)
        {
            return Err("ACAD_LAUNCH_FAILED", ex.Message);
        }

        int waitMs = Math.Clamp(args["wait_ms"]?.GetValue<int>() ?? 60000, 0, 120000);
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        int dismissed = 0;
        DateTime lastProgress = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(750);

            dismissed += AcadModalDismiss.DismissAll((uint)pid);

            var live = HostClient.Discover().Where(i => !i.Degraded).ToList();
            if (live.Count > 0)
            {
                // 宿主就绪 ≠ P3D 环境收尾完成：留 2.5s 窗口并清扫残余弹窗，
                // 再由 CadAttached 尾部延后打开配置图纸
                Thread.Sleep(2500);
                dismissed += AcadModalDismiss.DismissAll((uint)pid);
                var attached = HostClient.SelectBest(live);
                var r = CadAttached(new JsonObject
                {
                    ["ok"] = true,
                    ["decision"] = "launched-new",
                    ["acad_pid"] = pid,
                    ["dismissed_modals"] = dismissed
                });
                r["ready"] = true;
                r["cad_state"] = attached.Status!.Data?["cad"]?["quiescent"]?.GetValue<bool>() ?? true
                    ? "online" : "busy";
                return r;
            }

            if ((DateTime.UtcNow - lastProgress).TotalSeconds >= 5)
            {
                lastProgress = DateTime.UtcNow;
                Program.Diag("attach", $"waiting acad pid={pid} (dismissed={dismissed}, elapsed={waitMs - (int)(deadline - DateTime.UtcNow).TotalMilliseconds}ms)");
            }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["decision"] = "launched-pending",
            ["ready"] = false,
            ["acad_pid"] = pid,
            ["dismissed_modals"] = dismissed,
            ["cad_state"] = "starting",
            ["waited_ms"] = waitMs,
            ["note"] = "acad launched but host.status not answering within wait_ms; "
                + "call start_or_attach_cad again to retry, or get_loop_status to inspect cad_state."
        };
    }

    private static JsonObject CadAttached(JsonObject res)
    {
        try
        {
            var status = HostClient.Call("host.status", null, 15000);
            var doc = status.Data?["cad"]?["doc"]?.GetValue<string>();
            if (doc != null)
            {
                res["doc"] = doc;
                return res;
            }
            res["doc"] = null;
            string? want = Repo.LastSeenDrawing
                ?? Environment.GetEnvironmentVariable("LOOP_DRAWING")
                ?? Repo.ConfigString("drawing");
            if (string.IsNullOrWhiteSpace(want))
            {
                res["doc_note"] = "cad online but on the Start tab (no document) and no drawing "
                    + "configured; open one via use_document";
                return res;
            }
            var open = HostClient.Call("use_document",
                new JsonObject { ["action"] = "open", ["path"] = want }, 55000);
            if (open.Ok)
            {
                res["doc"] = want;
                res["doc_opened"] = true;
            }
            else
            {
                res["doc_open_error"] = (open.ErrorCode ?? "HOST_ERROR") + ": " + (open.ErrorMessage ?? "");
            }
        }
        catch (Exception ex)
        {
            res["doc_probe_error"] = ex.GetType().Name + ": " + ex.Message;
        }
        return res;
    }

    private static (string? exe, string? productArgs) ResolveAcad()
    {
        var env = Environment.GetEnvironmentVariable("LOOP_ACAD_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return (env, Repo.ConfigString("acad_args"));

        var cfg = Repo.ConfigString("acad_exe");
        if (!string.IsNullOrWhiteSpace(cfg) && File.Exists(cfg))
            return (cfg, Repo.ConfigString("acad_args"));

        try
        {
            (string Exe, string Args)? plant3d = null;
            (string Exe, string Args)? fallback = null;

            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD");
            if (root == null) return (null, null);

            foreach (var release in root.GetSubKeyNames())
            {
                using var releaseKey = root.OpenSubKey(release);
                if (releaseKey == null) continue;
                foreach (var app in releaseKey.GetSubKeyNames())
                {
                    using var appKey = releaseKey.OpenSubKey(app);
                    var location = appKey?.GetValue("Location") as string;
                    if (string.IsNullOrWhiteSpace(location)) continue;
                    var candidate = Path.Combine(location, "acad.exe");
                    if (!File.Exists(candidate)) continue;

                    var productName = (appKey.GetValue("ProductName") as string) ?? "";
                    var lang = LangFromKeySuffix(app);
                    var args = productName.Contains("Plant 3D", StringComparison.OrdinalIgnoreCase)
                        ? "/product PLNT3D" + (lang != null ? " /language \"" + lang + "\"" : "")
                        : null;

                    if (args != null) plant3d ??= (candidate, args);
                    fallback ??= (candidate, Repo.ConfigString("acad_args"));
                }
            }
            return plant3d ?? fallback ?? (null, null);
        }
        catch {  }
        return (null, null);
    }

    private static string? LangFromKeySuffix(string keyName)
    {
        var idx = keyName.LastIndexOf(':');
        if (idx < 0 || idx == keyName.Length - 1) return null;
        return keyName[(idx + 1)..] switch
        {
            "804" => "zh-CN",
            "409" => "en-US",
            _ => null
        };
    }

    private static JsonObject Err(string code, string message) =>
        new() { ["ok"] = false, ["code"] = code, ["message"] = message };

    private static JsonObject BuildDiagnostic(string runId)
    {

        JsonObject? row = null;
        try
        {
            if (Ledger.TryFindIteration(runId, out var found))
                row = (JsonObject)found.DeepClone();
        }
        catch {  }

        var log = Ledger.LoadRunLog(runId, 200);

        if (row == null)
            return new JsonObject
            {
                ["ok"] = true,
                ["run_id"] = runId,
                ["found"] = false,
                ["note"] = "run_id not found in ledger (DONE) or status.json; 可能是 Engine 重启前的半程轮"
            };

        var topic = row["topic"]?.GetValue<string>();
        var verdict = row["verdict"]?.GetValue<string>();
        var phase = row["phase"]?.GetValue<string>();
        var code = row["code"]?.GetValue<string>();

        if (string.IsNullOrEmpty(topic) || row["hypothesis"] == null)
        {
            try
            {
                foreach (var path in Ledger.LedgerPaths())
                {
                    if (!System.IO.File.Exists(path)) continue;
                    foreach (var line in System.IO.File.ReadLines(path))
                    {
                        if (System.Text.Json.Nodes.JsonNode.Parse(line) is not JsonObject o) continue;
                        if (o["run_id"]?.GetValue<string>() != runId) continue;
                        if (string.IsNullOrEmpty(topic) && o["topic"] != null)
                            row["topic"] = o["topic"]?.DeepClone();
                        if (row["command"] == null && o["command"] != null)
                            row["command"] = o["command"]?.DeepClone();
                        if (row["hypothesis"] == null && o["hypothesis"] != null)
                            row["hypothesis"] = o["hypothesis"]?.DeepClone();
                        if (row["timings_ms"] == null && o["timings_ms"] != null)
                            row["timings_ms"] = o["timings_ms"]?.DeepClone();
                        break;
                    }
                }
                topic = row["topic"]?.GetValue<string>();
            }
            catch {  }
        }

        var (rootClass, confidence, summary, evidence) = ClassifyRootCause(verdict, phase, code, log);

        var history = BuildHistoryBlock(topic);

        var nextActions = BuildNextActions(rootClass, verdict, code, history, row);

        return new JsonObject
        {
            ["ok"] = true,
            ["run_id"] = runId,
            ["found"] = true,
            ["iteration"] = row,
            ["root_cause"] = new JsonObject
            {
                ["class"] = rootClass,
                ["confidence"] = confidence,
                ["summary"] = summary,
                ["evidence_lines"] = new JsonArray(evidence.Take(3).Select(l => (JsonNode)l).ToArray())
            },
            ["history"] = history,
            ["next_actions"] = new JsonArray(nextActions.Select(a => (JsonNode)a).ToArray()),
            ["logs"] = new JsonObject
            {
                ["tail"] = new JsonArray(log.TakeLast(20).Select(l => (JsonNode)l).ToArray()),
                ["count"] = log.Count
            }
        };
    }

    private static (string cls, double confidence, string summary, List<string> evidence) ClassifyRootCause(
        string? verdict, string? phase, string? code, List<string> log)
    {
        var evidence = new List<string>();

        if (!string.IsNullOrEmpty(code))
            foreach (var l in log)
                if (l.Contains(code, StringComparison.OrdinalIgnoreCase))
                { evidence.Add(l); if (evidence.Count >= 2) break; }

        return (verdict, phase, code) switch
        {
            ("FAIL", "ASSERT", "EXPECT_NOT_MET") => ("expect_mismatch", 0.9,
                $"expect.log_must_contain 至少一条 0 命中；code=EXPECT_NOT_MET 表明断言失败（非 CAD 离线）", evidence),
            ("FAIL", _, "BLACKLIST_HIT") => ("blacklist_hit", 0.95,
                "负面黑名单命中（eDuplicateKey/eLockViolation/MissingMethod/InvalidCast/Unhandled/FATAL）——代码层致命错误", evidence),
            ("ERROR", "ACT", "ACT_FAILED") => ("act_failed", 0.95,
                "act.run_command 被宿主拒绝；检查命令白名单 + CAD 状态", evidence),
            ("FAIL", _, "BUILD_FAILED") or ("ERROR", _, "BUILD_FAILED") => ("build_failed", 1.0,
                "build.ps1 编译失败；查看 build_errors / build_tail 字段", evidence),
            ("ERROR", _, "RELOAD_LEAK") or ("ERROR", _, "GUID_MISMATCH") or ("ERROR", _, "VERSION_MISMATCH") =>
                ("reload_failed", 0.95,
                "业务 DLL 热重载失败；RELOAD_LEAK=上一代资源未释放，GUID_MISMATCH=磁盘与预期不符——先排查再重跑", evidence),
            ("ERROR", "ACT", "ACT_TIMEOUT") => ("act_timeout", 0.9,
                "命令超过 act 后轮询上限仍未完成（宿主被长命令占线属正常，不是失联）：以业务日志人工判定，"
                + "或拆小命令；先查 acad 是否 Responding", evidence),
            ("ERROR", _, "CAD_NOT_ATTACHED") or ("ERROR", _, "HOST_UNREACHABLE") or ("ERROR", _, "HOST_PIPE_GONE") =>
                ("attach_error", 0.95,
                "CAD 离线 / 宿主管道不可达；调 attach_cad 重连，或查 acad 进程 / LoopHost.dll 加载", evidence),
            ("ERROR", "COLLECT", _) => ("host_unreachable", 0.7,
                $"COLLECT 阶段失败（phase=COLLECT）：可能是 host.tail_log 调用超时或日志汇溢出（dropped=true）", evidence),
            ("RUNNING", "REPORT", "WAIT_TIMEOUT") => ("wait_timeout", 1.0,
                "wait_ms 内未完成；用 get_iteration 轮询，或加大 wait_ms 重跑", evidence),
            (_, _, _) when !string.IsNullOrEmpty(code) => ("unknown", 0.3,
                $"未匹配规则的 code={code}（verdict={verdict} phase={phase}）；需要人工研判", evidence),
            _ => ("unknown", 0.2,
                "无 code 信息；需结合 evidence_lines 排查", evidence)
        };
    }

    private static JsonObject BuildHistoryBlock(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return new JsonObject { ["available"] = false };

        // 全量取同 topic 行（7 天过滤在下方做）——此前 limit=50 截尾会让
        // same_topic_count/pass_rate 在高频 topic 下变成"最近窗口"统计
        var rows = Ledger.ListIterations(topic, null, int.MaxValue);
        var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);
        var recent = new List<JsonObject>();
        foreach (var n in rows)
        {
            if (n is not JsonObject o) continue;

            if (o["verdict"]?.GetValue<string>() == "BUSY") continue;

            if (o["ts"] is JsonNode tsNode && DateTime.TryParse(tsNode.GetValue<string>(), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            {
                if (ts < sevenDaysAgo) continue;
            }
            recent.Add(o);
        }

        if (recent.Count == 0)
            return new JsonObject
            {
                ["available"] = true,
                ["topic"] = topic,
                ["same_topic_count"] = 0,
                ["pass_rate"] = 0,
                ["note"] = "最近 7 天同 topic 无历史；这是首次跑"
            };

        int pass = recent.Count(o => o["verdict"]?.GetValue<string>() == "PASS");
        int fail = recent.Count(o => o["verdict"]?.GetValue<string>() == "FAIL");
        int error = recent.Count(o => o["verdict"]?.GetValue<string>() == "ERROR");
        double passRate = recent.Count > 0 ? (double)pass / recent.Count : 0;

        var lastPass = recent.LastOrDefault(o => o["verdict"]?.GetValue<string>() == "PASS");

        var lastFive = recent.TakeLast(5).ToList();
        bool recentStuck = lastFive.Count == 5 && lastFive.All(o => o["verdict"]?.GetValue<string>() != "PASS");

        var hist = new JsonObject
        {
            ["available"] = true,
            ["topic"] = topic,
            ["same_topic_count"] = recent.Count,
            ["pass_rate"] = Math.Round(passRate, 2),
            ["verdict_breakdown"] = new JsonObject { ["PASS"] = pass, ["FAIL"] = fail, ["ERROR"] = error },
            ["recent_stuck"] = recentStuck,
            ["same_topic"] = new JsonArray(recent.TakeLast(10).Select(o => new JsonObject
            {
                ["run_id"] = o["run_id"]?.DeepClone(),
                ["ts"] = o["ts"]?.DeepClone(),
                ["verdict"] = o["verdict"]?.DeepClone(),
                ["code"] = o["code"]?.DeepClone(),
                ["hypothesis"] = o["hypothesis"]?.DeepClone()
            }).Select(n => (JsonNode)n).ToArray())
        };
        if (lastPass != null)
            hist["last_pass"] = new JsonObject
            {
                ["run_id"] = lastPass["run_id"]?.DeepClone(),
                ["ts"] = lastPass["ts"]?.DeepClone()
            };
        return hist;
    }

    private static List<JsonObject> BuildNextActions(string rootClass, string? verdict, string? code,
        JsonObject history, JsonObject row)
    {
        var actions = new List<JsonObject>();
        int p = 1;
        int sameCount = history["same_topic_count"]?.GetValue<int>() ?? 0;
        double passRate = history["pass_rate"]?.GetValue<double>() ?? 0;
        bool recentStuck = history["recent_stuck"]?.GetValue<bool>() ?? false;
        bool hypothesisMissing = row["hypothesis"] == null || row["hypothesis"]?.GetValue<string?>() is null or "";

        if (rootClass == "attach_error" && sameCount > 0 && passRate > 0)
            actions.Add(new JsonObject
            {
                ["priority"] = p++,
                ["action"] = "rerun_with_attach_first",
                ["reason"] = "attach 错误但同 topic 历史有 PASS；先调 attach_cad 等 ready:true 再 run_iteration"
            });

        if (rootClass == "expect_mismatch" && hypothesisMissing)
            actions.Add(new JsonObject
            {
                ["priority"] = p++,
                ["action"] = "rerun_with_hypothesis",
                ["reason"] = "断言失败但缺 hypothesis——按 repro-before-fix 纪律先写假设再跑"
            });

        if (recentStuck)
            actions.Add(new JsonObject
            {
                ["priority"] = p++,
                ["action"] = "topic_stuck_review",
                ["reason"] = "同 topic 最近 5 次全 FAIL——可能假设/测试设计有问题，建议重审 hypothesis 或拆 fixture"
            });

        if (rootClass == "blacklist_hit")
            actions.Add(new JsonObject
            {
                ["priority"] = p++,
                ["action"] = "fix_code_then_rebuild",
                ["reason"] = "黑名单命中 = 致命代码问题（eDuplicateKey/eLockViolation/MissingMethod 等）。先改代码 → build.ps1 → reload_plugins → 再 run_iteration"
            });

        if (rootClass == "build_failed")
            actions.Add(new JsonObject
            {
                ["priority"] = p++,
                ["action"] = "fix_compile_errors",
                ["reason"] = "build.ps1 报错——查 evidence_lines / build_errors / build_tail 字段定位 file:line"
            });

        if (rootClass == "reload_failed")
            actions.Add(new JsonObject
            {
                ["priority"] = p++,
                ["action"] = "rerun_reload_or_restart",
                ["reason"] = "RELOAD_LEAK=上一代资源未释放；guid/version mismatch=磁盘与预期不符；先 attach_reload 再 run_iteration，仍败则 restart acad"
            });

        if (rootClass == "wait_timeout")
            actions.Add(new JsonObject
            {
                ["priority"] = p++,
                ["action"] = "poll_or_extend_wait",
                ["reason"] = "用 get_iteration{run_id} 轮询终态；或加大 wait_ms 重跑"
            });

        if (verdict == "ERROR" && rootClass != "attach_error")
            actions.Add(new JsonObject
            {
                ["priority"] = p++,
                ["action"] = "rerun_same_params",
                ["reason"] = "ERROR 且非 attach 错误——可能是单次瞬态；同参重跑一次确认是否稳定"
            });

        if (actions.Count == 0)
            actions.Add(new JsonObject
            {
                ["priority"] = 1,
                ["action"] = "inspect_evidence",
                ["reason"] = "无明确规则匹配——看 root_cause.evidence_lines + get_iteration{run_id, section:'log'} 全量日志"
            });

        return actions;
    }
}

