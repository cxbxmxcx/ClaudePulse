using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudePulse.Services;

/// <summary>
/// Finds the top-level window hosting a Claude Code session (the CLI itself is
/// a console process with no window) by walking up the parent-process chain,
/// then brings that window to the foreground.
/// </summary>
public static class WindowFocusService
{
    public static bool FocusSession(int claudePid)
    {
        foreach (var pid in AncestorChain(claudePid, maxDepth: 12))
        {
            var hwnd = FindMainWindow(pid);
            if (hwnd != IntPtr.Zero)
                return BringToForeground(hwnd);
        }
        return false;
    }

    private static IEnumerable<int> AncestorChain(int pid, int maxDepth)
    {
        int current = pid;
        for (int i = 0; i < maxDepth && current > 4; i++)
        {
            yield return current;
            int parent = GetParentPid(current);
            if (parent <= 0 || parent == current) yield break;

            // PID-reuse guard: a real parent must have started before its child.
            try
            {
                using var child = Process.GetProcessById(current);
                using var candidate = Process.GetProcessById(parent);
                if (candidate.StartTime > child.StartTime.AddSeconds(1)) yield break;
            }
            catch (Exception) { yield break; }

            current = parent;
        }
    }

    private static int GetParentPid(int pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return -1;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out _);
            return status == 0 ? (int)pbi.InheritedFromUniqueProcessId : -1;
        }
        finally { CloseHandle(handle); }
    }

    private static IntPtr FindMainWindow(int pid)
    {
        IntPtr best = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint windowPid);
            if (windowPid != (uint)pid) return true;
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true; // owned popup

            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0) return true;

            best = hwnd;
            return false; // stop at the first real titled window
        }, IntPtr.Zero);
        return best;
    }

    private static bool BringToForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint ourThread = GetCurrentThreadId();

        // Attach input queues so Windows permits the foreground switch.
        if (foregroundThread != ourThread) AttachThreadInput(ourThread, foregroundThread, true);
        if (targetThread != ourThread) AttachThreadInput(ourThread, targetThread, true);
        try
        {
            BringWindowToTop(hwnd);
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            if (foregroundThread != ourThread) AttachThreadInput(ourThread, foregroundThread, false);
            if (targetThread != ourThread) AttachThreadInput(ourThread, targetThread, false);
        }
    }

    // ------------------------------------------------------------------ P/Invoke

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint GW_OWNER = 4;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass,
        ref PROCESS_BASIC_INFORMATION info, int length, out int returnLength);

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
}
