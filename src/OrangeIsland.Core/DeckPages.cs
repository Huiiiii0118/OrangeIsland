namespace OrangeIsland.Core;

// The pages of the open panel, in one place: their order in the page switch, whether each is shown, the size each asks for,
// and which page the panel falls back to. The panel and the settings window ask here instead of comparing pages themselves.
public static class DeckPages
{
    // The page switch, left to right, until the order is changed in the settings (DeckSettings.PageOrder).
    public static readonly IReadOnlyList<DeckPage> Order = [DeckPage.Notify, DeckPage.Music, DeckPage.Monitor, DeckPage.Clipboard, DeckPage.Convert];
    // Each known page once, in the saved order; a page missing from the file takes its default place after the others.
    // Notifications lead the switch: a page read first thing in the morning sits where the eye lands first, whatever an older
    // settings file saved. The pages after it keep the order they were put in.
    public static List<DeckPage> Normalize(IEnumerable<DeckPage>? order) =>
        [.. (order ?? []).Where(page => page != DeckPage.Usage && Enum.IsDefined(page)).Concat(Order).Prepend(DeckPage.Notify).Distinct()];

    public static bool Shown(DeckSettings settings, DeckPage page) => page switch
    {
        DeckPage.Music => settings.MusicPage, DeckPage.Monitor => settings.MonitorPage,
        DeckPage.Notify => settings.NotifyPage, DeckPage.Clipboard => settings.ClipboardPage, DeckPage.Convert => settings.ConvertPage, _ => false
    };

    public static IReadOnlyList<DeckPage> Visible(DeckSettings settings) => settings.PageOrder.Where(page => Shown(settings, page)).ToList();

    // The page shown when wanted is asked for: wanted while it is shown, otherwise the first shown page in switch order.
    // With every page switched off it is the music page, which then says that it is off.
    public static DeckPage Resolve(DeckSettings settings, DeckPage wanted)
        => Shown(settings, wanted) ? wanted : Visible(settings) is [var first, ..] ? first : DeckPage.Music;

    // The page the panel opens on; null keeps the last viewed page.
    public static DeckPage? Opening(DefaultDeckPage choice) => choice switch
    {
        DefaultDeckPage.Music => DeckPage.Music, DefaultDeckPage.Monitor => DeckPage.Monitor,
        DefaultDeckPage.Notify => DeckPage.Notify, DefaultDeckPage.Clipboard => DeckPage.Clipboard, DefaultDeckPage.Convert => DeckPage.Convert, _ => null
    };

    // Every page's size lies within these; the usage page is also at least as wide as its columns need (MinimumWidth).
    // Sizes are before the scale (PanelScales): drawn at 150 %, a page of the largest size is 1800 x 600 DIP.
    public const double MinWidth = 440, MaxWidth = 1200, MinHeight = 140, MaxHeight = 400;
    // The smallest width an older settings.json saved for the music page. It no longer limits the panel: every shown page
    // shares one size, and MusicLayout keeps the volume bar usable down to MinWidth.
    public const double MusicMinWidth = 660;
    // The widest a page can be on a monitor with this work area: drawn at the scale, the work area's width less 12 DIP each side.
    public static double MaxWidthOn(double workWidth, double scale = 1) => Math.Max(MinWidth, Math.Min(MaxWidth, Math.Floor((workWidth - 24) / scale)));

    public static (double MinWidth, double MaxWidth, double MinHeight, double MaxHeight) Limits(DeckSettings settings, DeckPage page)
        => (page switch { DeckPage.Usage => Math.Max(MinWidth, settings.MinimumWidth), _ => MinWidth },
            MaxWidth, MinHeight, MaxHeight);

    // Every shown page shares one size, so switching pages never changes the panel's shape. The clipboard field holds it;
    // the individual per-page size fields remain for older settings.json files but are no longer written.
    // Sets that size, as dragging an edge or a size field does; the value is kept within the limits.
    public static void SetSize(DeckSettings settings, DeckPage page, double width, double height)
    {
        var (minWidth, maxWidth, minHeight, maxHeight) = Limits(settings, page);
        width = Math.Clamp(width, minWidth, Math.Max(minWidth, maxWidth)); height = Math.Clamp(height, minHeight, maxHeight);
        if (page == DeckPage.Usage) { settings.Width = width; settings.Height = height; return; }
        settings.ClipboardWidth = width; settings.ClipboardHeight = height;
    }

    // All shown pages read the one shared size (see SetSize); only the hidden usage page keeps its own.
    public static (double Width, double Height) Size(DeckSettings settings, DeckPage page) => page == DeckPage.Usage
        ? (Math.Max(settings.Width, settings.MinimumWidth), settings.Height)
        : (settings.ClipboardWidth, settings.ClipboardHeight);

    // The quick-size presets for every shown page: they all set the one shared size, so the same preset lands on the same
    // shape whichever page's size row it is picked from. Standard is the shared size's default; all three keep the volume
    // bar usable (MusicLayout) and the monitor's side process list (MonitorLayout: at least 560 wide).
    public static (double Width, double Height) QuickSize(PanelSize size) => size switch
    {
        PanelSize.Compact => (560, 200), PanelSize.Spacious => (760, 300), _ => (560, 240)
    };

    // The largest width and height among the shown pages (the usage page when none is): the window holds any of them, so
    // switching pages never resizes the window itself.
    public static (double Width, double Height) Largest(DeckSettings settings)
    {
        var pages = Visible(settings) is { Count: > 0 } visible ? visible : [DeckPage.Music];
        return (pages.Max(page => Size(settings, page).Width), pages.Max(page => Size(settings, page).Height));
    }
}

// The scale OrangeIsland is drawn at (DeckSettings.PanelScale), in percent: one of Choices; any other value is the default.
public static class PanelScales
{
    public const int Default = 100;
    public static readonly IReadOnlyList<int> Choices = [100, 150];
    public static int Fit(int percent) => Choices.Contains(percent) ? percent : Default;
    public static double Factor(DeckSettings settings) => Fit(settings.PanelScale) / 100d;
}
