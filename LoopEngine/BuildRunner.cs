using System.Diagnostics;

namespace LoopEngine;

public static class BuildRunner
{
    public sealed class BuildResult
    {
        public bool Ok;
        public int ExitCode;
        public long Ms;
        public string? DllVersion;
        public List<string> Errors = new();
        public List<string> Tail = new();
    }

    public static BuildResult Run()
    {
        var result = new BuildResult();
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{Repo.BuildScript}\"",
            WorkingDirectory = Repo.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null) { result.ExitCode = -1; return result; }

        var outputLines = new List<string>();
        var outputLock = new object();
        var outTask = Task.Run(() =>
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
                lock (outputLock) outputLines.Add(line);
        });
        var errTask = Task.Run(() =>
        {
            string? line;
            while ((line = p.StandardError.ReadLine()) != null)
                lock (outputLock) outputLines.Add("[stderr] " + line);
        });

        if (!p.WaitForExit(600_000))
        {
            try { p.Kill(true); } catch { }
            result.ExitCode = -2;
        }
        else
        {
            result.ExitCode = p.ExitCode;
        }
        Task.WaitAll(new[] { outTask, errTask }, 5000);
        result.Ms = sw.ElapsedMilliseconds;

        List<string> lines;
        lock (outputLock) lines = new List<string>(outputLines);

        foreach (var line in lines)
        {
            if (line.Contains("error ", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains(".cs(") || line.Contains(".cs:")))
                result.Errors.Add(line.Trim());
            if (line.StartsWith("DLL Version:", StringComparison.Ordinal))
                result.DllVersion = line["DLL Version:".Length..].Trim();
        }
        result.Tail = lines.Skip(Math.Max(0, lines.Count - 15)).ToList();
        result.Ok = result.ExitCode == 0;
        return result;
    }
}

