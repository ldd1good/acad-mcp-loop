using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SamplePlugin
{
    /// <summary>
    /// 磁盘 JSON 日志（每行一个 JSON 对象）。字段结构与引擎磁盘日志采集约定对齐：
    /// Timestamp / Level / Message / Exception / ProcessId / ThreadId / Properties。
    /// 目录默认 C:\Logs\CadLoop（LOOP_LOG_DIR 可覆盖），与 loop.config.json 示例的
    /// disk_log_dir + disk_log_file_prefix（SamplePlugin_）配套，run_iteration 的
    /// expect/log_must_contain 才能在盘日志里命中业务行。
    /// </summary>
    public static class SampleLog
    {
        private static readonly object Gate = new object();

        public static string Dir =>
            Environment.GetEnvironmentVariable("LOOP_LOG_DIR") ?? @"C:\Logs\CadLoop";

        public static void Write(string level, string message, IDictionary<string, object> props = null)
        {
            try
            {
                var line = MiniJson.Write(new Dictionary<string, object>
                {
                    { "Timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") },
                    { "Level", level },
                    { "Message", message },
                    { "Exception", null },
                    { "ProcessId", Process.GetCurrentProcess().Id },
                    { "ThreadId", Thread.CurrentThread.ManagedThreadId },
                    { "Properties", (object)props ?? new Dictionary<string, object>() }
                });
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(
                        Path.Combine(Dir, "SamplePlugin_" + DateTime.Now.ToString("yyyyMMdd") + ".json"),
                        line + Environment.NewLine);
                }
            }
            catch (Exception) { }
        }
    }
}
