using System.Security.Cryptography;

namespace LoopEngine;

internal static class DrawingLock
{
    private const string Prefix = "HotReloadCAD.LoopEngine.Drawing.";

    public static Mutex? TryEnter(string? fixtureHint, out string? busyError, out string? drawing)
    {
        busyError = null;
        drawing = ResolveDrawing(fixtureHint);
        if (string.IsNullOrWhiteSpace(drawing)) return null;

        var mutex = new Mutex(initiallyOwned: false, name: Prefix + KeyHash(drawing), out _);
        try
        {
            if (mutex.WaitOne(0)) return mutex;
        }
        catch (AbandonedMutexException)
        {

            Program.Diag("drawing-lock", $"abandoned drawing lock taken over: {drawing}");
            return mutex;
        }

        busyError = $"drawing is already being driven by another engine instance: {drawing} "
            + $"(named mutex {Prefix}*); wait for that run to finish, then retry";
        mutex.Dispose();
        return null;
    }

    private static string? ResolveDrawing(string? fixtureHint)
    {
        // fixture 显式参数优先于环境/配置/最近图纸：迭代跑在哪张图上，锁就落在哪张图上
        if (!string.IsNullOrWhiteSpace(fixtureHint)) return Normalize(fixtureHint);
        var env = Environment.GetEnvironmentVariable("LOOP_DRAWING");
        if (!string.IsNullOrWhiteSpace(env)) return Normalize(env);
        var cfg = Repo.ConfigString("drawing");
        if (!string.IsNullOrWhiteSpace(cfg)) return Normalize(cfg);
        return Repo.LastSeenDrawing is { } last ? Normalize(last) : null;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path.Trim()).ToLowerInvariant(); }
        catch { return path.Trim().ToLowerInvariant(); }
    }

    private static string KeyHash(string normalized)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes, 0, 8);
    }
}

