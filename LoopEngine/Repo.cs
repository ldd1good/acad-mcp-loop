namespace LoopEngine;

public static class Repo
{

    public static string Root { get; } = FindRoot();

    public static string DataDir => Path.Combine(Root, ".debug");
    public static string LedgerFile => Path.Combine(DataDir, "loop", "iterations.jsonl");
    public static string RunsDir => Path.Combine(DataDir, "runs");

    public static string BusinessDll =>
        ConfigString("paths:business_dll") ?? Path.Combine(Root, "bin", "x64", "Debug", "HotReloadCAD.dll");

    public static string? HostDll => ConfigString("paths:host_dll");

    // 构建脚本：loop.config.json build_script（相对路径按仓库根解析），缺省根下 build.ps1
    public static string BuildScript =>
        ConfigString("build_script") is { } script
            ? (Path.IsPathRooted(script) ? script : Path.Combine(Root, script))
            : Path.Combine(Root, "build.ps1");

    // 业务插件磁盘日志文件名前缀：<dir>\<prefix>yyyyMMdd.json（dir 见 disk_log_dir）
    public static string DiskLogFilePrefix => ConfigString("disk_log_file_prefix") ?? "HotReloadCAD_";

    public static string? LastSeenDrawing => _lastSeen ??= ReadLastSeenDrawingFile();
    private static string? _lastSeen;

    private static string LastSeenDrawingFile => Path.Combine(DataDir, "last-drawing.txt");

    public static void RememberDrawing(string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc)) return;
        _lastSeen = doc;
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(LastSeenDrawingFile, doc);
        }
        catch {  }
    }

    private static string? ReadLastSeenDrawingFile()
    {
        try
        {
            return File.Exists(LastSeenDrawingFile)
                ? File.ReadAllText(LastSeenDrawingFile).Trim()
                : null;
        }
        catch { return null; }
    }

    private static long? _clientTimeoutMs;

    public static long ClientTimeoutMs
    {
        get
        {
            if (!_clientTimeoutMs.HasValue)
            {
                long parsed = 0;
                try
                {

                    var env = Environment.GetEnvironmentVariable("LOOP_CLIENT_TIMEOUT_MS");
                    if (!string.IsNullOrWhiteSpace(env) && long.TryParse(env, out var envMs) && envMs > 0)
                    {
                        parsed = envMs;
                    }
                    else
                    {
                        var file = Path.Combine(Root, ".zcode", "config.json");
                        if (File.Exists(file) &&
                            System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) is System.Text.Json.Nodes.JsonObject root &&
                            root["mcp"]?["servers"]?["plant3d-loop"]?["timeoutMs"] is System.Text.Json.Nodes.JsonNode t)
                            parsed = t.GetValue<long>();
                    }
                }
                catch { }
                _clientTimeoutMs = parsed;
            }
            return _clientTimeoutMs.Value;
        }
    }

    public static int MaxWaitMs
    {
        get
        {
            var t = ClientTimeoutMs;
            if (t <= 0) return 280_000;
            return (int)Math.Clamp(t - 5_000, 10_000, 280_000);
        }
    }

    public static string BuildGuidSidecar =>
        Path.Combine(Path.GetDirectoryName(BusinessDll) ?? ".", Path.GetFileName(BusinessDll) + ".buildguid");

    public static string? ReadDiskBuildGuid()
    {
        try
        {
            var f = BuildGuidSidecar;
            if (!File.Exists(f)) return null;
            var s = File.ReadAllText(f).Trim();
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    private static System.Text.Json.Nodes.JsonObject? _config;
    private static bool _configLoaded;

    private static System.Text.Json.Nodes.JsonObject? Config
    {
        get
        {
            if (_configLoaded) return _config;
            _configLoaded = true;
            try
            {
                var file = Path.Combine(AppContext.BaseDirectory, "loop.config.json");
                if (File.Exists(file))
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) is System.Text.Json.Nodes.JsonObject o)
                        _config = o;
                }
            }
            catch {  }
            return _config;
        }
    }

    public static string? ConfigString(string key)
    {
        var node = (System.Text.Json.Nodes.JsonNode?)Config;
        foreach (var seg in key.Split(':'))
        {
            if (node is not System.Text.Json.Nodes.JsonObject o) return null;
            node = o[seg];
        }
        return node?.GetValue<string>();
    }

    private static string FindRoot()
    {

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = Path.GetFullPath(start);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "build.ps1"))) return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
        }
        return Directory.GetCurrentDirectory();
    }
}

