namespace DpiBypass.Core.Vodafone;

public sealed class TtlFixException : Exception
{
    public TtlFixException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Tuning knobs for <see cref="HotspotTtlFix"/>.</summary>
public sealed record TtlFixSettings
{
    /// <summary>
    /// Packets at or below this TTL are never touched.
    /// </summary>
    /// <remarks>
    /// This has to stay above the highest TTL any decoy strategy emits, or the fix
    /// would rewrite the very packets whose early expiry makes the bypass work.
    /// <c>TtlFixGuardTests</c> fails the build if a new strategy ever crosses it.
    /// </remarks>
    public const int DefaultGuard = 32;

    /// <summary>
    /// 65 leaves exactly 64 at the operator after the phone routes the packet once,
    /// which is what its own traffic looks like.
    /// </summary>
    public const byte DefaultTimeToLive = 65;

    /// <summary>How many networks the mode may be remembered for.</summary>
    public const int MaxNetworks = 10;

    public static readonly TtlFixSettings Default = new();

    public byte TimeToLive { get; init; } = DefaultTimeToLive;

    public int Guard { get; init; } = DefaultGuard;

    /// <summary>Drop outbound IPv6 on the shared adapter so the operator sees one source.</summary>
    public bool DropIPv6 { get; init; } = true;

    public void Validate()
    {
        if (Guard is < 1 or > 254)
        {
            throw new TtlFixException($"Koruma eşiği 1-254 aralığında olmalı (verilen: {Guard}).");
        }

        if (TimeToLive <= Guard)
        {
            throw new TtlFixException(
                $"TTL {Guard + 1}-255 aralığında olmalı (verilen: {TimeToLive}). "
                    + "Koruma eşiğinin altındaki bir değer atlatma yöntemlerini bozar.");
        }
    }
}

/// <summary>A network the user switched the mode on for.</summary>
public sealed record TtlFixNetwork
{
    public required string Key { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string AdapterName { get; init; } = string.Empty;

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}
