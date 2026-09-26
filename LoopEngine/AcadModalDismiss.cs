using System.Runtime.InteropServices;
using System.Text;

namespace LoopEngine;

internal static class AcadModalDismiss
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private const uint WM_CLOSE = 0x0010;
    private const uint BM_CLICK = 0x00F5;

    private static readonly string[] DismissButtonTitles =
    {
        "否", "不", "取消", "关闭", "以后再说", "不发送",
        "No", "Cancel", "Close", "Don't Send", "Later"
    };

    private static readonly string[] LoadButtonTitles =
    {
        "加载", "是", "始终加载", "Load", "Yes", "Always Load"
    };

    // "代理信息/代理图形"（缺少 Object Enabler，如 PnPDwg2dAnno）：信息性对话框，
    // 点"确定"接受代理显示并关闭；此前未识别导致残余弹窗堵死宿主 Idle 泵
    private static readonly string[] ProxyOkButtonTitles =
    {
        "确定", "OK"
    };

    public static int DismissAll(uint acadPid)
    {
        int dismissed = 0;
        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == acadPid && IsWindowVisible(hWnd) && GetRaw(hWnd, className: true) == "#32770")
            {
                if (TryDismiss(hWnd)) dismissed++;
            }
            return true;
        }, IntPtr.Zero);
        return dismissed;
    }

    private static bool TryDismiss(IntPtr dialog)
    {

        var title = GetRaw(dialog, className: false);
        bool security = title.Contains("安全") || title.Contains("Security", StringComparison.OrdinalIgnoreCase);
        bool proxyInfo = title.Contains("代理信息") || title.Contains("代理图形")
                         || title.Contains("Proxy", StringComparison.OrdinalIgnoreCase);
        var prefer = security ? LoadButtonTitles : proxyInfo ? ProxyOkButtonTitles : DismissButtonTitles;

        var buttons = new List<IntPtr>();
        IntPtr h = IntPtr.Zero;
        do
        {
            h = FindWindowEx(dialog, h, "Button", null);
            if (h != IntPtr.Zero) buttons.Add(h);
        } while (h != IntPtr.Zero);

        foreach (var b in buttons)
        {
            var text = GetRaw(b, className: false).Trim();
            foreach (var t in prefer)
            {
                if (text.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith(t, StringComparison.OrdinalIgnoreCase))
                {
                    Program.Diag("dismiss", $"acad dialog [{title}] -> click [{text}]");
                    SendMessage(b, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }
            }
        }

        Program.Diag("dismiss", $"acad dialog [{title}] -> WM_CLOSE");
        SendMessage(dialog, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    private static string GetRaw(IntPtr hWnd, bool className)
    {
        var sb = new StringBuilder(256);
        if (className) GetClassName(hWnd, sb, 256);
        else GetWindowText(hWnd, sb, 256);
        return sb.ToString();
    }
}

