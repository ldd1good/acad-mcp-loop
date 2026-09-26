namespace LoopEngine;

public static class TierZero
{
    public static JsonObject Make(
        string runId, string verdict, string phase, string code,
        JsonObject? timings, IEnumerable<string>? evidence, string? next,
        JsonObject? extra = null)
    {
        var t0 = new JsonObject
        {
            ["run_id"] = runId,
            ["verdict"] = verdict,
            ["phase"] = phase,
            ["code"] = code
        };
        if (timings != null) t0["timings_ms"] = timings;
        if (evidence != null)
        {
            var arr = new JsonArray();
            foreach (var line in evidence.Take(3))
            {
                arr.Add(TrimLine(line ?? string.Empty));
            }
            t0["evidence"] = arr;
        }
        if (!string.IsNullOrEmpty(next)) t0["next"] = next;
        if (extra != null)
        {
            foreach (var kv in extra) t0[kv.Key] = kv.Value?.DeepClone();
        }
        return TrimTo(t0, 2048);
    }

    public static string TrimLine(string line)
    {
        line = line?.Trim() ?? "";
        return Utf8SafeCut(line, 200);
    }

    public static JsonObject TrimTo(JsonObject obj, int maxBytes)
    {
        var verdict = obj["verdict"]?.GetValue<string>() ?? "ERROR";
        bool isPass = string.Equals(verdict, "PASS", StringComparison.Ordinal);

        if (isPass)
        {

            var dropPass = new[] { "evidence", "timings_ms", "next" };
            foreach (var k in dropPass)
            {
                if (Utf8Len(obj) <= maxBytes) break;
                obj.Remove(k);
            }
        }
        else
        {

            if (Utf8Len(obj) > maxBytes) obj.Remove("timings_ms");
            if (Utf8Len(obj) > maxBytes) obj.Remove("next");
            if (Utf8Len(obj) > maxBytes && obj["evidence"] is JsonArray ev && ev.Count > 1)
            {
                var keep = new JsonArray(ev[0]?.DeepClone());
                obj["evidence"] = keep;
            }
        }

        if (Utf8Len(obj) > maxBytes)
        {
            var minimal = new JsonObject
            {
                ["run_id"] = obj["run_id"]?.DeepClone() ?? "?",
                ["verdict"] = obj["verdict"]?.DeepClone() ?? "ERROR",
                ["phase"] = obj["phase"]?.DeepClone() ?? "REPORT",
                ["code"] = "PAYLOAD_OVERFLOW"
            };
            return minimal;
        }
        return obj;
    }

    public static string Utf8SafeCut(string s, int maxBytes)
    {
        if (s == null) return "";
        int bytes = 0;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            int charBytes;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                charBytes = 4;
            else if (c < 0x80) charBytes = 1;
            else if (c < 0x800) charBytes = 2;
            else charBytes = 3;

            if (bytes + charBytes > maxBytes) break;
            sb.Append(c);
            bytes += charBytes;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                sb.Append(s[++i]);
        }
        if (bytes > 0 && sb.Length < s.Length) return sb.ToString() + "…";
        return sb.ToString();
    }

    private static int Utf8Len(JsonObject obj) =>
        System.Text.Encoding.UTF8.GetByteCount(obj.ToJsonString());
}

