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

public sealed record QosApplyResult(bool Created, string? Reason = null);

/// <summary>A policy Windows reports, whoever created it.</summary>
public sealed record QosPolicyInfo
{
    public required string Name { get; init; }

    public string Owner { get; init; } = string.Empty;

    public ulong? ThrottleRateBitsPerSecond { get; init; }

    public int? Dscp { get; init; }

    public string? AppPathName { get; init; }
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
        return dto is null
            ? new QosApplyResult(false, "Windows geçerli bir ilke sonucu döndürmedi.")
            : new QosApplyResult(dto.Created, dto.Reason);
    }

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
        public string? AppPathName { get; init; }
        public ulong ThrottleRateBitsPerSecond { get; init; }
        public bool IsDefault { get; init; }
    }

    private sealed record CreateDto
    {
        public bool Created { get; init; }
        public string? Reason { get; init; }
    }

    private sealed record RemoveDto
    {
        public string? Outcome { get; init; }
        public string? Reason { get; init; }
    }

    private const string ListScript = """
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
                    $all += @(Get-NetQosPolicy -PolicyStore $store -ErrorAction Stop)
                } catch { }
            }

            $policies = @($all |
                Where-Object { $_ -ne $null -and $_.Name } |
                Group-Object -Property Name |
                ForEach-Object {
                    $first = $_.Group | Select-Object -First 1
                    $throttle = 0
                    if ($null -ne $first.ThrottleRateAction) {
                        try { $throttle = [uint64]$first.ThrottleRateAction } catch { $throttle = 0 }
                    }

                    [pscustomobject]@{
                        Name = [string]$first.Name
                        Owner = [string]$first.Owner
                        AppPathName = [string]$first.AppPathName
                        ThrottleRateBitsPerSecond = $throttle
                        IsDefault = ([string]$first.Template -eq 'Default')
                    }
                })

            [pscustomobject]@{ Available = $true; Reason = $null; Policies = $policies } |
                ConvertTo-Json -Depth 4 -Compress
        } catch {
            [pscustomobject]@{ Available = $false; Reason = $_.Exception.Message; Policies = @() } |
                ConvertTo-Json -Depth 4 -Compress
        }
        """;

    private const string CreateScript = """
        $ErrorActionPreference = 'Stop'

        function Result([bool]$created, [string]$reason) {
            [pscustomobject]@{ Created = $created; Reason = $reason } | ConvertTo-Json -Compress
        }

        try {
            $name = [string]$env:DPI_BYPASS_QOS_NAME
            $prefix = [string]$env:DPI_BYPASS_QOS_PREFIX

            # The guard exists on both sides of the boundary on purpose: the caller
            # refuses foreign names, and so does the script that would create them.
            if (-not $name.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
                Result $false 'İlke adı bu uygulamanın ad alanında değil.'
                return
            }

            if (-not (Get-Command -Name 'New-NetQosPolicy' -ErrorAction SilentlyContinue)) {
                Result $false 'NetQos modülü bulunamadı.'
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
                Result $false 'Eşleşme koşulu olmayan bir ilke oluşturulmaz.'
                return
            }

            New-NetQosPolicy @arguments | Out-Null

            # Read back rather than trusting the create: a policy that is not in the
            # store did not happen, whatever the cmdlet returned.
            $written = Get-NetQosPolicy -PolicyStore $store -ErrorAction Stop |
                Where-Object { $_.Name -eq $name }
            if ($written) {
                Result $true $null
            } else {
                Result $false 'İlke oluşturuldu ancak depoda görünmüyor.'
            }
        } catch {
            Result $false $_.Exception.Message
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
