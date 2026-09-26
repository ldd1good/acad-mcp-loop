using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LoopHost
{

    public sealed class ReloadOrchestrator
    {
        public enum HostState { Idle, Ready, Reloading, Degraded }

        private static readonly ReloadOrchestrator _instance = new ReloadOrchestrator();
        public static ReloadOrchestrator Instance { get { return _instance; } }

        private volatile HostState _state = HostState.Idle;

        // 业务插件身份走 loop.config.json（LoopHost.dll 旁），未配置时回落内置约定值
        public static readonly string RegistrationTypeFullName =
            HostConfig.Get("business_registration_type", "HotReloadCAD.Agent.LoopToolRegistration");
        public static readonly string BusinessAssemblyName =
            HostConfig.Get("business_assembly", "HotReloadCAD");

        private AssemblyDependent.LoadDllMessage[] _lastLoad;
        private long _prepareMs;
        private string _lastDllPath;

        private ReloadOrchestrator() { }

        public string StateString
        {
            get { return _state.ToString(); }
        }

        public string LoadedBusinessVersion
        {
            get
            {
                try
                {
                    var asm = LastBusinessAssembly();
                    return asm == null ? null : asm.GetName().Version.ToString();
                }
                catch { return null; }
            }
        }

        public string LoadedBusinessGuid
        {
            get
            {
                try { return ReadBuildGuid(LastBusinessAssembly()); }
                catch { return null; }
            }
        }

        public MainThreadResult PrepareBytes(string dllPath, string expectedVersion, string expectedGuid)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
                    return MainThreadResult.Failure("DLL_NOT_FOUND", "dll not found: " + dllPath, sw.ElapsedMilliseconds);
                _lastDllPath = dllPath;

                var diskAssembly = AssemblyName.GetAssemblyName(dllPath);
                if (!string.IsNullOrEmpty(expectedVersion))
                {
                    Version expected;
                    try { expected = new Version(expectedVersion); }
                    catch { return MainThreadResult.Failure("BAD_VERSION", "expected_version unparsable: " + expectedVersion, sw.ElapsedMilliseconds); }
                    var d = diskAssembly.Version;
                    if (d.Major != expected.Major || d.Minor != expected.Minor || d.Build != expected.Build)
                        return MainThreadResult.Failure("VERSION_MISMATCH",
                            "disk=" + d + " expected=" + expectedVersion, sw.ElapsedMilliseconds);
                }

                if (!string.IsNullOrEmpty(expectedGuid))
                {
                    var sidecar = dllPath + ".buildguid";
                    if (File.Exists(sidecar))
                    {
                        var diskGuid = File.ReadAllText(sidecar).Trim();
                        if (diskGuid.Length > 0 && !string.Equals(diskGuid, expectedGuid, StringComparison.Ordinal))
                            return MainThreadResult.Failure("GUID_MISMATCH",
                                "disk buildguid=" + diskGuid + " expected=" + expectedGuid, sw.ElapsedMilliseconds);
                    }
                }

                _state = HostState.Reloading;
                RingLogSink.Log("reload", "prepare: " + dllPath + " disk=" + diskAssembly.Version
                    + " old=" + (LoadedBusinessVersion ?? "none"));

                var loader = new AssemblyDependent(dllPath);
                _lastLoad = loader.Load();
                _prepareMs = sw.ElapsedMilliseconds;

                int loadedCount = _lastLoad.Count(m => m.LoadYes);
                int skippedCount = _lastLoad.Count(m => m.Skipped);
                RingLogSink.Log("reload", "bytes ready: loaded=" + loadedCount + " skipped=" + skippedCount
                    + " ms=" + _prepareMs);
                return MainThreadResult.Success(null, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _state = HostState.Degraded;
                return MainThreadResult.Failure("RELOAD_FAILED", ex.GetType().Name + ": " + ex.Message, sw.ElapsedMilliseconds);
            }
        }

        internal static void AbandonReload()
        {
            var inst = Instance;
            if (inst._state != HostState.Reloading) return;
            inst._state = LastBusinessAssembly() != null ? HostState.Ready : HostState.Idle;
            RingLogSink.Log("reload", "register cancelled — state rolled back (no teardown/register ran)");
        }

        /// <summary>陈旧业务 DLL 自动重载（2026-09-19，P0-2）。动机：本日两次 run_command
        /// "验证"跑在旧 DLL 上（宿主 loaded=2400，磁盘已 2402），性能修复被误判无效。
        /// 比对磁盘 sidecar（*.buildguid）vs 内存 AssemblyMetadata.BuildGuid，失配走完整
        /// reload 管线（注册仍过主线程泵）。返回 true=发生重载。失败不抛——调用方
        /// （act.run_command_async 受理前）只记日志不阻塞命令。</summary>
        public bool AutoReloadIfStale(out string detail)
        {
            detail = null;
            try
            {
                var dllPath = ResolveBusinessDllPath();
                if (dllPath == null) { detail = "dll path unresolved (no last reload path; repo probe failed)"; return false; }
                var sidecar = dllPath + ".buildguid";
                if (!File.Exists(sidecar)) { detail = "no sidecar: " + sidecar; return false; }
                var diskGuid = File.ReadAllText(sidecar).Trim();
                if (diskGuid.Length == 0) { detail = "sidecar empty"; return false; }
                var memGuid = Instance.LoadedBusinessGuid;
                if (string.Equals(memGuid, diskGuid, StringComparison.OrdinalIgnoreCase))
                {
                    detail = "fresh " + LoadedBusinessVersion + " guid=" + Short(diskGuid);
                    return false;
                }
                string diskVersion = null;
                try { diskVersion = System.Reflection.AssemblyName.GetAssemblyName(dllPath).Version.ToString(); }
                catch { }
                RingLogSink.Log("stale-guard", "STALE loaded=" + (memGuid ?? "none") + "@" + (LoadedBusinessVersion ?? "?")
                    + " disk=" + Short(diskGuid) + "@" + (diskVersion ?? "?") + " → auto reload");
                var prep = PrepareBytes(dllPath, diskVersion, diskGuid);
                if (prep == null || !prep.Ok)
                {
                    detail = "prepare failed: " + (prep != null ? prep.ErrorCode + " " + prep.ErrorMessage : "null");
                    return false;
                }
                var reg = MainThreadPump.Instance.Execute("stale-guard.register",
                    delegate { return RegisterReloaded(dllPath, diskVersion, diskGuid); },
                    55000, 15000, -1, false, delegate { });
                bool ok = reg != null && reg.Ok;
                detail = (ok ? "AUTO RELOADED " : "reload failed(" + (reg != null ? reg.ErrorCode : "null") + ") ")
                    + LoadedBusinessVersion + " guid=" + Short(diskGuid);
                return ok;
            }
            catch (Exception ex)
            {
                detail = "stale-guard error: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static string Short(string g)
        {
            return string.IsNullOrEmpty(g) ? "-" : g.Substring(0, Math.Min(8, g.Length));
        }

        /// <summary>业务 DLL 路径解析：上次 reload 的路径 → 仓库相对探测
        ///（LoopHost.dll 位于 &lt;repo&gt;/LoopHost/bin/&lt;cfg&gt;/，业务在 &lt;repo&gt;/bin/x64/&lt;cfg&gt;/）。</summary>
        private string ResolveBusinessDllPath()
        {
            if (!string.IsNullOrEmpty(_lastDllPath) && File.Exists(_lastDllPath)) return _lastDllPath;
            try
            {
                string hostDir = null;
                try { hostDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
                catch { }
                if (string.IsNullOrEmpty(hostDir)) hostDir = AppDomain.CurrentDomain.BaseDirectory;
                // hostDir = <repo>/LoopHost/bin/<cfg> → cfg dir → LoopHost dir → repo
                var loopHostDir = Path.GetDirectoryName(Path.GetDirectoryName(hostDir));
                if (loopHostDir != null)
                {
                    var repo = Path.GetDirectoryName(loopHostDir);
                    if (repo != null)
                    {
                        foreach (var cfg in new[] { "Debug", "Release" })
                        {
                            var cand = Path.Combine(repo, "bin", "x64", cfg, BusinessAssemblyName + ".dll");
                            if (File.Exists(cand)) return cand;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public object RegisterReloaded(string dllPath, string expectedVersion, string expectedGuid)
        {
            var sw = Stopwatch.StartNew();
            string oldVersion = LoadedBusinessVersion;
            // 账目加固（2026-09-19 P2-7）：before/after guid 显式入账——此前仅版本号对比曾让
            // agent 分不清"reload 真发生了"还是"skipped 明细误导"（old 显示已是新版的疑云）。
            string beforeGuid = null;
            try { beforeGuid = ReadBuildGuid(LastBusinessAssembly()); } catch { }
            try
            {

                bool teardownOk = ToolRegistry.Instance.RunTeardownSafely();

                ToolRegistry.Instance.ResetBusinessTools();

                var regType = FindRegistrationType();
                if (regType == null)
                {
                    _state = HostState.Degraded;
                    return Fail("REG_NOT_FOUND",
                        RegistrationTypeFullName + " not found after load", oldVersion, sw);
                }

                InvokeBusinessRegister(regType);

                string newVersion = LoadedBusinessVersion;
                if (!string.IsNullOrEmpty(expectedVersion))
                {
                    var expectedV = new Version(expectedVersion);
                    var memV = Version.Parse(newVersion ?? "0.0.0");
                    if (memV.Major != expectedV.Major || memV.Minor != expectedV.Minor || memV.Build != expectedV.Build)
                    {
                        _state = HostState.Degraded;
                        return Fail("VERSION_MISMATCH",
                            "memory=" + newVersion + " expected=" + expectedVersion, oldVersion, sw);
                    }
                }

                string memGuid = ReadBuildGuid(LastBusinessAssembly());
                bool guidAsserted = false;
                if (!string.IsNullOrEmpty(expectedGuid) && !string.IsNullOrEmpty(memGuid))
                {
                    guidAsserted = true;
                    if (!string.Equals(memGuid, expectedGuid, StringComparison.Ordinal))
                    {
                        _state = HostState.Degraded;
                        return Fail("GUID_MISMATCH",
                            "memory guid=" + memGuid + " expected=" + expectedGuid, oldVersion, sw);
                    }
                }

                var leaks = GenerationLedger.LeakSnapshot(memGuid ?? "?", !teardownOk);
                int epoch = ToolRegistry.Instance.AdvanceEpoch();
                _state = leaks.Count > 0 ? HostState.Degraded : HostState.Ready;

                var tools = ToolRegistry.Instance.Names();
                tools.Sort(StringComparer.Ordinal);

                int loadedCount = _lastLoad != null ? _lastLoad.Count(m => m.LoadYes) : 0;
                int skippedCount = _lastLoad != null ? _lastLoad.Count(m => m.Skipped) : 0;

                RingLogSink.Log("reload", (leaks.Count > 0 ? "LEAK " : "done: ") + "epoch=" + epoch
                    + " version=" + newVersion + " guid=" + (memGuid ?? "-")
                    + " loaded=" + loadedCount + " skipped=" + skippedCount
                    + " tools=" + tools.Count + " leaks=" + leaks.Count);

                var result = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "ok", leaks.Count == 0 },
                    { "old", oldVersion },
                    { "new", newVersion },
                    { "before_guid", beforeGuid },
                    { "build_guid", memGuid },
                    { "disk_guid", expectedGuid },
                    { "guid_asserted", guidAsserted },
                    { "epoch", epoch },
                    { "tools", tools },
                    { "loaded_files", loadedCount },
                    { "skipped_files", skippedCount },
                    { "skipped_detail", SkippedDetail() },
                    { "leaks", leaks },
                    { "state", _state.ToString() },
                    { "ms", sw.ElapsedMilliseconds + _prepareMs }
                };
                if (leaks.Count > 0)
                {
                    result["code"] = "RELOAD_LEAK";
                    result["message"] = leaks.Count + " live slot(s) not owned by new generation — see leaks";
                }
                return result;
            }
            catch (Exception ex)
            {
                _state = HostState.Degraded;
                return Fail("RELOAD_FAILED", ex.GetType().Name + ": " + ex.Message, oldVersion, sw);
            }
        }

        private static System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> SkippedDetail()
        {
            var detail = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
            if (_instance._lastLoad == null) return detail;
            foreach (var m in _instance._lastLoad)
            {
                if (!m.Skipped && m.LoadYes) continue;
                if (!m.Skipped && !m.LoadYes)
                {
                    detail.Add(new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "path", m.Path }, { "skipped", false }, { "loaded", false },
                        { "reason", "load_failed" }
                    });
                    continue;
                }
                detail.Add(new System.Collections.Generic.Dictionary<string, object>
                {
                    { "path", m.Path }, { "skipped", true }, { "reason", m.Reason ?? "unspecified" }
                });
            }
            return detail;
        }

        private static System.Collections.Generic.Dictionary<string, object> Fail(
            string code, string message, string oldVersion, Stopwatch sw)
        {
            RingLogSink.Log("reload", "FAILED " + code + ": " + message);
            return new System.Collections.Generic.Dictionary<string, object>
            {
                { "ok", false },
                { "code", code },
                { "message", message },
                { "old", oldVersion },
                { "state", _instance._state.ToString() },
                { "ms", sw.ElapsedMilliseconds + _instance._prepareMs }
            };
        }

        internal static void InvokeBusinessRegister(Type regType)
        {
            Action<string, Func<string, string>> register =
                (name, fn) => ToolRegistry.Instance.Register(name, fn);
            Action<Action> setTeardown =
                teardown => ToolRegistry.Instance.SetTeardown(teardown);
            Func<Action<string>> attachLogSink =
                () => line => RingLogSink.Instance.Append(line);

            var mi5 = regType.GetMethod("Register", new Type[]
            {
                typeof(Action<string, Func<string, string>>),
                typeof(Action<Action>),

                typeof(Func<Action<string>>),
                typeof(Action<string, string>),
                typeof(Action<string>)
            });
            if (mi5 != null)
            {
                mi5.Invoke(null, new object[]
                {
                    register, setTeardown, attachLogSink,
                    new Action<string, string>(GenerationLedger.Claim),
                    new Action<string>(GenerationLedger.Release)
                });
                RingLogSink.Log("reload", "registered via 5-arg signature (generation claims enabled)");
                return;
            }

            regType.InvokeMember(
                "Register",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod,
                null, null,
                new object[] { register, setTeardown, attachLogSink });
            RingLogSink.Log("reload", "registered via legacy 3-arg signature (no generation claims)");
        }

        private static string ReadBuildGuid(Assembly asm)
        {
            if (asm == null) return null;
            try
            {
                foreach (var a in asm.GetCustomAttributesData())
                {
                    if (a.AttributeType == null || a.AttributeType.Name != "AssemblyMetadataAttribute") continue;
                    if (a.ConstructorArguments.Count != 2) continue;
                    if ((a.ConstructorArguments[0].Value as string) == "BuildGuid")
                        return a.ConstructorArguments[1].Value as string;
                }
            }
            catch { }
            return null;
        }

        private static Assembly LastBusinessAssembly()
        {
            Assembly found = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == BusinessAssemblyName) found = a;
            }
            return found;
        }

        private static Type FindRegistrationType()
        {
            Type found = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != BusinessAssemblyName) continue;
                try
                {
                    var t = a.GetType(RegistrationTypeFullName, false);
                    if (t != null) found = t;
                }
                catch { }
            }
            return found;
        }
    }
}

