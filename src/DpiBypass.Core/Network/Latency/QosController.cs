using System.Globalization;
using System.Text.Json;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Network;

/// <summary>Whether policy-based QoS can be used here, and what is already configured.</summary>
public sealed record QosCapability
{
    public static readonly QosCapability Unavailable = new()
    {
        Available = false,
        Reason = "Windows QoS ilke modülü bu sistemde kullanılamıyor.",
    };

    public bool Available { get; init; }

    public string? Reason { get; init; }

    /// <summary>Policies this application did not create. Never touched, only counted.</summary>
    public IReadOnlyList<string> ForeignPolicies { get; init; } = [];

    /// <summary>Leftovers from this application, which recovery is allowed to remove.</summary>
    public IReadOnlyList<string> OwnedPolicies { get; init; } = [];

    /// <summary>Foreign policies that would compete with a send-rate limit of ours.</summary>
    public IReadOnlyList<string> CompetingPolicies { get; init; } = [];

    /// <summary>
    /// A policy someone else owns that would compete with ours.
    /// </summary>
    /// <remarks>
    /// Domain-managed machines commonly ship QoS through Group Policy. Adding a policy
    /// beside one is not safe to reason about - the most specific match wins, and which
    /// that is depends on rules we did not write - so a conflict means standing down and
    /// telling the user, never guessing.
    /// </remarks>
    public bool HasForeignPolicies => ForeignPolicies.Count > 0;

    /// <summary>True when standing down is the right answer rather than adding a policy.</summary>
    public bool HasConflict => CompetingPolicies.Count > 0;
}

/// <summary>One policy this application wants Windows to create.</summary>
public sealed record QosPolicyRequest
{
    public required string Name { get; init; }

    /// <summary>Executable name or full path the policy matches, as Windows runs it.</summary>
    public string? AppPathName { get; init; }

    public LatencyProtocol? Protocol { get; init; }

    /// <summary>Destination address or prefix the policy matches.</summary>
    public string? DestinationPrefix { get; init; }

    public int? DestinationPort { get; init; }

    /// <summary>Outbound rate ceiling. This is the part that actually paces traffic.</summary>
    public ulong? ThrottleBitsPerSecond { get; init; }

    /// <summary>
    /// DSCP marking, which only does anything if the router classifies on it.
    /// </summary>
    /// <remarks>
    /// Marking is free to apply and impossible to verify from this end: the queueing it
    /// asks for happens in somebody else's equipment. It is therefore never counted as a
    /// gain on its own - only a measured reduction in loaded latency is.
    /// </remarks>
    public int? Dscp { get; init; }

    /// <summary>
    /// Where Windows keeps the policy. ActiveStore is deliberate: it does not survive a
    /// reboot, so a machine that crashes with a policy in place comes back clean.
    /// </summary>
    public string PolicyStore { get; init; } = QosPolicyStores.Active;

    public int Precedence { get; init; } = 127;

    public string DescribeMatch()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(AppPathName))
        {
            parts.Add($"uygulama={AppPathName}");
        }

        if (Protocol is { } protocol)
        {
            parts.Add($"protokol={protocol.ToLabel()}");
        }

        if (!string.IsNullOrWhiteSpace(DestinationPrefix))
        {
            parts.Add($"hedef={DestinationPrefix}");
        }

        if (DestinationPort is { } port)
        {
            parts.Add($"port={port.ToString(CultureInfo.InvariantCulture)}");
        }

        return parts.Count == 0 ? "(eşleşme yok)" : string.Join(" · ", parts);
    }
}

public static class QosPolicyStores
{
    public const string Active = "ActiveStore";
}

/// <summary>
/// What Windows was asked for, and what it actually stored.
/// </summary>
/// <remarks>
/// <see cref="Verified"/> is the point. An earlier build accepted a policy as created
/// because a policy of that name existed afterwards, which proves the name and nothing
/// else - not the throttle rate, not the application it matches, not the store it landed
/// in. Every condition and every action is now read back and compared, and a mismatch is
/// a failure rather than a policy quietly doing something else.
/// </remarks>
public sealed record QosApplyResult(bool Created, string? Reason = null, QosPolicyInfo? Verified = null);

/// <summary>A policy Windows reports, whoever created it.</summary>
public sealed record QosPolicyInfo
{
    public required string Name { get; init; }

    public string Owner { get; init; } = string.Empty;

    public string PolicyStore { get; init; } = string.Empty;

    public ulong? ThrottleRateBitsPerSecond { get; init; }

    public int? Dscp { get; init; }

    public string? AppPathName { get; init; }

    public string? Protocol { get; init; }

    public string? DestinationPrefix { get; init; }

    public int? DestinationPort { get; init; }

    public int? Precedence { get; init; }

    /// <summary>Everything about the stored policy, for the report and the log.</summary>
    public string Describe()
    {
        var parts = new List<string> { $"ad={Name}", $"depo={PolicyStore}" };

        if (!string.IsNullOrWhiteSpace(AppPathName))
        {
            parts.Add($"uygulama={AppPathName}");
        }

        if (ThrottleRateBitsPerSecond is { } throttle)
        {
            parts.Add($"sınır={throttle / 1_000_000d:F2} Mbit/s");
        }

        if (Precedence is { } precedence)
        {
            parts.Add($"öncelik={precedence}");
        }

        return string.Join(" · ", parts);
    }
}

public interface IQosController
{
    Task<QosCapability> DetectAsync(CancellationToken cancellationToken = default);

    Task<QosApplyResult> CreateAsync(QosPolicyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes one policy. Refuses any name this application does not own.</summary>
    Task<LatencyRestoreOutcome> RemoveAsync(
        string name,
        string policyStore,
        CancellationToken cancellationToken = default);

    /// <summary>Removes every leftover policy this application created, and nothing else.</summary>
    Task<int> RemoveAllOwnedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates and removes policy-based QoS rules, and never touches anybody else's.
/// </summary>
/// <remarks>
/// <para>
/// Windows applies a QoS policy per transport-layer endpoint: the inspection module
/// matches new connections against the stored policies and hands Pacer.sys a flow
/// carrying the DSCP value and the throttle rate, and Pacer.sys then schedules the
/// packets of that flow. The throttle action applies to outbound traffic, which is
/// exactly the half of a home connection this machine can do anything about.
/// </para>
/// <para>
/// Every policy this application creates is named <c>DPIBypass.Latency.*</c>, and the
/// removal path refuses any other name outright. That is the whole safety story: a user
/// or an administrator can have as many QoS policies as they like and none of them can
/// be modified, renamed or deleted from here.
/// </para>
/// </remarks>
public sealed class WindowsQosController : IQosController
{
    /// <summary>Every policy this application creates starts with this, and no other does.</summary>
    public const string PolicyNamePrefix = "DPIBypass.Latency.";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly Action<string>? _log;

    public WindowsQosController(Action<string>? log = null) => _log = log;

    /// <summary>Whether a name belongs to this application. The only thing that may be removed.</summary>
    public static bool IsOwnedName(string? name) =>
        name is not null && name.StartsWith(PolicyNamePrefix, StringComparison.Ordinal);

    /// <summary>Builds a policy name for one profile. Deliberately the only way to make one.</summary>
    public static string NameFor(string profileId, string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        // Windows policy names are free text; ours are restricted to characters that
        // cannot be confused with a wildcard or a path so a name is never a pattern.
        var safeProfile = new string([.. profileId.Where(char.IsAsciiLetterOrDigit)]);
        var safeRole = new string([.. role.Where(char.IsAsciiLetterOrDigit)]);

        return $"{PolicyNamePrefix}{safeRole}.{safeProfile}";
    }

    public async Task<QosCapability> DetectAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return QosCapability.Unavailable;
        }

        var result = await ProcessRunner
            .PowerShellWithEnvironmentAsync(ListScript, Environment(), TimeSpan.FromSeconds(25), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            _log?.Invoke($"latency.qos: ilke listesi okunamadı ({Describe(result)}).");
            return QosCapability.Unavailable with { Reason = Describe(result) };
        }

        var listed = Deserialize<ListDto>(result.StandardOutput);
        if (listed is null || !listed.Available)
        {
            return QosCapability.Unavailable with
            {
                Reason = listed?.Reason ?? "Windows QoS ilke modülü yanıt vermedi.",
            };
        }

        var owned = listed.Policies.Where(policy => IsOwnedName(policy.Name)).Select(policy => policy.Name!).ToArray();
        var foreign = listed.Policies.Where(policy => !IsOwnedName(policy.Name)).ToArray();

        // A foreign policy only competes when it does the same job: a catch-all default,
        // or another send-rate limit. Someone else's DSCP marking for their VoIP handset
        // is not a reason to refuse to pace a game update.
        var competing = foreign
            .Where(policy => policy.IsDefault || policy.ThrottleRateBitsPerSecond > 0)
            .Select(policy => policy.Name!)
            .ToArray();

        return new QosCapability
        {
            Available = true,
            OwnedPolicies = owned,
            ForeignPolicies = [.. foreign.Select(policy => policy.Name!)],
            CompetingPolicies = competing,
        };
    }

    public async Task<QosApplyResult> CreateAsync(
        QosPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsOwnedName(request.Name))
        {
            throw new InvalidOperationException(
                $"'{request.Name}' bu uygulamanın ilke ad alanında değil; oluşturulmayacak.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new QosApplyResult(false, "Bu platformda Windows QoS ilkesi oluşturulamaz.");
        }

        var environment = Environment();
        environment["DPI_BYPASS_QOS_NAME"] = request.Name;
        environment["DPI_BYPASS_QOS_STORE"] = request.PolicyStore;
        environment["DPI_BYPASS_QOS_APP"] = request.AppPathName;
        environment["DPI_BYPASS_QOS_PROTOCOL"] = request.Protocol?.ToLabel();
        environment["DPI_BYPASS_QOS_DST"] = request.DestinationPrefix;
        environment["DPI_BYPASS_QOS_DSTPORT"] = request.DestinationPort?.ToString(CultureInfo.InvariantCulture);
        environment["DPI_BYPASS_QOS_THROTTLE"] = request.ThrottleBitsPerSecond?.ToString(CultureInfo.InvariantCulture);
        environment["DPI_BYPASS_QOS_DSCP"] = request.Dscp?.ToString(CultureInfo.InvariantCulture);
        environment["DPI_BYPASS_QOS_PRECEDENCE"] = request.Precedence.ToString(CultureInfo.InvariantCulture);

        var result = await ProcessRunner
            .PowerShellWithEnvironmentAsync(CreateScript, environment, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return new QosApplyResult(false, Describe(result));
        }

        var dto = Deserialize<CreateDto>(result.StandardOutput);
        if (dto is null)
        {
            return new QosApplyResult(false, "Windows geçerli bir ilke sonucu döndürmedi.");
        }

        var verified = dto.Policy is null ? null : ToInfo(dto.Policy);
        if (!dto.Created)
        {
            return new QosApplyResult(false, dto.Reason, verified);
        }

        // The script compares field by field, but the answer is checked again here so the
        // rule lives in C# too: a policy that does not match what was asked for is not a
        // policy that was created, whatever the store says is in it.
        var mismatch = DescribeMismatch(request, verified);
        if (mismatch is not null)
        {
            _log?.Invoke($"latency.qos: '{request.Name}' beklenenden farklı yazıldı ({mismatch}).");
            return new QosApplyResult(false, mismatch, verified);
        }

        return new QosApplyResult(true, null, verified);
    }

    /// <summary>
    /// Every condition and action compared against what was asked for.
    /// </summary>
    /// <returns>Null when the stored policy is exactly the requested one.</returns>
    internal static string? DescribeMismatch(QosPolicyRequest request, QosPolicyInfo? stored)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (stored is null)
        {
            return "ilke oluşturuldu ancak depoda görünmüyor";
        }

        if (!string.Equals(stored.Name, request.Name, StringComparison.Ordinal))
        {
            return $"ilke adı '{stored.Name}', beklenen '{request.Name}'";
        }

        if (!string.Equals(stored.PolicyStore, request.PolicyStore, StringComparison.OrdinalIgnoreCase))
        {
            return $"ilke '{stored.PolicyStore}' deposunda, beklenen '{request.PolicyStore}'";
        }

        if (!Matches(request.AppPathName, stored.AppPathName))
        {
            return $"uygulama eşleşmesi '{stored.AppPathName ?? "(yok)"}', beklenen '{request.AppPathName ?? "(yok)"}'";
        }

        if (!Matches(request.Protocol?.ToLabel(), stored.Protocol))
        {
            return $"protokol koşulu '{stored.Protocol ?? "(yok)"}', beklenen '{request.Protocol?.ToLabel() ?? "(yok)"}'";
        }

        if (!Matches(request.DestinationPrefix, stored.DestinationPrefix))
        {
            return $"hedef öneki '{stored.DestinationPrefix ?? "(yok)"}', beklenen '{request.DestinationPrefix ?? "(yok)"}'";
        }

        if (request.DestinationPort != stored.DestinationPort)
        {
            return $"hedef portu {stored.DestinationPort?.ToString(CultureInfo.InvariantCulture) ?? "(yok)"}, "
                + $"beklenen {request.DestinationPort?.ToString(CultureInfo.InvariantCulture) ?? "(yok)"}";
        }

        if (request.ThrottleBitsPerSecond != stored.ThrottleRateBitsPerSecond)
        {
            return $"hız sınırı {stored.ThrottleRateBitsPerSecond?.ToString(CultureInfo.InvariantCulture) ?? "(yok)"} bit/s, "
                + $"beklenen {request.ThrottleBitsPerSecond?.ToString(CultureInfo.InvariantCulture) ?? "(yok)"} bit/s";
        }

        if (request.Dscp != stored.Dscp)
        {
            return $"DSCP {stored.Dscp?.ToString(CultureInfo.InvariantCulture) ?? "(yok)"}, "
                + $"beklenen {request.Dscp?.ToString(CultureInfo.InvariantCulture) ?? "(yok)"}";
        }

        if (stored.Precedence is { } precedence && precedence != request.Precedence)
        {
            return $"öncelik {precedence}, beklenen {request.Precedence}";
        }

        // Ownership last, because a policy carrying somebody else's owner with our name is
        // the one case where the right answer is to touch nothing at all.
        if (!IsOwnedName(stored.Name))
        {
            return "ilke bu uygulamanın ad alanında değil";
        }

        return null;

        static bool Matches(string? wanted, string? stored)
        {
            var left = string.IsNullOrWhiteSpace(wanted) ? null : wanted.Trim();
            var right = string.IsNullOrWhiteSpace(stored) ? null : stored.Trim();
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static QosPolicyInfo ToInfo(PolicyDto dto) => new()
    {
        Name = dto.Name ?? string.Empty,
        Owner = dto.Owner ?? string.Empty,
        PolicyStore = dto.PolicyStore ?? string.Empty,
        AppPathName = dto.AppPathName,
        Protocol = dto.IPProtocol,
        DestinationPrefix = dto.IPDstPrefix,
        DestinationPort = dto.IPDstPort == 0 ? null : dto.IPDstPort,
        ThrottleRateBitsPerSecond = dto.ThrottleRateBitsPerSecond == 0 ? null : dto.ThrottleRateBitsPerSecond,
        Dscp = dto.DscpValue,
        Precedence = dto.Precedence,
    };

    public async Task<LatencyRestoreOutcome> RemoveAsync(
        string name,
        string policyStore,
        CancellationToken cancellationToken = default)
    {
        if (!IsOwnedName(name))
        {
            // Never a silent no-op: being asked to delete somebody else's policy means
            // something upstream is wrong, and it must not be papered over.
            throw new InvalidOperationException($"'{name}' bu uygulamaya ait değil; silinmeyecek.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return LatencyRestoreOutcome.MissingProperty;
        }

        var environment = Environment();
        environment["DPI_BYPASS_QOS_NAME"] = name;
        environment["DPI_BYPASS_QOS_STORE"] = string.IsNullOrWhiteSpace(policyStore)
            ? QosPolicyStores.Active
            : policyStore;

        var result = await ProcessRunner
            .PowerShellWithEnvironmentAsync(RemoveScript, environment, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            _log?.Invoke($"latency.qos: '{name}' kaldırılamadı ({Describe(result)}).");
            return LatencyRestoreOutcome.Failed;
        }

        var dto = Deserialize<RemoveDto>(result.StandardOutput);
        if (dto is null || !Enum.TryParse<LatencyRestoreOutcome>(dto.Outcome, ignoreCase: true, out var outcome))
        {
            return LatencyRestoreOutcome.Failed;
        }

        return outcome;
    }

    public async Task<int> RemoveAllOwnedAsync(CancellationToken cancellationToken = default)
    {
        var capability = await DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.Available)
        {
            return 0;
        }

        var removed = 0;
        foreach (var name in capability.OwnedPolicies)
        {
            var outcome = await RemoveAsync(name, QosPolicyStores.Active, cancellationToken).ConfigureAwait(false);
            if (outcome is LatencyRestoreOutcome.Restored or LatencyRestoreOutcome.MissingProperty)
            {
                removed++;
            }
        }

        return removed;
    }

    private static Dictionary<string, string?> Environment() => new()
    {
        ["DPI_BYPASS_QOS_PREFIX"] = PolicyNamePrefix,
    };

    private static T? Deserialize<T>(string json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json.Trim(), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Describe(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(text) ? $"PowerShell çıkış kodu {result.ExitCode}" : text.Trim();
    }

    private sealed record ListDto
    {
        public bool Available { get; init; }
        public string? Reason { get; init; }
        public List<PolicyDto> Policies { get; init; } = [];
    }

    private sealed record PolicyDto
    {
        public string? Name { get; init; }
        public string? Owner { get; init; }
        public string? PolicyStore { get; init; }
        public string? AppPathName { get; init; }
        public string? IPProtocol { get; init; }
        public string? IPDstPrefix { get; init; }
        public int IPDstPort { get; init; }
        public ulong ThrottleRateBitsPerSecond { get; init; }
        public int? DscpValue { get; init; }
        public int? Precedence { get; init; }
        public bool IsDefault { get; init; }
    }

    private sealed record CreateDto
    {
        public bool Created { get; init; }
        public string? Reason { get; init; }
        public PolicyDto? Policy { get; init; }
    }

    private sealed record RemoveDto
    {
        public string? Outcome { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Flattens one policy into the shape both the list and the read-back compare.
    /// </summary>
    /// <remarks>
    /// Shared so a policy is described identically wherever it is read: a read-back that
    /// projected fields differently from the listing would compare two shapes and call the
    /// difference a mismatch.
    /// </remarks>
    private const string PolicyProjection = """
        function ConvertTo-DpiPolicy($policy, [string]$store) {
            $throttle = 0
            if ($null -ne $policy.ThrottleRateAction) {
                try { $throttle = [uint64]$policy.ThrottleRateAction } catch { $throttle = 0 }
            }

            $port = 0
            if ($null -ne $policy.IPDstPortStart -and $null -ne $policy.IPDstPortEnd `
                    -and [int]$policy.IPDstPortStart -eq [int]$policy.IPDstPortEnd) {
                $port = [int]$policy.IPDstPortStart
            }

            $dscp = $null
            if ($null -ne $policy.DSCPValue) {
                try { $dscp = [int]$policy.DSCPValue } catch { $dscp = $null }
            }

            $precedence = $null
            if ($null -ne $policy.Precedence) {
                try { $precedence = [int]$policy.Precedence } catch { $precedence = $null }
            }

            $protocol = [string]$policy.IPProtocol
            # Windows stores "no protocol condition" as Both or None depending on version;
            # neither is a condition, and both must compare equal to "not specified".
            if ($protocol -eq 'Both' -or $protocol -eq 'None') { $protocol = '' }

            [pscustomobject]@{
                Name = [string]$policy.Name
                Owner = [string]$policy.Owner
                PolicyStore = $store
                AppPathName = [string]$policy.AppPathName
                IPProtocol = $protocol
                IPDstPrefix = [string]$policy.IPDstPrefix
                IPDstPort = $port
                ThrottleRateBitsPerSecond = $throttle
                DscpValue = $dscp
                Precedence = $precedence
                IsDefault = ([string]$policy.Template -eq 'Default')
            }
        }
        """;

    private const string ListScript = PolicyProjection + """

        $ErrorActionPreference = 'Stop'

        try {
            if (-not (Get-Command -Name 'Get-NetQosPolicy' -ErrorAction SilentlyContinue)) {
                [pscustomobject]@{ Available = $false; Reason = 'NetQos modülü bulunamadı.'; Policies = @() } |
                    ConvertTo-Json -Depth 4 -Compress
                return
            }

            # Both stores are read: a policy the machine persisted and one created for
            # this session alone both matter when deciding whether ours would collide.
            $all = @()
            foreach ($store in @('ActiveStore', 'localhost')) {
                try {
                    foreach ($policy in @(Get-NetQosPolicy -PolicyStore $store -ErrorAction Stop)) {
                        $all += [pscustomobject]@{ Store = $store; Policy = $policy }
                    }
                } catch { }
            }

            $policies = @($all |
                Where-Object { $null -ne $_.Policy -and $_.Policy.Name } |
                Group-Object -Property { [string]$_.Policy.Name } |
                ForEach-Object {
                    $entry = $_.Group | Select-Object -First 1
                    ConvertTo-DpiPolicy $entry.Policy $entry.Store
                })

            [pscustomobject]@{ Available = $true; Reason = $null; Policies = $policies } |
                ConvertTo-Json -Depth 4 -Compress
        } catch {
            [pscustomobject]@{ Available = $false; Reason = $_.Exception.Message; Policies = @() } |
                ConvertTo-Json -Depth 4 -Compress
        }
        """;

    private const string CreateScript = PolicyProjection + """

        $ErrorActionPreference = 'Stop'

        function Result([bool]$created, [string]$reason, $policy) {
            [pscustomobject]@{ Created = $created; Reason = $reason; Policy = $policy } |
                ConvertTo-Json -Depth 4 -Compress
        }

        try {
            $name = [string]$env:DPI_BYPASS_QOS_NAME
            $prefix = [string]$env:DPI_BYPASS_QOS_PREFIX

            # The guard exists on both sides of the boundary on purpose: the caller
            # refuses foreign names, and so does the script that would create them.
            if (-not $name.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
                Result $false 'İlke adı bu uygulamanın ad alanında değil.' $null
                return
            }

            if (-not (Get-Command -Name 'New-NetQosPolicy' -ErrorAction SilentlyContinue)) {
                Result $false 'NetQos modülü bulunamadı.' $null
                return
            }

            $store = [string]$env:DPI_BYPASS_QOS_STORE
            if ([string]::IsNullOrWhiteSpace($store)) { $store = 'ActiveStore' }

            $existing = Get-NetQosPolicy -PolicyStore $store -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq $name }
            if ($existing) {
                Remove-NetQosPolicy -Name $name -PolicyStore $store -Confirm:$false -ErrorAction Stop
            }

            $arguments = @{
                Name = $name
                PolicyStore = $store
                Confirm = $false
                ErrorAction = 'Stop'
            }

            if (-not [string]::IsNullOrWhiteSpace($env:DPI_BYPASS_QOS_APP)) {
                $arguments['AppPathNameMatchCondition'] = [string]$env:DPI_BYPASS_QOS_APP
            }
            if (-not [string]::IsNullOrWhiteSpace($env:DPI_BYPASS_QOS_PROTOCOL)) {
                $arguments['IPProtocolMatchCondition'] = [string]$env:DPI_BYPASS_QOS_PROTOCOL
            }
            if (-not [string]::IsNullOrWhiteSpace($env:DPI_BYPASS_QOS_DST)) {
                $arguments['IPDstPrefixMatchCondition'] = [string]$env:DPI_BYPASS_QOS_DST
            }
            if (-not [string]::IsNullOrWhiteSpace($env:DPI_BYPASS_QOS_DSTPORT)) {
                $arguments['IPDstPortMatchCondition'] = [uint16]$env:DPI_BYPASS_QOS_DSTPORT
            }
            if (-not [string]::IsNullOrWhiteSpace($env:DPI_BYPASS_QOS_THROTTLE)) {
                $arguments['ThrottleRateActionBitsPerSecond'] = [uint64]$env:DPI_BYPASS_QOS_THROTTLE
            }
            if (-not [string]::IsNullOrWhiteSpace($env:DPI_BYPASS_QOS_DSCP)) {
                $arguments['DSCPAction'] = [sbyte]$env:DPI_BYPASS_QOS_DSCP
            }
            if (-not [string]::IsNullOrWhiteSpace($env:DPI_BYPASS_QOS_PRECEDENCE)) {
                $arguments['Precedence'] = [uint32]$env:DPI_BYPASS_QOS_PRECEDENCE
            }

            if ($arguments.Count -le 4) {
                Result $false 'Eşleşme koşulu olmayan bir ilke oluşturulmaz.' $null
                return
            }

            New-NetQosPolicy @arguments | Out-Null

            # Read back rather than trusting the create, and read back everything: a policy
            # of the right name carrying the wrong throttle rate or matching the wrong
            # application is worse than no policy, because it looks like one that works.
            $written = Get-NetQosPolicy -PolicyStore $store -ErrorAction Stop |
                Where-Object { $_.Name -eq $name } |
                Select-Object -First 1

            if (-not $written) {
                Result $false 'İlke oluşturuldu ancak depoda görünmüyor.' $null
                return
            }

            Result $true $null (ConvertTo-DpiPolicy $written $store)
        } catch {
            Result $false $_.Exception.Message $null
        }
        """;

    private const string RemoveScript = """
        $ErrorActionPreference = 'Stop'

        function Result([string]$outcome, [string]$reason) {
            [pscustomobject]@{ Outcome = $outcome; Reason = $reason } | ConvertTo-Json -Compress
        }

        try {
            $name = [string]$env:DPI_BYPASS_QOS_NAME
            $prefix = [string]$env:DPI_BYPASS_QOS_PREFIX

            if (-not $name.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
                Result 'Failed' 'İlke adı bu uygulamanın ad alanında değil; silinmedi.'
                return
            }

            if (-not (Get-Command -Name 'Remove-NetQosPolicy' -ErrorAction SilentlyContinue)) {
                Result 'MissingProperty' 'NetQos modülü bulunamadı.'
                return
            }

            $store = [string]$env:DPI_BYPASS_QOS_STORE
            if ([string]::IsNullOrWhiteSpace($store)) { $store = 'ActiveStore' }

            $existing = Get-NetQosPolicy -PolicyStore $store -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq $name }
            if (-not $existing) {
                Result 'AlreadyOriginal' $null
                return
            }

            Remove-NetQosPolicy -Name $name -PolicyStore $store -Confirm:$false -ErrorAction Stop

            $remaining = Get-NetQosPolicy -PolicyStore $store -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq $name }
            if ($remaining) {
                Result 'Failed' 'İlke silme sonrasında hâlâ depoda.'
            } else {
                Result 'Restored' $null
            }
        } catch {
            Result 'Failed' $_.Exception.Message
        }
        """;
}
