using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using OrangeIsland.Core;

namespace OrangeIsland;

// The What's new window: the notes built into this copy (ReleaseHistory), newest first. It opens from the About page, and
// from the panel's gear after an update, which marks the versions the update brought.
public sealed partial class SettingsWindow
{
    private Window? _notesDialog;
    private Window? _noticesDialog;

    internal async Task ShowReleaseNotesAsync()
    {
        const int aboutPage = 7;
        int generation = ++_updateOpenGeneration;
        ShowPage(aboutPage);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (generation != _updateOpenGeneration || !IsLoaded || !IsVisible || _app.Exiting) return;
        ShowReleaseNotes();
    }

    private void ShowReleaseNotes()
    {
        if (_notesDialog is not null || _updateDialog is not null || !IsVisible) return;
        var updates = _app.Updates;
        string? updatedFrom = updates.UpdatedFrom;
        var dialog = new Window { Title = Loc.T("更新内容", "What's new"), Owner = this, Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = UI.PanelFont,
            Background = UI.Brush(Surface), Foreground = UI.Brush(TextPrimary) };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OrangeIsland;component/SettingsResources.xaml", UriKind.Relative) });
        _palette.Apply(dialog.Resources); Native.SettingsChrome.Apply(dialog); dialog.ResizeMode = ResizeMode.NoResize;
        dialog.SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 24) };
        var title = TextLine(Loc.T("更新内容", "What's new"), 21, TextPrimary, FontWeights.SemiBold); title.Margin = new Thickness(0, 0, 0, 16); panel.Children.Add(title);

        var list = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var entries = ReleaseHistory.Shown(ReleaseHistory.Bundled, updates.CurrentVersion, updates.Beta);
        foreach (var entry in entries)
        {
            if (list.Children.Count > 0) list.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider), Margin = new Thickness(0, 18, 0, 18) });
            list.Children.Add(ReleaseSection(entry, ReleaseHistory.IsNew(entry.Version, updatedFrom, updates.CurrentVersion)));
        }
        if (entries.Count == 0) list.Children.Add(TextLine(Loc.T("这个版本没有附带更新说明。", "This version has no release notes."), 12, TextSecondary));
        var scroll = new ScrollViewer { Content = list, MaxHeight = 480, Style = (Style)FindResource("SettingsScrollViewer") };
        AutomationProperties.SetName(scroll, Loc.T("更新内容", "What's new"));
        panel.Children.Add(scroll);

        var close = ActionButton(Loc.T("关闭", "Close"), dialog.Close); close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 18, 0, 0); close.IsCancel = true; panel.Children.Add(close);
        void Theme()
        {
            _palette.Apply(dialog.Resources); dialog.Background = UI.Brush(Surface); dialog.Foreground = UI.Brush(TextPrimary);
            Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        }
        PaletteChanged += Theme;
        dialog.Closed += (_, _) => { PaletteChanged -= Theme; _notesDialog = null; };
        var frame = new Border { BorderThickness = new Thickness(1), Child = panel };
        frame.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsSurface"); dialog.Content = frame;
        _notesDialog = dialog; updates.MarkNotesSeen(); dialog.ShowDialog();
    }

    // One version: its number, a mark when the last update brought it, then its notes.
    private StackPanel ReleaseSection(ReleaseEntry entry, bool fresh)
    {
        var section = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(TextLine(entry.Version, 15, TextPrimary, FontWeights.SemiBold));
        if (fresh)
        {
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(9, 0, 0, 0),
                Background = UI.Brush(Inset), VerticalAlignment = VerticalAlignment.Center, Child = TextLine(Loc.T("本次更新", "Just updated"), 9, _palette.Accent, FontWeights.Medium) };
            header.Children.Add(badge);
        }
        section.Children.Add(header);
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; FillNotes(body, entry.Notes.For(Loc.Language)); section.Children.Add(body);
        return section;
    }

    // Release notes as they are written in releases/<version>.md, for this window and the update dialog. A line starting
    // with "- " or "* " is an item, indented two spaces per level below another; any other line is a heading, with or
    // without leading "#". **text** is bold. Everything stays text, so notes from the update server cannot run anything.
    private void FillNotes(Panel target, string text)
    {
        target.Children.Clear();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var content = line.TrimStart();
            if (content.Length == 0) continue;
            if (content.StartsWith("- ", StringComparison.Ordinal) || content.StartsWith("* ", StringComparison.Ordinal))
            {
                int level = (line.Length - content.Length) / 2;
                var item = new Grid { Margin = new Thickness(level * 14, 3, 0, 0) };
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); item.ColumnDefinitions.Add(new ColumnDefinition());
                item.Children.Add(TextLine(level == 0 ? "•" : "◦", 12, TextSecondary));
                var words = Inline(content[2..].Trim(), 12, TextSecondary); words.LineHeight = 18;
                Grid.SetColumn(words, 1); item.Children.Add(words);
                target.Children.Add(item);
            }
            else
            {
                var heading = Inline(content.TrimStart('#').Trim(), 12.5, TextPrimary, FontWeights.SemiBold);
                heading.Margin = new Thickness(0, target.Children.Count == 0 ? 0 : 12, 0, 2); target.Children.Add(heading);
            }
        }
        if (target.Children.Count > 0) target.Children[0].SetValue(MarginProperty, ((FrameworkElement)target.Children[0]).Margin with { Top = 0 });
    }

    // Wrapped text whose **marked** parts are bold. An unpaired "**" stays as written.
    private TextBlock Inline(string text, double size, string color, FontWeight? weight = null)
    {
        var block = TextLine("", size, color, weight); block.TextWrapping = TextWrapping.Wrap;
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
        {
            bool bold = i % 2 == 1 && i < parts.Length - 1;
            string part = i % 2 == 1 && !bold ? "**" + parts[i] : parts[i];
            if (part.Length > 0) block.Inlines.Add(new System.Windows.Documents.Run(part) { FontWeight = bold ? FontWeights.SemiBold : block.FontWeight });
        }
        return block;
    }

    // The third-party licence notices are built into the executable as an embedded resource, so a release is one file.
    // This window reads that resource and shows it as selectable, scrollable text — no external .txt to ship or open.
    private void ShowThirdPartyNotices()
    {
        if (_noticesDialog is not null || !IsVisible) return;
        string notices;
        try
        {
            using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream("OrangeIsland.ThirdPartyNotices.txt");
            if (stream is null) throw new IOException("third-party notices resource missing");
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            notices = reader.ReadToEnd();
        }
        catch (IOException)
        {
            Notice(Loc.T("第三方声明", "Third-party notices"), Loc.T("无法读取第三方声明文本。", "Could not read the third-party notices."));
            return;
        }

        var dialog = new Window { Title = Loc.T("第三方声明", "Third-party notices"), Owner = this, Width = 620, Height = 560,
            MinWidth = 440, MinHeight = 320, ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = UI.PanelFont,
            Background = UI.Brush(Surface), Foreground = UI.Brush(TextPrimary) };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OrangeIsland;component/SettingsResources.xaml", UriKind.Relative) });
        _palette.Apply(dialog.Resources); Native.SettingsChrome.Apply(dialog);
        dialog.SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        System.Windows.Media.TextOptions.SetTextFormattingMode(dialog, System.Windows.Media.TextFormattingMode.Display);

        var grid = new Grid { Margin = new Thickness(26, 22, 26, 24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = TextLine(Loc.T("第三方声明", "Third-party notices"), 21, TextPrimary, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 0, 14); Grid.SetRow(title, 0); grid.Children.Add(title);

        var text = new TextBox { Text = notices, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontSize = 12, FontFamily = UI.PanelFont, BorderThickness = new Thickness(0), Padding = new Thickness(0, 0, 14, 0),
            Background = System.Windows.Media.Brushes.Transparent, Foreground = UI.Brush(TextSecondary),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(text, Loc.T("第三方声明", "Third-party notices"));
        Grid.SetRow(text, 1); grid.Children.Add(text);

        var close = ActionButton(Loc.T("关闭", "Close"), dialog.Close); close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 18, 0, 0); close.IsCancel = true; Grid.SetRow(close, 2); grid.Children.Add(close); dialog.Loaded += (_, _) => close.Focus();
        void Theme()
        {
            _palette.Apply(dialog.Resources); dialog.Background = UI.Brush(Surface); dialog.Foreground = UI.Brush(TextPrimary);
            text.Foreground = UI.Brush(TextSecondary); Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        }
        PaletteChanged += Theme;
        dialog.Closed += (_, _) => { PaletteChanged -= Theme; _noticesDialog = null; };
        var frame = new Border { BorderThickness = new Thickness(1), Child = grid };
        frame.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsSurface"); dialog.Content = frame;
        _noticesDialog = dialog; dialog.ShowDialog();
    }
}
