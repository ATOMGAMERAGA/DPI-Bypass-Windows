namespace DpiBypass.Core.Onboarding;

/// <summary>What, if anything, the window shows before the app itself.</summary>
public enum WelcomeKind
{
    /// <summary>Straight to the app.</summary>
    None = 0,

    /// <summary>The brand and one line, then the app. For somebody who has seen the tour.</summary>
    Brief = 1,

    /// <summary>The four-card introduction, with Back, Next and Skip.</summary>
    Tour = 2,
}

/// <summary>Why the app is starting, which is what decides whether a welcome belongs.</summary>
/// <param name="LaunchedByUser">
/// The person opened it: a shortcut, the Start menu, a second launch of an app already
/// running. False for the logon task and for anything else that started it in the
/// background.
/// </param>
/// <param name="StartingMinimised">
/// The window is going straight to the notification area. Nobody is looking at it, so
/// there is nothing for a welcome to greet.
/// </param>
/// <param name="RestoredFromTray">
/// The window is coming back from the notification area inside a session that has already
/// been running. The app is not starting; it is reappearing.
/// </param>
/// <param name="HasSeenTour">Whether the introduction has ever been finished or skipped.</param>
/// <param name="ShowOnStartup">The "Açılışta göster" preference.</param>
public readonly record struct WelcomeContext(
    bool LaunchedByUser,
    bool StartingMinimised,
    bool RestoredFromTray,
    bool HasSeenTour,
    bool ShowOnStartup);

/// <summary>One card of the introduction.</summary>
/// <param name="Title">The heading, e.g. "Bağlan'a bas."</param>
/// <param name="Body">One sentence under it. Never a paragraph, never a manual.</param>
/// <param name="Symbol">The Fluent glyph beside it.</param>
public sealed record WelcomeCard(string Title, string Body, string Symbol);

/// <summary>
/// Decides whether the window greets the user, and with what.
/// </summary>
/// <remarks>
/// <para>
/// The rule is that a welcome answers an arrival. Somebody who double-clicked the
/// shortcut has arrived; the logon task starting the app into the notification area has
/// not, and neither has a window coming back from the tray in a session that has been
/// running since this morning. Getting that wrong is not a cosmetic problem: a greeting
/// that appears every time the window is restored turns the app's own front door into
/// something to get past.
/// </para>
/// <para>
/// The copy is short on purpose. Four cards, one sentence each, in plain Turkish, and no
/// claim the app does not deliver - no VPN, no anonymity, no encryption of the user's
/// traffic. What it does is find settings that work on this network, and that is what it
/// says.
/// </para>
/// </remarks>
public static class WelcomeFlow
{
    /// <summary>The introduction, in order.</summary>
    public static IReadOnlyList<WelcomeCard> Cards { get; } =
    [
        new(
            "DPI Bypass'a hoş geldin.",
            "Bağlantı sorunlarını gidermek için ağında çalışan ayarları bulmana yardımcı olur.",
            "Sparkle"),
        new(
            "Bağlan'a bas.",
            "Uygulama bağlantını kontrol eder ve uygun profili seçer.",
            "Power"),
        new(
            "Sonucu takip et.",
            "Gecikme tepki süresidir. Hız, verinin ne kadar hızlı aktarıldığını gösterir.",
            "TopSpeed"),
        new(
            "Kontrol sende.",
            "İstediğin zaman bağlantıyı kesebilir veya ayarları değiştirebilirsin.",
            "Options"),
    ];

    /// <summary>The line the brief greeting shows instead of the tour.</summary>
    public const string BriefLine = "Bağlantını hazırlıyoruz.";

    public static WelcomeKind Decide(WelcomeContext context)
    {
        // Coming back from the notification area is not an arrival, whatever else is true.
        if (context.RestoredFromTray)
        {
            return WelcomeKind.None;
        }

        // Neither is a start nobody asked for, or one that goes straight to the tray.
        if (!context.LaunchedByUser || context.StartingMinimised)
        {
            return WelcomeKind.None;
        }

        // The first time is the only time the introduction is unconditional. Somebody who
        // has never seen it has no way to ask for it, and the preference they would use to
        // turn it off is one of the things it is there to tell them about.
        if (!context.HasSeenTour)
        {
            return WelcomeKind.Tour;
        }

        return context.ShowOnStartup ? WelcomeKind.Brief : WelcomeKind.None;
    }
}
