using System.IO;
using System.Windows.Threading;
using OrangeIsland.Core;
using OrangeIsland.Native;

namespace OrangeIsland;

// Keeps the notification history and watches the programs that announce messages. One of each for the whole app: every panel
// draws the same list, and a notice read on one panel is read on all of them.
public partial class App
{
    internal NotifyStore? Notices { get; private set; }
    private NotifyWatch? _notifyWatch;
    private TaskbarWatch? _taskbarWatch;
    private string _notifyFolder = "";
    // Bringing a watched program up by hand reads its notices too, not only a click on the capsule. The timer follows the
    // foreground window; a program running in the tray does not own it and is not "opened" by this test.
    private DispatcherTimer? _foregroundWatch;
    private IntPtr _foregroundWindow;
    // No timer clears unread notices any more: the capsule stays while anything is unread. Kept only to stop a timer
    // left over from an earlier run when the app exits (StopNotify).
    private readonly Dictionary<string, DispatcherTimer> _noticeTimers = new(StringComparer.Ordinal);

    // Raised when a notice arrives, so a panel shows it on the collapsed island.
    internal event Action<NotifyItem>? NoticeArrived;
    // Raised when the list changed in any way (read, removed, cleared), so open pages draw it again.
    internal event Action? NoticesChanged;

    internal string? MailError => _notifyWatch?.MailError;
    internal DateTimeOffset? MailSeen => _notifyWatch?.MailSeen;
    internal bool MailBusy => _notifyWatch?.MailBusy == true;
    internal bool MailConfigured => Settings.NotifyMail && Settings.NotifyMailUser.Length > 0 && MailSecret.Has;

    // Whether a program's notices are wanted: each group has its own switch.
    internal bool Wanted(NotifySource source) => source switch
    {
        NotifySource.Qq => Settings.NotifyQq,
        NotifySource.WeChat => Settings.NotifyWeChat,
        NotifySource.Mail => Settings.NotifyMail,
        _ => Settings.NotifyApps
    };

    internal void StartNotify()
    {
        if (Notices is null)
        {
            _notifyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrangeIsland");
            MailSecret.Use(_notifyFolder);
            Notices = new NotifyStore(_notifyFolder);
            Notices.Changed += () => NoticesChanged?.Invoke();
        }
        // The programs' own windows and the notifications Windows keeps for them, plus the mailbox over IMAP.
        bool wanted = Settings.NotifyQq || Settings.NotifyWeChat || Settings.NotifyMail || Settings.NotifyApps;
        if (wanted && _notifyWatch is null)
        {
            _notifyWatch = new NotifyWatch(Dispatcher, () => Settings);
            _notifyWatch.Arrived += NoticesArrived;
            _notifyWatch.MailStateChanged += () => NoticesChanged?.Invoke();
            _notifyWatch.Start();
        }
        else if (!wanted && _notifyWatch is not null)
        {
            _notifyWatch.Arrived -= NoticesArrived;
            _notifyWatch.Dispose(); _notifyWatch = null;
        }
        // The taskbar and the docks flash a program's button when a message waits, which is read where the programs
        // themselves give nothing to read (their popup window, the toast store and the accessibility tree can all be empty).
        // A flash adds an unread notice; only opening the program or reading it on the page clears it.
        if (wanted && _taskbarWatch is null)
        {
            _taskbarWatch = new TaskbarWatch();
            _taskbarWatch.Attention += TaskbarAttention;
            _taskbarWatch.Settled += TaskbarSettled;
            _taskbarWatch.Start();
        }
        else if (!wanted && _taskbarWatch is not null)
        {
            _taskbarWatch.Attention -= TaskbarAttention;
            _taskbarWatch.Settled -= TaskbarSettled;
            _taskbarWatch.Dispose(); _taskbarWatch = null;
        }
        // One second catches a window the taskbar has just raised without doing work on every UI frame. Starting from the
        // window already in front means the program the user happened to be in when the app launched is not "opened" again.
        if (wanted && _foregroundWatch is null)
        {
            _foregroundWatch = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _foregroundWatch.Tick += (_, _) => CheckForegroundRead();
            _foregroundWindow = ForegroundProgram.Window();
            _foregroundWatch.Start();
        }
        else if (!wanted && _foregroundWatch is not null)
        {
            _foregroundWatch.Stop(); _foregroundWatch = null;
        }
        _notifyWatch?.ApplyInterval();
    }

    // A notice is read not only by clicking the capsule: opening its program by hand — taskbar, dock, Alt+Tab, or a chat
    // window inside it — answers it as well. Only a change of foreground window counts as opening, so a message that
    // arrives while the user is already looking at that window is still announced. Reading follows the capsule's click
    // exactly: the notices stay in the list, only their unread state ends, and only for this program. Marking a program
    // that has nothing unread does nothing, so a switch between two of its windows is harmless.
    private void CheckForegroundRead()
    {
        IntPtr window = ForegroundProgram.Window();
        if (window == _foregroundWindow) return;
        _foregroundWindow = window;
        if (Exiting || Notices is not { Unread: > 0 } store) return;
        // The shared recognition — process, a Store app's CoreWindow, then a title that is not a browser's or a document's
        // — covers every program the watch can announce. None of them can leave a reminder behind after it was opened.
        if (ForegroundProgram.Program(window) is not { } program) return;
        store.MarkProgramRead(program.Key);
        // The mailbox read over IMAP has no window of its own; opening any mail client answers those notices too.
        if (program.Source == NotifySource.Mail) store.MarkProgramRead(NotifyPrograms.MailKey);
        CancelTimer(program.Key);
    }

    internal void StopNotify()
    {
        _foregroundWatch?.Stop(); _foregroundWatch = null;
        _notifyWatch?.Dispose(); _notifyWatch = null;
        foreach (var timer in _noticeTimers.Values) timer.Stop();
        _noticeTimers.Clear();
        if (_taskbarWatch is null) return;
        _taskbarWatch.Attention -= TaskbarAttention;
        _taskbarWatch.Settled -= TaskbarSettled;
        _taskbarWatch.Dispose(); _taskbarWatch = null;
    }

    // A flash on a watched program's button, in the taskbar or on a dock: a message waits. One unread notice of the program is
    // already showing unless there is none yet; the taskbar cannot say more about the message than "there is one", and a notice
    // window the program put up instead can name the sender. The watch raises this on a thread of its own; the store and the
    // panels live on the UI thread.
    private void TaskbarAttention(NotifyProgram program, string hint) => Dispatcher.BeginInvoke(() =>
    {
        if (Notices is not { } store || Exiting || !Wanted(program.Source)) return;
        // Each new flash is another message: the capsule counts them, even though the taskbar cannot say what they say. The
        // sender a notice window named is used when there is one; otherwise the program's own name, and "something new"
        // under it — nothing is made up to look like the message itself.
        string title = hint.Length > 0 && !hint.Equals(program.Name, StringComparison.OrdinalIgnoreCase)
            ? hint : Loc.T(program.Name, program.Name);
        string body = Loc.T("有新消息", "Something new");
        var added = store.Add(program.Source, program.Key, title, body, Loc.T(program.Name, program.Name), fresh: true);
        if (added is null) return;
        NoticeArrived?.Invoke(added);
    });

    // The flashing stopped: that is not the message being read. The capsule stays while anything is unread, and only
    // opening the program (or reading or clearing it on the page) ends the reminder.
    private void TaskbarSettled(NotifyProgram program) => Dispatcher.BeginInvoke(() =>
    {
        if (Exiting) return;
        CancelTimer(program.Key);
        if (program.Source == NotifySource.Mail) CancelTimer(NotifyPrograms.MailKey);
    });

    private void CancelTimer(string program)
    {
        if (!_noticeTimers.Remove(program, out var timer)) return;
        timer.Stop();
    }

    // The window that announced a program's latest notice: the icon the panel draws is taken from the program owning it, so
    // a program installed anywhere, or one that sets its taskbar icon at run time, shows the icon the user actually sees.
    internal IntPtr NotifyWindow(string program) => _taskbarWatch?.WindowOf(program) ?? IntPtr.Zero;

    // The capsule was clicked: the program that announced the newest unread notice comes up in front. Which of its notices
    // that counted as read is the caller's to say — one notice, not the program's whole list and not everyone's. A mail
    // notice whose client is not running has no window to bring up: the click itself is the answer — the caller reads the
    // notices, the reminder ends here, and no mail program is started in its place.
    internal bool OpenNotifyApp(string program)
    {
        if (Notices is null) return false;
        bool opened = AppActivate.Open(program, _taskbarWatch?.WindowOf(program) ?? IntPtr.Zero);
        if (!opened && !IsMail(program)) return false;
        CancelTimer(program);
        return true;
    }

    // Whether a notice's program is mail at all: the mailbox read over IMAP, or one of the mail clients the page watches.
    private static bool IsMail(string program)
    {
        if (program == NotifyPrograms.MailKey) return true;
        foreach (var candidate in NotifyPrograms.All)
            if (candidate.Key == program && candidate.Source == NotifySource.Mail) return true;
        return false;
    }

    // Asks the mailbox once now, for the page's "立即检查" button. onChecked reports the outcome to the settings window.
    internal void CheckMailNow(Action<string>? onChecked = null) => _notifyWatch?.PokeMail(onChecked);

    private void NoticesArrived(IReadOnlyList<NotifyArrival> arrivals)
    {
        if (Notices is null) return;
        bool added = false;
        foreach (var arrival in arrivals)
        {
            if (!Wanted(arrival.Source)) continue;
            if (Notices.Add(arrival.Source, arrival.Program, arrival.Title, arrival.Body, arrival.App, sender: arrival.Sender) is null) continue;
            added = true;
        }
        if (added && Notices.LatestUnread is { } latest) NoticeArrived?.Invoke(latest);
    }

    // The mailbox password is written where only this user can read it back.
    internal bool SetMailPassword(string password) => MailSecret.Write(password ?? "");
}
