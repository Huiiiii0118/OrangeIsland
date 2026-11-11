using System.Net.NetworkInformation;
using System.Windows;
using Microsoft.Win32;
using OrangeIsland.Core;

namespace OrangeIsland;

// Automatic update checks. The panel's minute clock asks whether a check is due; a returning network and waking from
// sleep ask too. AppUpdates decides when one is due, merges it with any check already running, and keeps failures silent.
public partial class App
{
    private bool _updateEvents;
    private string? _noticeVersion;
    private bool _notesNotice;

    private void StartBackgroundUpdates()
    {
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        Updates.Changed += UpdateNoticeChanged;
        _updateEvents = true;
        _noticeVersion = Updates.NoticeVersion; _notesNotice = Updates.UpdatedFrom is not null; foreach (var deck in Decks) deck.UpdateNotice();
    }

    private void StopBackgroundUpdates()
    {
        if (!_updateEvents) return;
        _updateEvents = false;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        Updates.Changed -= UpdateNoticeChanged;
    }

    // A download started from the update dialog's "background" button. The dialog closes so the app is fully usable; the
    // download runs on AppUpdates (independent of any window), the About page swaps its row for an "updating" progress
    // bar, and a successful download installs itself. Cancel/failure simply returns the About page to its normal controls.
    internal void StartBackgroundUpdate()
    {
        if (Exiting || Updates.Stage is not AppUpdateStage.Available || !Updates.CanInstall) return;
        Updates.BeginBackground();
        _ = CompleteBackgroundUpdate();
    }

    private async Task CompleteBackgroundUpdate()
    {
        try
        {
            await Updates.DownloadAsync(Lifetime.Token);
            if (Exiting || Updates.Stage != AppUpdateStage.Ready) return;
            // Ready means the package is staged and verified. Installing shuts the app down; there is no UI to restore.
            InstallUpdate();
        }
        finally
        {
            // Reached only when the download was cancelled or failed (a successful install exits the process). Normal
            // wording and buttons come back, and nothing is left stuck showing "updating".
            Updates.EndBackground();
        }
    }

    internal void CheckUpdatesIfDue()
    {
        if (Exiting || !_updateEvents) return;
        // Asking whether any network is up lists every adapter, which took over 100 ms on the UI thread with many virtual
        // adapters; it runs on the thread pool, and the check itself was already in the background.
        var lifetime = Lifetime.Token;
        _ = Task.Run(() =>
        {
            bool online;
            try { online = NetworkInterface.GetIsNetworkAvailable(); }
            catch (NetworkInformationException) { online = false; }
            return online;
        }, lifetime).ContinueWith(task => { if (!Exiting && task.IsCompletedSuccessfully) _ = Updates.CheckInBackgroundAsync(task.Result, lifetime); },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) Dispatcher.BeginInvoke(CheckUpdatesIfDue);
    }

    private void PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(CheckUpdatesIfDue);
    }

    // Download progress also raises Changed; the panel is touched only when the marked version or the update's notes notice changes.
    private void UpdateNoticeChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateNoticeChanged); return; }
        if (Updates.NoticeVersion == _noticeVersion && Updates.UpdatedFrom is not null == _notesNotice) return;
        _noticeVersion = Updates.NoticeVersion; _notesNotice = Updates.UpdatedFrom is not null; foreach (var deck in Decks) deck.UpdateNotice();
    }
}
