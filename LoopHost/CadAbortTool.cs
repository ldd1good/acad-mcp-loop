using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;

namespace LoopHost
{
    /// <summary>
    /// P1-1 安全中止：acad 主线程挂起（regen 死锁 / 命令不返回）时的 Esc 注入。
    /// PostMessage 异步投递不占主线程，挂起状态也能送达；另经 MainThreadPump.Post 排队
    /// SendStringToExecute("\x03\x03")（主线程恢复后生效的兜底）。
    /// 降级顺序（docs/loop-engine-issues-2026-09-12.md P1-1）：Esc 注入 → 复查 → 仍 hung 才建议重启。
    /// </summary>
    internal static class CadAbortTool
    {
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint VK_ESCAPE = 0x1B;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>host.abort 处理：在服务线程直接执行（不排队等主线程——挂起时等不到）。</summary>
        public static Dictionary<string, object> Handle()
        {
            var pump = MainThreadPump.Instance;
            var data = new Dictionary<string, object>
            {
                { "stalled_ms", pump.StalledMs },
                { "pending_jobs", pump.PendingCount },
                { "oldest_pending_ms", pump.OldestPendingAgeMs }
            };

            int posted = 0;
            try
            {
                IntPtr hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        if (PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero)) posted++;
                        if (PostMessage(hwnd, WM_KEYUP, (IntPtr)VK_ESCAPE, IntPtr.Zero)) posted++;
                    }
                }
                else
                {
                    data["window_note"] = "main window handle unavailable (minimized?)";
                }
            }
            catch (Exception ex)
            {
                data["post_error"] = ex.GetType().Name + ": " + ex.Message;
            }
            data["posted_esc"] = posted;

            // 命令行取消序列排队：主线程恢复后吃掉 \x03\x03（ESC），取消可能仍挂着的命令提示
            try
            {
                pump.Post("host.abort.queued", delegate
                {
                    var doc = Application.DocumentManager.MdiActiveDocument;
                    if (doc != null) doc.SendStringToExecute("\x003\x003", true, false, false);
                });
                data["queued_cancel"] = true;
            }
            catch (Exception ex)
            {
                data["queue_error"] = ex.GetType().Name + ": " + ex.Message;
            }

            data["ok"] = true;
            data["note"] = posted > 0
                ? "Esc posted to main window (async) + cancel chars queued; recheck attach_status in 30s — restart acad only if still hung"
                : "Esc post failed; recheck attach_status in 30s — restart acad if still hung";
            return data;
        }
    }
}
