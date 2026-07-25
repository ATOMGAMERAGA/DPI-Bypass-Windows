using System.Drawing;
using System.Windows.Forms;
using AtomDpi.Core;

// System.Windows and System.Windows.Forms both define Application, so WPF's copy is
// referred to by its full name below rather than pulled in with a using.

namespace AtomDpi.App.Infrastructure;

/// <summary>
/// The notification area icon.
/// </summary>
/// <remarks>
/// The icon is loaded at the exact pixel size the shell asks for, taken from the
/// multi-image .ico. Handing the shell a single large bitmap and letting it
/// downscale is what makes tray icons look smeared on high DPI displays, so the
/// right frame is picked instead - and re-picked if the user changes scaling.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _statusItem;
    private Icon? _current;

    public TrayIcon()
    {
        _statusItem = new ToolStripMenuItem("Durum bilinmiyor") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Korumayı başlat");
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();

        var open = new ToolStripMenuItem("Pencereyi aç");
        open.Click += (_, _) => OpenRequested?.Invoke();

        var test = new ToolStripMenuItem("discord.com testi");
        test.Click += (_, _) => TestRequested?.Invoke();

        var exit = new ToolStripMenuItem("Çıkış");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem, new ToolStripSeparator(), open, _toggleItem, test, new ToolStripSeparator(), exit,
        });

        _current = LoadIcon();
        _icon = new NotifyIcon
        {
            Text = AppPaths.ProductName,
            Visible = true,
            ContextMenuStrip = menu,
            Icon = _current,
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;

    public event Action? ToggleRequested;

    public event Action? TestRequested;

    public event Action? ExitRequested;

    public void Update(string headline, string detail, bool isRunning)
    {
        _statusItem.Text = headline;
        _toggleItem.Text = isRunning ? "Korumayı durdur" : "Korumayı başlat";

        // NotifyIcon.Text is capped at 127 characters by the shell.
        var tooltip = $"{AppPaths.ProductName}\n{headline}\n{detail}";
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    public void Notify(string title, string message, bool warning = false)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
            _icon.ShowBalloonTip(5000);
        }
        catch (Exception)
        {
            // Notifications can be disabled by policy; not worth reporting.
        }
    }

    /// <summary>Re-reads the icon at the current DPI. Call after a display change.</summary>
    public void RefreshForDpi()
    {
        var replacement = LoadIcon();
        var previous = _current;

        _icon.Icon = replacement;
        _current = replacement;

        // Only dispose the icon we just replaced, and only once it is off the shell.
        if (!ReferenceEquals(previous, replacement))
        {
            previous?.Dispose();
        }
    }

    private static Icon LoadIcon()
    {
        var size = SystemInformation.SmallIconSize;

        try
        {
            var uri = new Uri("pack://application:,,,/Assets/atomdpi.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                // This overload picks the closest frame in the multi-image file rather
                // than scaling one, which is what keeps the tray icon sharp.
                return new Icon(stream, size);
            }
        }
        catch (Exception)
        {
            // Fall through to the executable's own icon.
        }

        var fallback = Environment.ProcessPath is null ? null : Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        return fallback ?? SystemIcons.Shield;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
