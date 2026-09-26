using System.Text;

namespace LoopEngine;

public static class Program
{
    private const string ProtocolVersion = "2024-11-05";

    public static int Main(string[] args)
    {

        if (args.Contains("--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0");
            return 0;
        }

        Console.OutputEncoding = Encoding.UTF8;
        IsolateStdoutAndHookCrashes();

        Serve();
        return 0;
    }

    private static string? _crashLogPath;

    internal static void Diag(string kind, string message)
    {
        try
        {
            if (_crashLogPath == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_crashLogPath)!);
            File.AppendAllText(_crashLogPath!,
                $"{DateTime.UtcNow:O}\t{kind}\t{message}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch { }
    }

    private static void LogCrash(string kind, string message) => Diag(kind, message);

    internal static void StderrNote(string message)
    {
        try { Console.Error.WriteLine($"[loop-engine pid={Environment.ProcessId}] {message}"); }
        catch {  }
    }

    private static void IsolateStdoutAndHookCrashes()
    {
        try { Console.SetOut(TextWriter.Null); } catch { }

        _crashLogPath = Path.Combine(Repo.DataDir, "loop", $"engine-{Environment.ProcessId}.log");
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {

            StderrNote("UnhandledException: " + (e.ExceptionObject?.ToString() ?? "<null>"));
            LogCrash("UnhandledException", e.ExceptionObject?.ToString() ?? "<null>");
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash("UnobservedTaskException", e.Exception?.ToString() ?? "<null>");
            e.SetObserved();
        };
    }

    private static void Serve()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin, new UTF8Encoding(false));
        using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
        var writeLock = new object();

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;

            var current = line;
            Task.Run(() =>
            {
                string? response = null;
                try
                {
                    response = Dispatch(current);
                }
                catch (Exception ex)
                {
                    response = JsonRpcError(0, -32603, "internal: " + ex.GetType().Name + ": " + ex.Message);
                }
                if (response != null)
                    lock (writeLock) writer.WriteLine(response);
            });
        }

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (Iteration.ActiveRunSnapshot() != null && DateTime.UtcNow < deadline)
                Thread.Sleep(200);
            var active = Iteration.ActiveRunSnapshot();
            if (active != null)
            {
                StderrNote("exiting while an iteration is still active (run will be marked abandoned): "
                    + active.ToJsonString());
                LogCrash("ExitWithActiveRun", active.ToJsonString());
            }
        }
        catch { }
    }

    private static string? Dispatch(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch { return JsonRpcError(0, -32700, "parse error"); }
        if (node is not JsonObject req) return JsonRpcError(0, -32600, "invalid request");

        JsonNode? id = req["id"]?.DeepClone();
        string method = req["method"]?.GetValue<string>() ?? "";

        switch (method)
        {
            case "initialize":
                return JsonRpcResult(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false }
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "plant3d-loop",
                        ["version"] = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0"
                    }
                });

            case "notifications/initialized":
            case "notifications/cancelled":
                return null;

            case "ping":
                return JsonRpcResult(id, new JsonObject());

            case "tools/list":
            {
                var tools = new JsonArray();
                foreach (var (name, desc, schema) in ToolSurface.Tools)
                {
                    tools.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["description"] = desc,
                        ["inputSchema"] = JsonNode.Parse(schema)
                    });
                }
                return JsonRpcResult(id, new JsonObject { ["tools"] = tools });
            }

            case "tools/call":
            {
                var p = req["params"] as JsonObject ?? new JsonObject();
                string name = p["name"]?.GetValue<string>() ?? "";
                var toolArgs = p["arguments"] as JsonObject ?? new JsonObject();

                JsonObject result;
                try
                {
                    result = ToolSurface.Call(name, toolArgs) ?? ToolSurfaceUnknown(name);
                }
                catch (Exception ex)
                {
                    result = new JsonObject
                    {
                        ["ok"] = false,
                        ["code"] = "ENGINE_INTERNAL",
                        ["message"] = ex.GetType().Name + ": " + ex.Message
                    };
                }

                return JsonRpcResult(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = result.ToJsonString()
                    })
                });
            }

            default:
                return JsonRpcError(id, -32601, "method not found: " + method);
        }
    }

    private static JsonObject ToolSurfaceUnknown(string name) =>
        new() { ["ok"] = false, ["code"] = "UNKNOWN_TOOL", ["message"] = name };

    private static string JsonRpcResult(JsonNode? id, JsonObject result) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone() ?? 0,
            ["result"] = result
        }.ToJsonString();

    private static string JsonRpcError(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone() ?? 0,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        }.ToJsonString();
}

