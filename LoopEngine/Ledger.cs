using System.Text;

namespace LoopEngine;

public static class Ledger
{
    private const long RotateBytes = 5 * 1024 * 1024;
    private static readonly object Lock = new();

    private const string Base32 = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string SanitizeRunId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder();
        foreach (var c in raw.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            if (sb.Length >= 64) break;
        }
        return sb.ToString();
    }

    public static string MintRunId(string topic)
    {
        topic = SanitizeRunId(topic);
        if (topic.Length == 0) topic = "adhoc";
        var suffix = new string(Enumerable.Range(0, 4)
            .Select(_ => Base32[Random.Shared.Next(Base32.Length)]).ToArray());
        return $"R-{DateTime.Now:yyyyMMdd-HHmmss}-{topic}-{suffix}";
    }

    public static void AppendIteration(JsonObject entry)
    {
        var path = Repo.LedgerFile;
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > RotateBytes)
                {
                    var backup = Path.Combine(Path.GetDirectoryName(path)!, "iterations.backup.jsonl");
                    File.Delete(backup);
                    File.Move(path, backup);
                }
            }
            catch {  }

            for (int i = 0; ; i++)
            {
                try
                {
                    File.AppendAllText(path, entry.ToJsonString() + Environment.NewLine, new UTF8Encoding(false));
                    break;
                }
                catch (IOException) when (i < 4)
                {
                    Thread.Sleep(250);
                }
            }
        }
    }

    public static JsonArray ListIterations(string? topic, string? verdict, int limit)
    {
        var rows = new List<JsonObject>();
        foreach (var path in LedgerPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    if (JsonNode.Parse(line) is not JsonObject o) continue;
                    if (!string.IsNullOrEmpty(topic) &&
                        !string.Equals(o["topic"]?.GetValue<string>(), topic, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(verdict) &&
                        !string.Equals(o["verdict"]?.GetValue<string>(), verdict, StringComparison.OrdinalIgnoreCase))
                        continue;
                    rows.Add(o);
                }
            }
            catch { }
        }
        rows.Sort((a, b) => string.Compare(
            a["ts"]?.GetValue<string>(), b["ts"]?.GetValue<string>(), StringComparison.Ordinal));

        int take = Math.Max(1, limit);
        int skip = Math.Max(0, rows.Count - take);
        return new JsonArray(rows.Skip(skip).Take(take).Select(o => (JsonNode)o.DeepClone()).ToArray());
    }

    public static IEnumerable<string> LedgerPaths()
    {
        yield return Repo.LedgerFile;
        yield return Path.Combine(Path.GetDirectoryName(Repo.LedgerFile)!,
            Path.GetFileNameWithoutExtension(Repo.LedgerFile) + ".backup.jsonl");
    }

    public static bool TryFindIteration(string runId, out JsonObject row)
    {
        row = new JsonObject();

        var stage = TryLoadStage(runId);
        if (stage != null && stage["stage"]?.GetValue<string>() == "DONE")
        {
            row = new JsonObject
            {
                ["run_id"] = runId,
                ["ts"] = stage["ts"]?.DeepClone() ?? "",
                ["stage"] = "DONE",
                ["verdict"] = stage["verdict"]?.DeepClone() ?? "UNKNOWN",
                ["phase"] = stage["phase"]?.DeepClone() ?? "REPORT",
                ["code"] = stage["code"]?.DeepClone() ?? "?",
                ["source"] = "status.json"
            };
            return true;
        }

        foreach (var path in LedgerPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var lines = File.ReadAllLines(path);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (lines[i].Length == 0) continue;
                    if (JsonNode.Parse(lines[i]) is not JsonObject o) continue;
                    if (string.Equals(o["run_id"]?.GetValue<string>(), runId, StringComparison.Ordinal))
                    {
                        row = o;
                        return true;
                    }
                }
            }
            catch { }
        }
        return false;
    }

    public static void SaveRunLog(string runId, IEnumerable<string> lines)
    {
        try
        {
            var dir = Path.Combine(Repo.RunsDir, runId);
            Directory.CreateDirectory(dir);
            File.AppendAllLines(Path.Combine(dir, "log.jsonl"), lines, new UTF8Encoding(false));
        }
        catch { }
    }

    public static List<string> LoadRunLog(string runId, int limit)
        => LoadRunLog(runId, limit, null, null, false);

    // P3-1：读全文件 → 过滤 → 取末 limit 行。旧版读"前 limit 行"是头部截断缺陷
    // （判决行总在尾部）；默认排除宿主噪声行（"host":"xxx"），contains/level 可再过滤。
    public static List<string> LoadRunLog(string runId, int limit,
        string? contains, string? level, bool includeHostNoise)
    {
        var lines = new List<string>();
        try
        {
            var path = Path.Combine(Repo.RunsDir, runId, "log.jsonl");
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length == 0) continue;
                    if (!includeHostNoise && Iteration.IsHostNoiseLine(line)) continue;
                    if (contains != null
                        && line.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (level != null && !LineMatchesLevel(line, level)) continue;
                    lines.Add(line);
                }
            }
        }
        catch { }
        return lines.Count > limit ? lines.GetRange(lines.Count - limit, limit) : lines;
    }

    private static bool LineMatchesLevel(string line, string level)
    {
        const string needle = "\"Level\":\"";
        int idx = line.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) return true;
        int start = idx + needle.Length;
        int end = line.IndexOf('"', start);
        if (end < 0) return true;
        return string.Equals(line.Substring(start, end - start), level, StringComparison.OrdinalIgnoreCase);
    }

    // P3-1：运行中迭代的实时日志尾——直接读磁盘业务日志按 run_id 过滤（log.jsonl 要到
    // COLLECT 结束才落盘）。不碰宿主管道，不打断进行中迭代的 host gate。
    public static List<string> LiveTailFromDisk(string runId, int count)
    {
        var result = new List<string>();
        try
        {
            var dir = Repo.ConfigString("disk_log_dir") ?? @"C:\Logs\Plant3D";
            foreach (var date in new[] { DateTime.Now, DateTime.Now.AddDays(-1) })
            {
                var path = Path.Combine(dir, Repo.DiskLogFilePrefix + date.ToString("yyyyMMdd") + ".json");
                if (!File.Exists(path)) continue;
                var tail = new Queue<string>(count);
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length == 0 || Iteration.IsHostNoiseLine(line)) continue;
                    if (line.IndexOf("\"run_id\":\"" + runId + "\"", StringComparison.Ordinal) < 0) continue;
                    tail.Enqueue(line);
                    while (tail.Count > count) tail.Dequeue();
                }
                result.AddRange(tail);
            }
        }
        catch { }
        if (result.Count > count) result = result.GetRange(result.Count - count, count);
        return result;
    }

    public static void SaveStage(string runId, string stage,
        string? verdict = null, string? phase = null, string? code = null)
    {
        try
        {
            var dir = Path.Combine(Repo.RunsDir, runId);
            Directory.CreateDirectory(dir);
            var o = new JsonObject
            {
                ["run_id"] = runId,
                ["stage"] = stage,
                ["engine_pid"] = Environment.ProcessId,
                ["ts"] = DateTime.UtcNow.ToString("o")
            };
            if (verdict != null) o["verdict"] = verdict;
            if (phase != null) o["phase"] = phase;
            if (code != null) o["code"] = code;

            // 原子写（temp+rename）+ 重试：status.json 是 run_id 幂等回放的依据，
            // 半截文件/写失败会让幂等键失效导致同 run 双跑；写失败保留旧文件也比损坏强
            var path = Path.Combine(dir, "status.json");
            var tmp = path + ".tmp";
            bool written = false;
            for (int i = 0; ; i++)
            {
                try
                {
                    File.WriteAllText(tmp, o.ToJsonString(), new UTF8Encoding(false));
                    File.Move(tmp, path, overwrite: true);
                    written = true;
                    break;
                }
                catch (IOException) when (i < 4)
                {
                    Thread.Sleep(250);
                }
                catch (Exception ex)
                {
                    Program.Diag("stage", $"{runId} -> {stage} status.json write FAILED: {ex.Message}");
                    break;
                }
            }

            if (written)
                Program.Diag("stage", $"{runId} -> {stage}" + (verdict != null ? $" ({verdict})" : ""));
        }
        catch {  }
    }

    public static JsonObject? TryLoadStage(string runId)
    {
        try
        {
            var path = Path.Combine(Repo.RunsDir, runId, "status.json");
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch { return null; }
    }
}

