using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;
using Tesseract;

namespace DpiBypass.Core.Network;

public sealed record ScreenCaptureRegion(int X, int Y, int Width, int Height)
{
    // The selector is meant to contain one numeric HUD value. Keeping the upper bound
    // tight also prevents an accidentally selected desktop from being enlarged for OCR.
    public bool IsValid => Width is >= 12 and <= 800 && Height is >= 8 and <= 300;

    public override string ToString() => $"{X},{Y} · {Width}×{Height}";
}

/// <summary>Reads VALORANT's Network RTT number from a user-selected screen rectangle.</summary>
public sealed partial class ValorantHudLatencySource : IGameLatencySource, IDisposable
{
    private const string ValorantProcess = "VALORANT-Win64-Shipping";
    private static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TesseractEngine? _engine;
    private bool _disposed;

    public ScreenCaptureRegion? Region { get; set; }

    public bool CanMeasure(LatencyEndpoint endpoint)
        => Region is { IsValid: true }
        && endpoint.ApplicationProtocol == LatencyProtocol.Udp
        && endpoint.Label.Contains("VALORANT", StringComparison.OrdinalIgnoreCase);

    public async Task<GameLatencySeries> MeasureAsync(
        LatencyEndpoint endpoint,
        LatencyProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Region is not { IsValid: true } region)
        {
            return new GameLatencySeries { Instrument = "VALORANT Network RTT (ekran)", Failure = "Ekran alanı seçilmedi." };
        }

        var gameWindow = FindGameWindow();
        if (gameWindow is null)
        {
            return new GameLatencySeries
            {
                Instrument = "VALORANT Network RTT (ekran)",
                Failure = "VALORANT penceresi bulunamadı.",
            };
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!BringToForeground(gameWindow))
            {
                return new GameLatencySeries
                {
                    Instrument = "VALORANT Network RTT (ekran)",
                    Failure = "Network RTT ölçümü için VALORANT ön plana alınamadı.",
                };
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            if (!IsForeground(gameWindow.ProcessId))
            {
                return new GameLatencySeries
                {
                    Instrument = "VALORANT Network RTT (ekran)",
                    Failure = "Network RTT ölçümü sırasında VALORANT ön planda değil.",
                };
            }

            var engine = GetEngine();
            var samples = new List<double>();
            var frames = Math.Max(1, request.ProbeCount + request.WarmupCount);

            for (var index = 0; index < frames; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = IsForeground(gameWindow.ProcessId)
                    ? ReadFrame(engine, region)
                    : null;
                if (index >= request.WarmupCount && value is { } measured)
                {
                    samples.Add(measured);
                }

                if (index + 1 < frames)
                {
                    await Task.Delay(SamplePeriod, cancellationToken).ConfigureAwait(false);
                }
            }

            return new GameLatencySeries
            {
                Instrument = "VALORANT Network RTT (ekran)",
                FramesRead = request.ProbeCount,
                Samples = samples,
                Failure = samples.Count < Math.Ceiling(request.ProbeCount * 0.80)
                    ? "Network RTT değeri karelerin en az %80'inde okunamadı."
                    : null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GameLatencySeries
            {
                Instrument = "VALORANT Network RTT (ekran)",
                FramesRead = request.ProbeCount,
                Failure = $"Ekran RTT okuması başarısız: {ex.Message}",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Makes the game's pixels visible before the region-selection overlay opens.</summary>
    public static bool TryBringGameToForeground()
    {
        var window = FindGameWindow();
        return window is not null && BringToForeground(window);
    }

    private static GameWindow? FindGameWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        Process[] processes = [];
        try
        {
            processes = Process.GetProcessesByName(ValorantProcess);
            foreach (var process in processes)
            {
                var handle = process.MainWindowHandle;
                if (handle != 0)
                {
                    return new GameWindow(handle, process.Id);
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool BringToForeground(GameWindow window)
    {
        if (IsForeground(window.ProcessId))
        {
            return true;
        }

        _ = ShowWindow(window.Handle, 9); // SW_RESTORE
        return SetForegroundWindow(window.Handle) || IsForeground(window.ProcessId);
    }

    private static bool IsForeground(int processId)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        return foregroundProcessId == (uint)processId;
    }

    private TesseractEngine GetEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        var dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        _engine = new TesseractEngine(dataPath, "eng", EngineMode.LstmOnly);
        _engine.SetVariable("tessedit_char_whitelist", "0123456789");
        _engine.SetVariable("user_defined_dpi", "300");
        return _engine;
    }

    private static double? ReadFrame(TesseractEngine engine, ScreenCaptureRegion region)
    {
        using var captured = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(captured))
        {
            graphics.CopyFromScreen(region.X, region.Y, 0, 0, captured.Size, CopyPixelOperation.SourceCopy);
        }

        const int scale = 4;
        const int padding = 16;
        using var enlarged = new Bitmap(
            (region.Width * scale) + (padding * 2),
            (region.Height * scale) + (padding * 2),
            PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(enlarged))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(
                captured,
                new Rectangle(padding, padding, region.Width * scale, region.Height * scale));
        }

        var value = Recognize(engine, enlarged);
        if (value is not null)
        {
            return value;
        }

        // VALORANT themes and HDR paths can present bright text on a dark translucent
        // panel. A second, inverted pass gives Tesseract dark digits on a light field.
        using var inverted = Invert(enlarged);
        return Recognize(engine, inverted);
    }

    private static double? Recognize(TesseractEngine engine, Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        using var pix = Pix.LoadFromMemory(stream.ToArray());
        using var page = engine.Process(pix, PageSegMode.SingleWord);
        return ParseRtt(page.GetText());
    }

    private static Bitmap Invert(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix(
        [
            [-1, 0, 0, 0, 0],
            [0, -1, 0, 0, 0],
            [0, 0, -1, 0, 0],
            [0, 0, 0, 1, 0],
            [1, 1, 1, 0, 1],
        ]));
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, result.Width, result.Height),
            0,
            0,
            source.Width,
            source.Height,
            GraphicsUnit.Pixel,
            attributes);
        return result;
    }

    internal static double? ParseRtt(string? text)
    {
        var match = Digits().Match(text ?? string.Empty);
        return match.Success
            && double.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is >= 0 and <= 2000
                ? value
                : null;
    }

    [GeneratedRegex(@"\d{1,4}", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    private sealed record GameWindow(nint Handle, int ProcessId);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine?.Dispose();
        _gate.Dispose();
    }
}
