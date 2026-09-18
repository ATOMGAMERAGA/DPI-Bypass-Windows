using System.Collections.ObjectModel;
using DpiBypass.Core;
using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Network;
using DpiBypass.Core.Network.Latency;

namespace DpiBypass.App.ViewModels;

/// <summary>
/// The four facts the main screen shows, and the wording that keeps each of them honest.
/// </summary>
/// <remarks>
/// <para>
/// The main screen answers one question - "is this working, and is it any good?" - and
/// anything that does not help answer it belongs on a page behind the rail. What is left
/// is the active profile, the latency, the stability and the traffic going through the
/// link right now.
/// </para>
/// <para>
/// Every one of them carries the kind of measurement it is, because the four numbers are
/// easy to misread in exactly the same way: as promises. Latency says which target and
/// which protocol it came from, so a TLS handshake time is never mistaken for a game's
/// ping. Traffic says it is what is flowing at this moment, so it is never mistaken for
/// the line's capacity - the number people quote when they say an app "measured their
/// speed" after it watched a single download. And anything that has not been measured
/// says "Ölçülmedi" rather than showing a zero, a dash, or the last value from a
/// different network.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    private const string NotMeasured = "Ölçülmedi";

    private readonly NetworkLoadSampler _trafficSampler = new();
    private NetworkCounters? _lastTrafficCounters;
    private string _lastTrafficAdapter = string.Empty;

    private string _trafficValue = NotMeasured;
    private string _trafficNote = "Bağlantı açıldığında ölçülür.";

    /// <summary>Which bypass profile is installed on the engine right now.</summary>
    public string ActiveProfileValue => _service.State == ProtectionState.Stopped
        ? NotMeasured
        : _service.Strategy.Name;

    /// <summary>How that profile came to be chosen, and when it was last checked.</summary>
    public string ActiveProfileNote
    {
        get
        {
            if (_service.State == ProtectionState.Stopped)
            {
                return "Koruma kapalıyken profil seçilmez.";
            }

            if (_service.Settings.ManualStrategyId is { Length: > 0 })
            {
                return "Elle seçildi.";
            }

            return _service.LastSelection is null
                ? "Bu ağda kayıtlı profil doğrulanarak kullanıldı."
                : $"Ölçülerek seçildi · {SelectionAge}";
        }
    }

    /// <summary>
    /// Everything about the automatic choice a user might want to check.
    /// </summary>
    /// <remarks>
    /// The four things that make a claim checkable rather than reassuring: what was
    /// chosen, why, when it was measured, and how much was measured. The last of these is
    /// the one most easily left out, and it is the one that matters most - a choice made
    /// from reachability and connect times is a real choice, and it is not the same thing
    /// as a choice made after measuring the link's throughput. The interface says which.
    /// </remarks>
    public string SelectionRationale => _service.LastSelection?.Reason
        ?? "Bu ağda henüz otomatik ölçüm yapılmadı.";

    public string SelectionMeasuredAt => _service.LastSelection is null
        ? "Ölçülmedi"
        : SelectionAge;

    /// <summary>What was measured, in the user's words. Never implies a test that did not run.</summary>
    public string SelectionConfidenceLabel => _service.LastSelection?.Confidence switch
    {
        SelectionConfidence.WithThroughput =>
            "Doğrulama düzeyi: erişim, kararlılık, gecikme ve sürdürülebilir hız ölçüldü.",
        SelectionConfidence.LatencyOnly =>
            "Doğrulama düzeyi: erişim, kararlılık ve gecikme ölçüldü · hız testi yapılmadı.",
        SelectionConfidence.Insufficient =>
            "Doğrulama düzeyi: yeterli ölçüm alınamadı; mevcut ayar korundu.",
        _ => "Doğrulama düzeyi: ölçüm yapılmadı.",
    };

    /// <summary>The candidates that were measured and rejected, with the reason for each.</summary>
    public ObservableCollection<StrategyRejection> SelectionRejections { get; } = [];

    private string SelectionAge
    {
        get
        {
            if (_service.LastSelection is not { } selection)
            {
                return "Ölçülmedi";
            }

            var age = DateTimeOffset.UtcNow - selection.MeasuredAt;

            return age switch
            {
                { TotalSeconds: < 60 } => "az önce ölçüldü",
                { TotalMinutes: < 60 } => $"{age.TotalMinutes:F0} dk önce ölçüldü",
                { TotalHours: < 24 } => $"{age.TotalHours:F0} sa önce ölçüldü",
                _ => $"{age.TotalDays:F0} gün önce ölçüldü",
            };
        }
    }

    /// <summary>The idle latency, with a unit, or the fact that nobody measured one.</summary>
    public string LatencyValue
        => _service.LatencyStatus?.Idle is { } idle ? $"{idle.MedianRttMs:F0} ms" : NotMeasured;

    /// <summary>
    /// What that latency actually is. Never just "ping".
    /// </summary>
    /// <remarks>
    /// The reference target is a well-connected public host, which is a fine control for
    /// "did this change help" and a poor stand-in for the round trip to a game server on
    /// the other side of the country. Saying so is the difference between a number and a
    /// claim.
    /// </remarks>
    public string LatencyNote
    {
        get
        {
            var status = _service.LatencyStatus;
            if (status?.Idle is null)
            {
                return "Ping düşürme sayfasından ölçebilirsiniz.";
            }

            var target = string.IsNullOrWhiteSpace(status.Target) ? "hedef" : status.Target;
            var protocol = string.IsNullOrWhiteSpace(status.Protocol) ? string.Empty : $" · {status.Protocol}";
            var reference = status.RouteReferenceOnly ? " · genel internet referansı, oyun sunucusu değil" : string.Empty;

            return $"{target}{protocol}{reference}";
        }
    }

    /// <summary>
    /// Stability: the jitter, and the loss when it could be measured.
    /// </summary>
    /// <remarks>
    /// Jitter is what a voice call and a game actually notice, far more than the median,
    /// and it is the number a screen showing only "ping" hides. Loss is reported
    /// separately and only when it was genuinely measured - a failed HTTP request is not
    /// a lost packet, and counting it as one is how an app invents a network problem.
    /// </remarks>
    public string StabilityValue
    {
        get
        {
            var idle = _service.LatencyStatus?.Idle;
            if (idle is null)
            {
                return NotMeasured;
            }

            return idle.PacketLossPercent is { } loss
                ? $"±{idle.JitterMs:F0} ms · %{loss:F1} kayıp"
                : $"±{idle.JitterMs:F0} ms";
        }
    }

    public string StabilityNote
    {
        get
        {
            var idle = _service.LatencyStatus?.Idle;
            if (idle is null)
            {
                return "Gecikmeyle birlikte ölçülür.";
            }

            return idle.PacketLossPercent is null
                ? "Gecikme dalgalanması. Paket kaybı bu yöntemle ölçülemedi."
                : "Gecikme dalgalanması ve paket kaybı.";
        }
    }

    /// <summary>What is flowing through the adapter at this moment. Not the line's capacity.</summary>
    public string TrafficValue => _trafficValue;

    public string TrafficNote => _trafficNote;

    /// <summary>
    /// Re-reads the adapter's byte counters and turns two readings into a rate.
    /// </summary>
    /// <remarks>
    /// Driven by the presentation timer, which only runs while the window is on screen, so
    /// an app sitting in the notification area is not enumerating network interfaces every
    /// two seconds for a label nobody can see. The first reading of a session, and the
    /// first after the machine changes network, produces no rate at all rather than a
    /// rate computed against a counter from a different adapter.
    /// </remarks>
    private void RefreshTraffic()
    {
        var network = _service.Network;
        var adapter = network.AdapterId ?? string.Empty;

        if (adapter.Length == 0)
        {
            _lastTrafficCounters = null;
            _lastTrafficAdapter = string.Empty;
            SetTraffic(NotMeasured, "Ağ bağdaştırıcısı belirlenemedi.");
            return;
        }

        var current = _trafficSampler.Read(network);
        if (current is null)
        {
            _lastTrafficCounters = null;
            SetTraffic(NotMeasured, "Bağdaştırıcı sayaçları okunamadı.");
            return;
        }

        if (!string.Equals(adapter, _lastTrafficAdapter, StringComparison.OrdinalIgnoreCase))
        {
            // A counter from the adapter the machine was on a moment ago and a counter
            // from the one it is on now do not describe one window of time.
            _lastTrafficAdapter = adapter;
            _lastTrafficCounters = current;
            SetTraffic(NotMeasured, "Ölçüm penceresi bekleniyor.");
            return;
        }

        var sample = NetworkLoadSample.Between(_lastTrafficCounters, current);
        _lastTrafficCounters = current;

        if (sample.State == LatencyLoadState.Unknown)
        {
            SetTraffic(NotMeasured, "Ölçüm penceresi bekleniyor.");
            return;
        }

        SetTraffic(
            $"↓ {Rate(sample.DownlinkKbps)} · ↑ {Rate(sample.UplinkKbps)}",
            "Şu anda akan trafik. Hattın azami hızı değildir.");
    }

    private void SetTraffic(string value, string note)
    {
        if (_trafficValue != value)
        {
            _trafficValue = value;
            Raise(nameof(TrafficValue));
        }

        if (_trafficNote != note)
        {
            _trafficNote = note;
            Raise(nameof(TrafficNote));
        }
    }

    private static string Rate(double kbps) => kbps >= 1000
        ? $"{kbps / 1000:F1} Mb/sn"
        : $"{kbps:F0} kb/sn";

    /// <summary>Re-raises the four facts. Called whenever the engine or a measurement moves.</summary>
    private void RefreshOverview()
    {
        Raise(nameof(ActiveProfileValue));
        Raise(nameof(ActiveProfileNote));
        Raise(nameof(LatencyValue));
        Raise(nameof(LatencyNote));
        Raise(nameof(StabilityValue));
        Raise(nameof(StabilityNote));
        Raise(nameof(SelectionRationale));
        Raise(nameof(SelectionMeasuredAt));
        Raise(nameof(SelectionConfidenceLabel));

        RefreshSelectionRejections();
    }

    /// <summary>
    /// Mirrors the rejected candidates into the bound collection, in place.
    /// </summary>
    /// <remarks>
    /// Rebuilt only when the list has actually changed. This runs on every service
    /// notification, and clearing an ObservableCollection the interface is bound to
    /// destroys and rebuilds every row - which on the settings page is a visible flicker
    /// for a list that usually has not moved.
    /// </remarks>
    private void RefreshSelectionRejections()
    {
        var current = _service.LastSelection?.Eliminated ?? [];

        if (SelectionRejections.SequenceEqual(current))
        {
            return;
        }

        SelectionRejections.Clear();
        foreach (var rejection in current)
        {
            SelectionRejections.Add(rejection);
        }
    }
}
