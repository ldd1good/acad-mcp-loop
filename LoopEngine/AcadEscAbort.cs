using System.Runtime.InteropServices;

namespace LoopEngine;

/// <summary>
/// P1-1 兜底：acad 主线程挂起（regen 死锁 / 命令不返回）时的 Esc 注入。
/// PostMessage 异步投递，不依赖目标线程的消息泵正在运转——挂起状态也能送达；
/// 宿主 host.abort 不可用时（LoopHost 未升级 / 管道不应答）走这条路径。
/// 降级顺序（docs/loop-engine-issues-2026-09-12.md P1-1）：Esc 注入 → 复查 → 仍 hung 才建议重启。
/// </summary>
internal static class AcadEscAbort
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint VK_ESCAPE = 0x1B;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static JsonObject TrySendEscape(int? preferredPid)
    {
        var res = new JsonObject { ["ok"] = false };
        try
        {
            var procs = HostClient.ListAcadProcesses();
            if (procs.Count == 0)
            {
                res["error"] = "no acad process found";
                return res;
            }
            // 目标选择：优先指定 pid（宿主管道所在实例）；否则选挂起（Responding=false）
            // 的实例——Esc 注入本来就是为救它；都不挂起且唯一时取唯一实例
            var target = preferredPid != null ? procs.FirstOrDefault(p => p.Pid == preferredPid) : null;
            target ??= procs.FirstOrDefault(p => !p.Responding) ?? (procs.Count == 1 ? procs[0] : null);
            if (target == null)
            {
                res["error"] = "multiple responsive acad instances and no preferred pid; "
                    + "call attach_status to pick one, then retry";
                return res;
            }
            using var proc = System.Diagnostics.Process.GetProcessById(target.Pid);
            IntPtr hwnd = proc.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                res["error"] = $"acad pid={target.Pid} has no main window handle";
                return res;
            }
            int posted = 0;
            for (int i = 0; i < 2; i++)
            {
                if (PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero)) posted++;
                if (PostMessage(hwnd, WM_KEYUP, (IntPtr)VK_ESCAPE, IntPtr.Zero)) posted++;
            }
            res["ok"] = posted > 0;
            res["posted"] = posted;
            res["acad_pid"] = target.Pid;
            res["responding"] = target.Responding;
            res["note"] = "Esc 已 PostMessage 到 acad 主窗口（异步，挂起态可送达）；"
                + "30s 后 attach_status 复查，仍 hung 则重启 acad";
        }
        catch (Exception ex)
        {
            res["error"] = ex.GetType().Name + ": " + ex.Message;
        }
        return res;
    }
}
