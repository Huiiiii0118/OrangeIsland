using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TwelveIsland.Core;

namespace TwelveIsland.Native;

// The program a window belongs to. The notification watch and the "opened by hand" read share this one answer: every chat
// program that can announce a message must be recognisable again when its window comes to the front, whatever its process
// is called and whether the shell owns the frame around it.
internal static class ForegroundProgram
{
    // Store apps (WhatsApp, LINE, Skype, Teams from the Microsoft Store) draw inside a frame the shell owns; their own
    // CoreWindow is a child window belonging to the real process. Reading only the frame's process would see
    // ApplicationFrameHost and miss every one of them.
    private const string FrameClass = "ApplicationFrameWindow";
    private const string CoreClass = "Windows.UI.Core.CoreWindow";

    public static IntPtr Window() => GetForegroundWindow();

    // allowHostTitles lets the notification watch keep recognising a program named only by a browser or document title
    // (a web chat, a renamed launcher). Reading a reminder from such a title would be wrong — a page mentioning 微信 is
    // not the user opening WeChat — so the foreground check leaves it off.
    public static NotifyProgram? Program(IntPtr window, bool allowHostTitles = false)
    {
        if (window == IntPtr.Zero) return null;
        uint pid = Owner(window);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return null;
        string process;
        try { using var found = Process.GetProcessById((int)pid); process = found.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The window's process can exit between the two calls; there is no program to name then.
            return null;
        }
        if (NotifyPrograms.FromProcess(process) is { } known) return known;
        if (!allowHostTitles && GenericHost(process)) return null;
        return NotifyPrograms.FromTitle(Title(window));
    }

    public static string Title(IntPtr window)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0) return "";
        var text = new StringBuilder(length + 1);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    // The process that draws a window: its own, except for a UWP app whose window is ApplicationFrameHost's frame around
    // the app's own CoreWindow child. The frame draws only its title bar.
    private static uint Owner(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        if (ClassName(window) != FrameClass) return pid;
        uint app = 0;
        EnumChildWindows(window, (child, _) =>
        {
            if (ClassName(child) != CoreClass) return true;
            GetWindowThreadProcessId(child, out uint owner);
            if (owner == pid || owner == 0) return true;
            app = owner;
            return false;
        }, IntPtr.Zero);
        return app != 0 ? app : pid;
    }

    private static string ClassName(IntPtr window)
    {
        var text = new StringBuilder(64);
        return GetClassName(window, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    // Programs whose window title is whatever the user happened to open. Titles of these are never used to decide that a
    // chat program has been opened, even when the text names one: that would end a reminder nobody looked at.
    private static readonly string[] Hosts =
    [
        "chrome", "msedge", "firefox", "iexplore", "opera", "brave", "vivaldi", "explorer", "notepad", "wordpad",
        "winword", "excel", "powerpnt", "wps", "et", "wpp", "code", "devenv", "rider", "idea64", "sublime_text",
        "acrobat", "acrord32", "sumatrapdf", "mspaint", "photoshop", "obs64", "windowsterminal", "conhost",
        "searchhost", "shellexperiencehost", "startmenuexperiencehost", "systemsettings", "taskmgr", "dllhost"
    ];

    private static bool GenericHost(string process)
    {
        foreach (var host in Hosts)
            // A two-letter host ("et", "wpp") is matched exactly, so a chat client whose name merely starts that way
            // is never mistaken for an editor.
            if (host.Length <= 3
                ? process.Equals(host, StringComparison.OrdinalIgnoreCase)
                : process.StartsWith(host, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private delegate bool WindowProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowProc proc, IntPtr data);
}
