using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrangeIsland.Core;

namespace OrangeIsland;

public sealed partial class SettingsWindow
{
    private Border? _updateBadge;
    private UpdateView? _aboutUpdateView;
    private Window? _updateDialog;
    private int _updateOpenGeneration;
    // The in-app confirm overlay shown when a download finishes. It lives inside the settings window (not a separate
    // window), so it follows the app's theme, size and DPI, and cannot be left behind as a stray dialog.
    private Grid? _installConfirmOverlay;
    private TextBlock? _installConfirmText;
    private Button? _installConfirmLater;
    private string? _installConfirmShownFor;

    internal async Task CheckUpdatesOnOpenAsync()
    {
        int generation = ++_updateOpenGeneration;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (!IsLoaded || !IsVisible) return;
        await _app.Updates.CheckAsync(false, _app.Lifetime.Token);
        if (generation != _updateOpenGeneration || _app.Exiting || _updateDialog is not null) return;
        if (_app.Updates.ShouldPrompt(IsLoaded && IsVisible && WindowState != WindowState.Minimized, IsActive)) ShowUpdateDialog();
    }

    // The panel's update dot opens the About page and shows the update dialog, fetching the release details first if needed.
    internal async Task ShowUpdateNoticeAsync()
    {
        int generation = ++_updateOpenGeneration;
        ShowPage(AboutPage);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (!IsLoaded || !IsVisible) return;
        if (!_app.Updates.HasUpdate) await _app.Updates.CheckAsync(true, _app.Lifetime.Token);
        if (generation != _updateOpenGeneration || _app.Exiting || _updateDialog is not null) return;
        ShowUpdateDialog();
    }

    private void UpdatesChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdatesChanged); return; }
        if (_updateBadge is not null) _updateBadge.Visibility = _app.Updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        _aboutUpdateView?.Refresh();
        // A download just finished. Close the modal update window, bring the person to the About page and surface the
        // in-app confirm overlay once. "Later" defers it to the next start, so that path never sets this marker.
        var pending = _app.Updates.ConfirmInstallVersion;
        if (pending is not null && !_app.Exiting && _installConfirmShownFor != pending)
        {
            if (_updateDialog is not null) _updateDialog.Close();
            ShowPage(AboutPage);
            ShowInstallConfirm(pending);
        }
        else if (pending is null && _installConfirmShownFor is not null && _installConfirmOverlay?.Visibility == Visibility.Visible)
            DismissInstallConfirm();
    }

    private Border UpdateBadge()
    {
        var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(9, 0, 0, 0),
            Background = UI.Brush(Inset), VerticalAlignment = VerticalAlignment.Center, Visibility = _app.Updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed,
            Child = TextLine(Loc.T("新版本", "New"), 9, _palette.Accent, FontWeights.Medium) };
        AutomationProperties.SetName(badge, Loc.T("有新版本可用", "A new version is available")); return badge;
    }

    // A themed, in-app overlay covering the whole settings window, shown when a download finishes. It is a Grid above
    // the window content, so it follows theme, DPI and resize. "Install and restart" installs at once; "Later" (also
    // Escape / the dim backdrop) defers the offer to the next start and leaves the package ready.
    private Grid BuildInstallConfirmOverlay()
    {
        var dim = new Border { Background = UI.Brush(_palette.Dark ? "#CC101014" : "#CC6B7280") };
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(28, 24, 28, 24),
            Width = 400, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        card.BorderThickness = new Thickness(1);
        var panel = new StackPanel();
        var title = TextLine(Loc.T("更新已就绪", "Update is ready"), 19, TextPrimary, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(title);
        _installConfirmText = TextLine("", 12.5, TextSecondary); _installConfirmText.TextWrapping = TextWrapping.Wrap;
        _installConfirmText.Margin = new Thickness(0, 0, 0, 6); panel.Children.Add(_installConfirmText);
        var hint = TextLine(Loc.T("安装时软件会重启，你的设置会保留。", "The app restarts to finish installing; your settings are kept."), 11.5, TextTertiary);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 0, 0, 22); panel.Children.Add(hint);
        var defer = () => { _app.Updates.Defer(); DismissInstallConfirm(); };
        var later = ActionButton(Loc.T("稍后", "Later"), defer);
        later.Style = (Style)FindResource("SettingsOutlineButton"); later.IsCancel = true;
        _installConfirmLater = later;
        var installAction = () => { DismissInstallConfirm(); _app.InstallUpdate(); };
        var install = ActionButton(Loc.T("安装并重启", "Install and restart"), installAction, true);
        foreach (var b in new[] { later, install }) { b.MinWidth = 104; b.Padding = new Thickness(14, 7, 14, 7); }
        install.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(later); buttons.Children.Add(install); panel.Children.Add(buttons);
        card.Child = panel;
        var grid = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
        grid.Children.Add(dim); grid.Children.Add(card);
        dim.MouseLeftButtonUp += (_, _) => defer();
        card.MouseLeftButtonUp += (_, _) => { };
        grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { defer(); e.Handled = true; } };
        _installConfirmOverlay = grid;
        return grid;
    }

    private void ShowInstallConfirm(string version)
    {
        if (_installConfirmOverlay is null) return;
        if (_installConfirmText is not null)
            _installConfirmText.Text = Loc.T($"新版本 {version} 已下载完成，是否现在安装？", $"Version {version} has been downloaded. Install it now?");
        _installConfirmShownFor = version;
        _installConfirmOverlay.Visibility = Visibility.Visible;
        // Focus the safe option so Enter/Escape cannot install by accident.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        { if (_installConfirmOverlay?.Visibility == Visibility.Visible) _installConfirmLater?.Focus(); });
    }

    private void DismissInstallConfirm()
    {
        if (_installConfirmOverlay is not null) _installConfirmOverlay.Visibility = Visibility.Collapsed;
        _installConfirmShownFor = null;
        _app.Updates.ConsumeConfirmInstall();
    }

    internal void ShowUpdateDialog()
    {
        if (_updateDialog is not null || !_app.Updates.HasUpdate || !IsVisible) return;
        var dialog = new Window { Title = Loc.T("更新", "Update"), Owner = this, Width = 510, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = UI.PanelFont,
            Background = UI.Brush(Surface), Foreground = UI.Brush(TextPrimary) };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OrangeIsland;component/SettingsResources.xaml", UriKind.Relative) });
        _palette.Apply(dialog.Resources); Native.SettingsChrome.Apply(dialog); dialog.ResizeMode = ResizeMode.NoResize;
        dialog.SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 24) };
        var title = TextLine(Loc.T("软件更新", "Software update"), 21, TextPrimary, FontWeights.SemiBold); title.Margin = new Thickness(0, 0, 0, 22); panel.Children.Add(title);
        var view = CreateUpdateView(false); panel.Children.Add(view.Root);
        // "Update in background" starts (or keeps) the download, then closes this modal window so the app is fully
        // usable again. When the package is ready, the in-app confirm overlay asks whether to install now.
        Action backgroundAction = () => { _app.StartBackgroundUpdate(); dialog.Close(); };
        var background = ActionButton(Loc.T("后台更新", "Update in background"), backgroundAction);
        background.ToolTip = Loc.T("关闭本窗口，下载在后台继续，期间可正常使用软件；下载完成后会询问是否安装。",
            "Closes this window and keeps downloading quietly. The app stays usable; you'll be asked to confirm when it finishes.");
        background.Style = (Style)FindResource("SettingsOutlineButton");
        Action laterAction = () => { _app.Updates.Defer(); dialog.Close(); };
        var close = ActionButton(Loc.T("稍后", "Later"), laterAction);
        close.ToolTip = Loc.T("停止下载并关闭本窗口，软件照常使用；下次启动软件时重新弹出更新信息。",
            "Stops the download and closes this window. The app stays usable; the update is offered again the next time it starts.");
        close.Style = (Style)FindResource("SettingsOutlineButton");

        foreach (var button in new[] { background, close }) { button.Padding = new Thickness(14, 6, 14, 6); button.MinWidth = 96; }
        background.Margin = new Thickness(0, 18, 8, 0);
        close.Margin = new Thickness(0, 18, 0, 0); close.IsCancel = true;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(background); footer.Children.Add(close); panel.Children.Add(footer);
        void Footer()
        {
            var stage = _app.Updates.Stage;
            background.Visibility = _app.Updates.CanInstall && stage is AppUpdateStage.Available or AppUpdateStage.Downloading
                ? Visibility.Visible : Visibility.Collapsed;
        }
        Footer();
        void Refresh() { if (!dialog.Dispatcher.HasShutdownStarted) dialog.Dispatcher.InvokeAsync(() => { view.Refresh(); Footer(); }); }
        void Theme()
        {
            _palette.Apply(dialog.Resources); dialog.Background = UI.Brush(Surface); dialog.Foreground = UI.Brush(TextPrimary);
            Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        }
        _app.Updates.Changed += Refresh; PaletteChanged += Theme;
        dialog.Closed += (_, _) => { _app.Updates.Changed -= Refresh; PaletteChanged -= Theme; _updateDialog = null; };
        var frame = new Border { BorderThickness = new Thickness(1), Child = panel };
        frame.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsSurface"); dialog.Content = frame;
        _updateDialog = dialog; _app.Updates.MarkPromptShown(); dialog.ShowDialog();
    }

    private sealed record UpdateView(FrameworkElement Root, Action Refresh);
    // inline: the About page shows the status and its actions on one settings row, with progress, errors and
    // release notes across the full width below it. The update dialog keeps a card with the actions at the bottom.
    private UpdateView CreateUpdateView(bool inline)
    {
        var updates = _app.Updates;
        var title = TextLine("", inline ? 13 : 15, TextPrimary, FontWeights.SemiBold);
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Polite);
        var detail = TextLine("", 12, TextSecondary); detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, inline ? 3 : 7, 0, 0);
        var notes = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; string? notesText = null;
        var noteScroll = new ScrollViewer { Content = notes, MaxHeight = 170, Margin = inline ? new Thickness(0, 2, 0, 16) : new Thickness(0, 16, 0, 0), Style = (Style)FindResource("SettingsScrollViewer") };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = inline ? new Thickness(0, 2, 0, 16) : new Thickness(0, 18, 0, 0), Style = (Style)FindResource("SettingsUpdateProgress") };
        AutomationProperties.SetName(progress, Loc.T("更新下载进度", "Update download progress"));
        var error = TextLine("", 12, _palette.Dark ? "#F0A6AA" : "#B12E3A"); error.Margin = inline ? new Thickness(0, 0, 0, 14) : new Thickness(0, 12, 0, 0); error.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        Action downloadAction = () =>
        {
            if (updates.Stage == AppUpdateStage.Ready) _app.InstallUpdate();
            else if (updates.CanInstall) _ = updates.DownloadAsync(_app.Lifetime.Token);
            else SaveStatus(Loc.T("这一版由本地构建，更新请直接替换程序文件。", "This copy is built locally; replace its files to update it."), false);
        };
        var primary = ActionButton(Loc.T("下载更新", "Download update"), downloadAction, true);
        var check = ActionButton(Loc.T("检查更新", "Check for updates"), () => { _ = updates.CheckAsync(true, _app.Lifetime.Token); });
        var skip = ActionButton(Loc.T("跳过此版本", "Skip this version"), updates.SkipVersion);
        var cancel = ActionButton(Loc.T("取消下载", "Cancel download"), updates.CancelDownload);
        check.Style = cancel.Style = skip.Style = (Style)FindResource("SettingsOutlineButton");
        foreach (var button in new[] { primary, check, cancel, skip }) { button.Padding = new Thickness(14, 6, 14, 6); button.MinWidth = 96; }
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement root;
        if (inline)
        {
            var header = new Grid { MinHeight = 44 };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 16, 10) };
            labels.Children.Add(title); labels.Children.Add(detail); header.Children.Add(labels);
            primary.Margin = new Thickness(8, 0, 0, 0); skip.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(skip); actions.Children.Add(check); actions.Children.Add(cancel); actions.Children.Add(primary);
            actions.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(actions, 1); header.Children.Add(actions);
            var panel = new StackPanel(); panel.Children.Add(header); panel.Children.Add(progress); panel.Children.Add(error); panel.Children.Add(noteScroll);
            panel.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            panel.Children.Add(Toggle(Loc.T("自动检查更新", "Check for updates automatically"), updates.AutoCheck, value => updates.AutoCheck = value));
            root = panel;
        }
        else
        {
            var content = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
            content.Children.Add(title); content.Children.Add(detail); content.Children.Add(noteScroll); content.Children.Add(progress); content.Children.Add(error);
            actions.Margin = new Thickness(0, 18, 0, 0); content.Children.Add(actions);
            primary.MinWidth = 112; primary.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(primary); actions.Children.Add(check); actions.Children.Add(cancel);
            skip.Margin = new Thickness(8, 0, 0, 0); actions.Children.Add(skip);
            var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = content };
            card.SetResourceReference(Border.BackgroundProperty, "SettingsInput"); card.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
            root = card;
        }
        void Refresh()
        {
            var stage = updates.Stage; var release = updates.Release;
            title.Text = stage switch
            {
                AppUpdateStage.Checking => Loc.T("正在检查更新…", "Checking for updates…"), AppUpdateStage.Downloading => Loc.T($"正在下载 {release?.Version}", $"Downloading {release?.Version}"),
                AppUpdateStage.Ready => Loc.T("更新已准备就绪", "The update is ready"), AppUpdateStage.Installing => Loc.T("正在安装更新…", "Installing the update…"),
                _ when release is not null => Loc.T($"发现新版本 {release.Version}", $"Version {release.Version} is available"),
                _ when updates.LastChecked is not null && updates.Error is null && updates.NoticeVersion is null => Loc.T("已是最新版本", "Up to date"), _ => Loc.T("保持 Orange island 为最新版本", "Keep this app up to date")
            };
            detail.Text = stage switch
            {
                AppUpdateStage.Downloading => Loc.T($"{updates.Progress}% · 下载完成后可安装并重启。", $"{updates.Progress}% · When the download finishes, Orange island can install it and restart."),
                AppUpdateStage.Ready => Loc.T($"{updates.CurrentVersion} → {release?.Version} · 重启后生效，现有设置将保留。", $"{updates.CurrentVersion} → {release?.Version} · Takes effect after a restart. Your settings are kept."),
                AppUpdateStage.Installing => Loc.T("正在退出，完成后将重新打开。", "The app is closing and will open again when the update is done."),
                _ when release is not null && updates.IsSkipped(release.Version) => Loc.T("已跳过此版本，不再提示；仍可手动下载。", "This version is skipped and will not be announced again. You can still download it."),
                _ when release is not null && !updates.CanInstall => Loc.T("当前程序不是通过安装包或免安装压缩包获得的，无法在应用内更新。请前往下载页面下载新版本。",
                    "This copy did not come from the installer or the portable package and cannot update itself. Download the new version from the download page."),
                _ when release is not null => Loc.T($"{updates.CurrentVersion} → {release.Version} · 约 {release.Size / 1048576d:0.#} MB", $"{updates.CurrentVersion} → {release.Version} · About {release.Size / 1048576d:0.#} MB"),
                _ when updates.LastChecked is { } time => Loc.T("上次检查 ", "Last checked ") + time.ToLocalTime().ToString("MM-dd HH:mm"),
                _ => updates.AutoCheck ? Loc.T("Orange island 会在启动后和每 6 小时于后台检查一次新版本，打开设置时也会检查；也可以随时手动检查。", "Orange island checks for a new version in the background after startup and every 6 hours, and when settings open. You can also check at any time.")
                    : Loc.T("自动检查更新已关闭，可以随时手动检查。", "Automatic update checks are off. You can check at any time.")
            };
            // Release notes laid out as in the What's new window (FillNotes), only as text: remote release content cannot run
            // HTML or scripts. Rebuilt only when the notes change, not on every download progress report.
            var text = release?.Notes.For(Loc.Language) ?? "";
            if (text != notesText) { notesText = text; FillNotes(notes, text); }
            noteScroll.Visibility = text.Length > 0 && stage is not AppUpdateStage.Checking ? Visibility.Visible : Visibility.Collapsed;
            progress.Value = updates.Progress; progress.Visibility = stage == AppUpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
            error.Text = updates.Error ?? ""; error.Visibility = updates.Error is null ? Visibility.Collapsed : Visibility.Visible;
            primary.Content = stage == AppUpdateStage.Ready ? Loc.T("安装并重启", "Install and restart") : updates.CanInstall ? Loc.T("下载更新", "Download update") : Loc.T("前往下载页面", "Open download page");
            primary.Visibility = release is not null && stage != AppUpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed; primary.IsEnabled = !updates.IsBusy;
            check.IsEnabled = !updates.IsBusy; check.Visibility = stage is AppUpdateStage.Downloading or AppUpdateStage.Ready or AppUpdateStage.Installing ? Visibility.Collapsed : Visibility.Visible;
            // On the About page checking is the main action until a found release hands that role to the download button.
            check.Style = (Style)FindResource(inline && release is null ? "SettingsPrimaryButton" : "SettingsOutlineButton");
            cancel.Visibility = stage == AppUpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
            skip.Visibility = release is not null && stage == AppUpdateStage.Available && !updates.IsSkipped(release.Version) ? Visibility.Visible : Visibility.Collapsed;
            skip.IsEnabled = !updates.IsBusy;
            AutomationProperties.SetName(primary, primary.Content.ToString());
        }
        Refresh(); return new UpdateView(root, Refresh);
    }
}
