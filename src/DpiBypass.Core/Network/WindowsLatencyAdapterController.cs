using System.Globalization;
using System.Text.Json;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Network;

public interface ILatencyAdapterController
{
    Task<AdapterLatencyCapability?> DetectAsync(
        NetworkFingerprint network,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one value and establishes whether the adapter is actually running with it.
    /// </summary>
    /// <param name="restart">
    /// What this run is allowed to do to a live adapter. A change that only takes effect
    /// after a miniport restart is left unapplied unless this permits one.
    /// </param>
    Task<LatencyApplyResult> ApplyAsync(
        AdapterLatencyCapability adapter,
        LatencyOptimizationCandidate candidate,
        AdapterRestartPolicy restart,
        CancellationToken cancellationToken = default);

    Task<LatencyRestoreOutcome> RestoreAsync(
        LatencySettingSnapshot setting,
        CancellationToken cancellationToken = default);

    /// <summary>Reads what the stack says the adapter's features are actually doing.</summary>
    Task<AdapterOperationalState> ReadOperationalStateAsync(
        string adapterId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses the Windows NetAdapter cmdlets without interpolating adapter data into code.
/// </summary>
/// <remarks>
/// <para>
/// The rule this class exists to enforce is that a registry write is not a result.
/// Microsoft states it plainly for <c>Set-NetAdapterAdvancedProperty</c>: "<c>-NoRestart</c>
/// … Many advanced properties require restarting the network adapter before the new
/// settings take effect." So every write is followed by an attempt to establish what the
/// stack is actually doing - <c>Get-NetAdapterRsc</c>, <c>Get-NetAdapterRss</c> and
/// <c>Get-NetAdapterLso</c> report the running state rather than the stored keyword - and
/// a value the driver has not picked up comes back as
/// <see cref="LatencyApplyState.RestartRequired"/>, never as an applied change.
/// </para>
/// <para>
/// Where no operational query exists, the only honest way to make a keyword take effect
/// is to restart the miniport, and that happens only with explicit consent and never in a
/// remote session. After a restart the adapter has to be the same adapter, with the link
/// up and an address and gateway back, before the caller is told anything succeeded.
/// </para>
/// </remarks>
public sealed class WindowsLatencyAdapterController : ILatencyAdapterController
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly Action<string>? _log;

    public WindowsLatencyAdapterController(Action<string>? log = null) => _log = log;

    public async Task<AdapterLatencyCapability?> DetectAsync(
        NetworkFingerprint network,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(network.AdapterId))
        {
            return null;
        }

        var result = await RunAsync(
            DetectScript,
            BuildEnvironment(network.AdapterId, string.Empty),
            TimeSpan.FromSeconds(25),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Success)
        {
            _log?.Invoke($"NIC düşük gecikme yetenekleri okunamadı: {DescribeFailure(result)}");
            return null;
        }

        var dto = Deserialize<CapabilityDto>(result.StandardOutput);
        if (dto is not { Found: true })
        {
            return null;
        }

        return new AdapterLatencyCapability
        {
            AdapterId = dto.AdapterId ?? network.AdapterId,
            AdapterName = dto.AdapterName ?? network.AdapterName ?? network.DisplayName,
            InterfaceDescription = dto.InterfaceDescription ?? string.Empty,
            AdapterType = network.AdapterType,
            IsPhysical = dto.HardwareInterface,
            IsVirtual = dto.Virtual,
            DriverVersion = dto.DriverVersion ?? string.Empty,
            // NetworkFingerprint got here from the live .NET adapter and gateway,
            // so eligibility does not depend on the localised text form of Status.
            IsUp = network.IsOnline,
            PowerManagement = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["SelectiveSuspend"] = dto.Power?.SelectiveSuspend ?? 0,
                ["DeviceSleepOnDisconnect"] = dto.Power?.DeviceSleepOnDisconnect ?? 0,
                ["D0PacketCoalescing"] = dto.Power?.D0PacketCoalescing ?? 0,
            },
            AdvancedProperties = dto.AdvancedProperties.Select(property => new AdapterAdvancedPropertyCapability
            {
                RegistryKeyword = property.RegistryKeyword ?? string.Empty,
                RegistryValues = property.RegistryValues,
                ValidRegistryValues = property.ValidRegistryValues,
            }).ToList(),
            RscIPv4Operational = dto.Rsc?.IPv4Operational,
            RscIPv6Operational = dto.Rsc?.IPv6Operational,
            RssEnabled = dto.Rss?.Enabled,
            RssMaxProcessors = dto.Rss?.MaxProcessors,
            LsoV2IPv4Enabled = dto.Lso?.V2IPv4Enabled,
            LsoV2IPv6Enabled = dto.Lso?.V2IPv6Enabled,
        };
    }

    public async Task<AdapterOperationalState> ReadOperationalStateAsync(
        string adapterId,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(adapterId))
        {
            return AdapterOperationalState.Empty;
        }

        var result = await RunAsync(
            OperationalScript,
            BuildEnvironment(adapterId, string.Empty),
            TimeSpan.FromSeconds(25),
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return AdapterOperationalState.Empty;
        }

        return ToOperationalState(Deserialize<OperationalDto>(result.StandardOutput));
    }

    public async Task<LatencyApplyResult> ApplyAsync(
        AdapterLatencyCapability adapter,
        LatencyOptimizationCandidate candidate,
        AdapterRestartPolicy restart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(restart);

        if (candidate.Kind != LatencySettingKind.AdvancedProperty)
        {
            // Power management is restorable but no longer writable: see
            // AdapterInterventionCatalog for why neither remaining power keyword can be
            // judged by a steady-state round-trip experiment.
            return LatencyApplyResult.Refused(
                "Bu sürüm güç yönetimi özelliklerini değiştirmez; yalnız eski anlık görüntülerden geri yükler.");
        }

        var environment = BuildEnvironment(adapter.AdapterId, candidate.PropertyName);
        environment["DPI_BYPASS_REGISTRY_VALUES"] = JsonSerializer.Serialize(candidate.DesiredValues);
        environment["DPI_BYPASS_OPERATIONAL_TARGET"] = DescribeOperationalTarget(candidate);
        environment["DPI_BYPASS_ALLOW_RESTART"] = restart.Allowed ? "1" : "0";
        environment["DPI_BYPASS_LINK_TIMEOUT"] = ((int)restart.LinkRecoveryTimeout.TotalSeconds)
            .ToString(CultureInfo.InvariantCulture);

        // The script may restart the miniport and then wait for the link, so the process
        // timeout has to leave room for the whole of that plus the cmdlets around it.
        var timeout = TimeSpan.FromSeconds(30) + (restart.Allowed ? restart.LinkRecoveryTimeout : TimeSpan.Zero);
        var result = await RunAsync(ApplyScript, environment, timeout, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Success)
        {
            return LatencyApplyResult.Refused(DescribeFailure(result));
        }

        var dto = Deserialize<ApplyDto>(result.StandardOutput);
        if (dto is null || !Enum.TryParse<LatencyApplyState>(dto.State, ignoreCase: true, out var state))
        {
            return LatencyApplyResult.Refused("Windows geçerli bir uygulama sonucu döndürmedi.");
        }

        var reason = dto.Reason;
        if (state == LatencyApplyState.RestartRequired && !restart.Allowed)
        {
            reason = restart.RefusalReason ?? reason;
        }

        return new LatencyApplyResult
        {
            State = state,
            Reason = reason,
            RestartPerformed = dto.Restarted,
            Operational = ToOperationalState(dto.Operational),
        };
    }

    public async Task<LatencyRestoreOutcome> RestoreAsync(
        LatencySettingSnapshot setting,
        CancellationToken cancellationToken = default)
    {
        var environment = BuildEnvironment(setting.AdapterId, setting.PropertyName);
        environment["DPI_BYPASS_SETTING_KIND"] = setting.Kind.ToString();
        environment["DPI_BYPASS_POWER_VALUE"] = setting.OriginalPowerValue?.ToString(CultureInfo.InvariantCulture);
        environment["DPI_BYPASS_REGISTRY_VALUES"] = JsonSerializer.Serialize(setting.OriginalValues);

        var result = await RunAsync(RestoreScript, environment, TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Success)
        {
            _log?.Invoke($"'{setting.PropertyName}' geri yüklenemedi: {DescribeFailure(result)}");
            return LatencyRestoreOutcome.Failed;
        }

        var dto = Deserialize<RestoreDto>(result.StandardOutput);
        if (dto is null || !Enum.TryParse<LatencyRestoreOutcome>(dto.Outcome, ignoreCase: true, out var outcome))
        {
            return LatencyRestoreOutcome.Failed;
        }

        if (!string.IsNullOrWhiteSpace(dto.Reason))
        {
            _log?.Invoke($"'{setting.PropertyName}' geri yükleme sonucu: {dto.Reason}");
        }

        return outcome;
    }

    /// <summary>
    /// Which operational field the script must look at, and what it must say.
    /// </summary>
    /// <remarks>
    /// Passed in rather than derived in PowerShell so the mapping between a keyword and
    /// the query that can prove it lives in one place, next to the catalogue that decides
    /// which keywords exist at all.
    /// </remarks>
    internal static string DescribeOperationalTarget(LatencyOptimizationCandidate candidate)
    {
        if (!AdapterOperationalState.HasOperationalQuery(candidate.PropertyName))
        {
            return string.Empty;
        }

        var wanted = candidate.DesiredValues.Count == 1
            && string.Equals(candidate.DesiredValues[0], "1", StringComparison.Ordinal);

        var field = candidate.PropertyName switch
        {
            AdapterInterventionCatalog.RscIPv4Keyword => "RscIPv4Operational",
            AdapterInterventionCatalog.RscIPv6Keyword => "RscIPv6Operational",
            AdapterInterventionCatalog.RssKeyword => "RssEnabled",
            AdapterInterventionCatalog.LsoIPv4Keyword => "LsoV2IPv4Enabled",
            _ => "LsoV2IPv6Enabled",
        };

        return $"{field}={(wanted ? "true" : "false")}";
    }

    /// <summary>
    /// The fixed names the scripts read, plus the allow-lists they enforce.
    /// </summary>
    /// <remarks>
    /// The lists are passed in rather than written into the scripts so the catalogue in
    /// C# stays the only place that decides what may be touched, and so a test can prove
    /// what that list does and does not contain.
    /// </remarks>
    private static Dictionary<string, string?> BuildEnvironment(string adapterId, string propertyName) => new()
    {
        ["DPI_BYPASS_ADAPTER_ID"] = adapterId,
        ["DPI_BYPASS_PROPERTY"] = propertyName,
        ["DPI_BYPASS_KEYWORDS"] = JsonSerializer.Serialize(AdapterInterventionCatalog.WritableKeywords),
        ["DPI_BYPASS_FORBIDDEN"] = JsonSerializer.Serialize(AdapterInterventionCatalog.ForbiddenKeywords),
        ["DPI_BYPASS_POWER_WRITE"] = JsonSerializer.Serialize(AdapterInterventionCatalog.WritablePowerProperties),
        ["DPI_BYPASS_POWER_RESTORE"] = JsonSerializer.Serialize(AdapterInterventionCatalog.RestorablePowerProperties),
    };

    private static AdapterOperationalState ToOperationalState(OperationalDto? dto) => dto is null
        ? AdapterOperationalState.Empty
        : new AdapterOperationalState
        {
            RscIPv4Operational = dto.RscIPv4Operational,
            RscIPv6Operational = dto.RscIPv6Operational,
            RssEnabled = dto.RssEnabled,
            LsoV2IPv4Enabled = dto.LsoV2IPv4Enabled,
            LsoV2IPv6Enabled = dto.LsoV2IPv6Enabled,
            LinkUsable = dto.LinkUsable,
            RegistryValues = dto.RegistryValues,
        };

    private static Task<ProcessResult> RunAsync(
        string script,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => ProcessRunner.PowerShellWithEnvironmentAsync(script, environment, timeout, cancellationToken);

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

    private static string DescribeFailure(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(text) ? $"PowerShell çıkış kodu {result.ExitCode}" : text.Trim();
    }

    private sealed record CapabilityDto
    {
        public bool Found { get; init; }
        public string? AdapterId { get; init; }
        public string? AdapterName { get; init; }
        public string? InterfaceDescription { get; init; }
        public string? DriverVersion { get; init; }
        public bool HardwareInterface { get; init; }
        public bool Virtual { get; init; }
        public PowerDto? Power { get; init; }
        public List<AdvancedPropertyDto> AdvancedProperties { get; init; } = [];
        public RscDto? Rsc { get; init; }
        public RssDto? Rss { get; init; }
        public LsoDto? Lso { get; init; }
    }

    private sealed record RscDto
    {
        public bool IPv4Enabled { get; init; }
        public bool IPv6Enabled { get; init; }
        public bool IPv4Operational { get; init; }
        public bool IPv6Operational { get; init; }
    }

    private sealed record RssDto
    {
        public bool Enabled { get; init; }
        public int MaxProcessors { get; init; }
    }

    private sealed record LsoDto
    {
        public bool V2IPv4Enabled { get; init; }
        public bool V2IPv6Enabled { get; init; }
    }

    private sealed record PowerDto
    {
        public int SelectiveSuspend { get; init; }
        public int DeviceSleepOnDisconnect { get; init; }
        public int D0PacketCoalescing { get; init; }
    }

    private sealed record AdvancedPropertyDto
    {
        public string? RegistryKeyword { get; init; }
        public List<string> RegistryValues { get; init; } = [];
        public List<string> ValidRegistryValues { get; init; } = [];
    }

    private sealed record OperationalDto
    {
        public bool? RscIPv4Operational { get; init; }
        public bool? RscIPv6Operational { get; init; }
        public bool? RssEnabled { get; init; }
        public bool? LsoV2IPv4Enabled { get; init; }
        public bool? LsoV2IPv6Enabled { get; init; }
        public bool? LinkUsable { get; init; }
        public List<string> RegistryValues { get; init; } = [];
    }

    private sealed record ApplyDto
    {
        public string? State { get; init; }
        public string? Reason { get; init; }
        public bool Restarted { get; init; }
        public OperationalDto? Operational { get; init; }
    }

    private sealed record RestoreDto
    {
        public string? Outcome { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Shared PowerShell helpers, prepended to every script this class runs.
    /// </summary>
    /// <remarks>
    /// The operational readers are the important part: each asks Windows what a feature
    /// is doing rather than which keyword is stored, and each returns <c>$null</c> when
    /// the driver does not answer, so "we could not tell" never turns into "no".
    /// </remarks>
    private const string CommonScript = """
        $ErrorActionPreference = 'Stop'

        function Find-DpiAdapter {
            $wanted = ($env:DPI_BYPASS_ADAPTER_ID -replace '[{}]', '').Trim()
            @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {
                (([string]$_.InterfaceGuid) -replace '[{}]', '').Trim() -eq $wanted
            }) | Select-Object -First 1
        }

        function Read-DpiOperational($adapter) {
            $rscV4 = $null; $rscV6 = $null; $rss = $null; $lsoV4 = $null; $lsoV6 = $null
            try {
                $state = Get-NetAdapterRsc -Name $adapter.Name -ErrorAction Stop | Select-Object -First 1
                if ($null -ne $state) {
                    $rscV4 = [bool]$state.IPv4Operational
                    $rscV6 = [bool]$state.IPv6Operational
                }
            } catch { }
            try {
                $state = Get-NetAdapterRss -Name $adapter.Name -ErrorAction Stop | Select-Object -First 1
                if ($null -ne $state) { $rss = [bool]$state.Enabled }
            } catch { }
            try {
                $state = Get-NetAdapterLso -Name $adapter.Name -ErrorAction Stop | Select-Object -First 1
                if ($null -ne $state) {
                    $lsoV4 = [bool]$state.V2IPv4Enabled
                    $lsoV6 = [bool]$state.V2IPv6Enabled
                }
            } catch { }

            [pscustomobject]@{
                RscIPv4Operational = $rscV4
                RscIPv6Operational = $rscV6
                RssEnabled = $rss
                LsoV2IPv4Enabled = $lsoV4
                LsoV2IPv6Enabled = $lsoV6
                LinkUsable = (Test-DpiLink $adapter)
                RegistryValues = @(Read-DpiKeyword $adapter ([string]$env:DPI_BYPASS_PROPERTY))
            }
        }

        function Read-DpiKeyword($adapter, [string]$keyword) {
            if ([string]::IsNullOrWhiteSpace($keyword)) { return @() }
            try {
                return @(Get-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $keyword -AllProperties -ErrorAction Stop |
                    Select-Object -ExpandProperty RegistryValue | ForEach-Object { [string]$_ })
            } catch {
                return @()
            }
        }

        # Up, addressed and with a default gateway: the three things that have to be true
        # again before a restarted adapter is a working adapter rather than a present one.
        function Test-DpiLink($adapter) {
            try {
                $live = Get-NetAdapter -IncludeHidden -Name $adapter.Name -ErrorAction Stop
                if ([string]$live.Status -ne 'Up') { return $false }

                $index = [int]$live.ifIndex
                $addresses = @(Get-NetIPAddress -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                    Where-Object { [string]$_.IPAddress -notlike '169.254.*' })
                if ($addresses.Count -eq 0) { return $false }

                $routes = @(Get-NetRoute -InterfaceIndex $index -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                    Where-Object { $_.NextHop -and [string]$_.NextHop -ne '0.0.0.0' })
                return $routes.Count -gt 0
            } catch {
                return $false
            }
        }
        """;

    private const string DetectScript = CommonScript + """

        function Read-PowerState($instance, [string]$name) {
            if ($null -eq $instance) { return 0 }
            $property = $instance.PSObject.Properties[$name]
            if ($null -eq $property -or $null -eq $property.Value) { return 0 }
            try { return [int]$property.Value } catch { return 0 }
        }

        $adapter = Find-DpiAdapter
        if ($null -eq $adapter) {
            [pscustomobject]@{ Found = $false } | ConvertTo-Json -Compress
            return
        }

        $power = $null
        try {
            $power = Get-NetAdapterPowerManagement -Name $adapter.Name -ErrorAction Stop
        } catch { }

        # Only the keywords this build is allowed to write are even read, and they are
        # matched on RegistryKeyword. DisplayName and DisplayValue are never touched
        # because Windows localises both.
        $wanted = @($env:DPI_BYPASS_KEYWORDS | ConvertFrom-Json | ForEach-Object { [string]$_ })
        $forbidden = @($env:DPI_BYPASS_FORBIDDEN | ConvertFrom-Json | ForEach-Object { [string]$_ })

        $advanced = @()
        try {
            $advanced = @(Get-NetAdapterAdvancedProperty -Name $adapter.Name -AllProperties -ErrorAction Stop |
                Where-Object { $wanted -contains [string]$_.RegistryKeyword } |
                Where-Object { $forbidden -notcontains [string]$_.RegistryKeyword } |
                ForEach-Object {
                    [pscustomobject]@{
                        RegistryKeyword = [string]$_.RegistryKeyword
                        RegistryValues = @($_.RegistryValue | ForEach-Object { [string]$_ })
                        ValidRegistryValues = @($_.ValidRegistryValues | ForEach-Object { [string]$_ })
                    }
                })
        } catch { }

        # Whether RSC, RSS and LSO are actually operational is not the same question as
        # whether the keyword is set, and the difference is the whole reason this build
        # can tell a real change from a stored one.
        $rsc = $null
        try {
            $state = Get-NetAdapterRsc -Name $adapter.Name -ErrorAction Stop | Select-Object -First 1
            if ($null -ne $state) {
                $rsc = [pscustomobject]@{
                    IPv4Enabled = [bool]$state.IPv4Enabled
                    IPv6Enabled = [bool]$state.IPv6Enabled
                    IPv4Operational = [bool]$state.IPv4Operational
                    IPv6Operational = [bool]$state.IPv6Operational
                }
            }
        } catch { }

        $rss = $null
        try {
            $state = Get-NetAdapterRss -Name $adapter.Name -ErrorAction Stop | Select-Object -First 1
            if ($null -ne $state) {
                $rss = [pscustomobject]@{
                    Enabled = [bool]$state.Enabled
                    MaxProcessors = [int]$state.MaxProcessors
                }
            }
        } catch { }

        $lso = $null
        try {
            $state = Get-NetAdapterLso -Name $adapter.Name -ErrorAction Stop | Select-Object -First 1
            if ($null -ne $state) {
                $lso = [pscustomobject]@{
                    V2IPv4Enabled = [bool]$state.V2IPv4Enabled
                    V2IPv6Enabled = [bool]$state.V2IPv6Enabled
                }
            }
        } catch { }

        # The driver version is part of the profile fingerprint: a result verified against
        # one driver says nothing about the next one.
        $driverVersion = ''
        try { $driverVersion = [string]$adapter.DriverVersion } catch { }

        [pscustomobject]@{
            Found = $true
            AdapterId = [string]$adapter.InterfaceGuid
            AdapterName = [string]$adapter.Name
            InterfaceDescription = [string]$adapter.InterfaceDescription
            DriverVersion = $driverVersion
            HardwareInterface = [bool]$adapter.HardwareInterface
            Virtual = [bool]$adapter.Virtual
            Power = [pscustomobject]@{
                SelectiveSuspend = Read-PowerState $power 'SelectiveSuspend'
                DeviceSleepOnDisconnect = Read-PowerState $power 'DeviceSleepOnDisconnect'
                D0PacketCoalescing = Read-PowerState $power 'D0PacketCoalescing'
            }
            AdvancedProperties = $advanced
            Rsc = $rsc
            Rss = $rss
            Lso = $lso
        } | ConvertTo-Json -Depth 6 -Compress
        """;

    private const string OperationalScript = CommonScript + """

        $adapter = Find-DpiAdapter
        if ($null -eq $adapter) {
            [pscustomobject]@{ LinkUsable = $false } | ConvertTo-Json -Depth 4 -Compress
            return
        }

        Read-DpiOperational $adapter | ConvertTo-Json -Depth 4 -Compress
        """;

    private const string ApplyScript = CommonScript + """

        function Result([string]$state, [string]$reason, [bool]$restarted, $operational) {
            [pscustomobject]@{
                State = $state
                Reason = $reason
                Restarted = $restarted
                Operational = $operational
            } | ConvertTo-Json -Depth 5 -Compress
        }

        # "RscIPv4Operational=false" and friends. Empty means Windows has no query that
        # can answer whether this keyword took effect, so a restart is the only proof.
        function Test-DpiOperationalTarget($operational) {
            $spec = [string]$env:DPI_BYPASS_OPERATIONAL_TARGET
            if ([string]::IsNullOrWhiteSpace($spec)) { return $null }

            $parts = $spec.Split('=')
            if ($parts.Count -ne 2) { return $null }

            $member = $operational.PSObject.Properties[$parts[0]]
            if ($null -eq $member -or $null -eq $member.Value) { return $null }

            return ([bool]$member.Value) -eq ($parts[1] -eq 'true')
        }

        try {
            $adapter = Find-DpiAdapter
            if ($null -eq $adapter -or -not [bool]$adapter.HardwareInterface -or [bool]$adapter.Virtual) {
                Result 'Refused' 'Aktif fiziksel bağdaştırıcı artık bulunamadı.' $false $null
                return
            }

            $property = [string]$env:DPI_BYPASS_PROPERTY
            $keywords = @($env:DPI_BYPASS_KEYWORDS | ConvertFrom-Json | ForEach-Object { [string]$_ })
            $forbidden = @($env:DPI_BYPASS_FORBIDDEN | ConvertFrom-Json | ForEach-Object { [string]$_ })

            # Checksum offloads are refused here as well as in the catalogue. Microsoft's
            # guidance is that they should always be enabled, and RSS, RSC and LSO all
            # depend on them, so there is no path through this script that turns one off.
            if ($forbidden -contains $property) {
                Result 'Refused' 'Sağlama toplamı devri bu uygulama tarafından hiçbir koşulda değiştirilmez.' $false $null
                return
            }

            if ($keywords -notcontains $property) {
                Result 'Refused' 'İzin verilmeyen NIC özelliği reddedildi.' $false $null
                return
            }

            $current = Get-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $property -AllProperties -ErrorAction Stop
            $values = @($env:DPI_BYPASS_REGISTRY_VALUES | ConvertFrom-Json | ForEach-Object { [string]$_ })
            $valid = @($current.ValidRegistryValues | ForEach-Object { [string]$_ })
            if (@($values | Where-Object { $_ -notin $valid }).Count -gt 0) {
                Result 'Refused' 'Sürücü istenen RegistryValue değerini desteklemiyor.' $false $null
                return
            }

            # Written without a restart first: if the driver honours it live there is no
            # reason to drop the user's connections to find that out.
            Set-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $property -RegistryValue $values -NoRestart -Confirm:$false -ErrorAction Stop

            $stored = @(Read-DpiKeyword $adapter $property)
            if (@(Compare-Object -ReferenceObject $values -DifferenceObject $stored).Count -ne 0) {
                Result 'Refused' 'Sürücü istenen değeri kayıt defterine yazmadı.' $false $null
                return
            }

            $operational = Read-DpiOperational $adapter
            $effective = Test-DpiOperationalTarget $operational
            if ($effective -eq $true) {
                Result 'OperationallyVerified' $null $false $operational
                return
            }

            if ([string]$env:DPI_BYPASS_ALLOW_RESTART -ne '1') {
                Result 'RestartRequired' 'Değer yazıldı ancak sürücü henüz kullanmıyor; yeniden başlatma onayı yok.' $false $operational
                return
            }

            # Consented restart. Restart-NetAdapter is the documented way to make the
            # miniport re-read its configuration, and it is what Set-NetAdapterAdvancedProperty
            # itself does when -NoRestart is not passed.
            Restart-NetAdapter -Name $adapter.Name -Confirm:$false -ErrorAction Stop

            $timeout = 45
            try { $timeout = [int]$env:DPI_BYPASS_LINK_TIMEOUT } catch { }
            if ($timeout -lt 5) { $timeout = 5 }

            $deadline = (Get-Date).AddSeconds($timeout)
            $back = $null
            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 750
                $back = Find-DpiAdapter
                # The same adapter, by GUID: a restart that brought a different interface
                # back is not this experiment's adapter and must not be measured.
                if ($null -ne $back -and (Test-DpiLink $back)) { break }
                $back = $null
            }

            if ($null -eq $back) {
                Result 'LinkNotRestored' 'Yeniden başlatmadan sonra bağlantı geri gelmedi.' $true $null
                return
            }

            $operational = Read-DpiOperational $back
            $stored = @($operational.RegistryValues)
            if (@(Compare-Object -ReferenceObject $values -DifferenceObject $stored).Count -ne 0) {
                Result 'NotVerified' 'Yeniden başlatmadan sonra değer kayıt defterinde kalmadı.' $true $operational
                return
            }

            $effective = Test-DpiOperationalTarget $operational
            if ($effective -eq $true) {
                Result 'OperationallyVerified' $null $true $operational
                return
            }

            if ($effective -eq $false) {
                Result 'NotVerified' 'Yeniden başlatmadan sonra da işletim sistemi ayarı etkin bildirmiyor.' $true $operational
                return
            }

            # No operational query exists for this keyword. The miniport reloaded with the
            # new value and the link came back, which is as far as Windows lets anyone
            # verify it - and it is reported as exactly that, not as an operational check.
            Result 'AdapterRestarted' $null $true $operational
        } catch {
            Result 'Refused' $_.Exception.Message $false $null
        }
        """;

    private const string RestoreScript = CommonScript + """

        function Result([string]$outcome, [string]$reason) {
            [pscustomobject]@{ Outcome = $outcome; Reason = $reason } | ConvertTo-Json -Compress
        }

        try {
            $adapter = Find-DpiAdapter
            if ($null -eq $adapter) {
                Result 'MissingAdapter' 'Bağdaştırıcı artık sistemde yok; anlık görüntü korundu.'
                return
            }

            $property = [string]$env:DPI_BYPASS_PROPERTY
            $keywords = @($env:DPI_BYPASS_KEYWORDS | ConvertFrom-Json | ForEach-Object { [string]$_ })
            $forbidden = @($env:DPI_BYPASS_FORBIDDEN | ConvertFrom-Json | ForEach-Object { [string]$_ })
            $powerRestorable = @($env:DPI_BYPASS_POWER_RESTORE | ConvertFrom-Json | ForEach-Object { [string]$_ })

            if ($forbidden -contains $property) {
                Result 'MissingProperty' 'Sağlama toplamı devri bu uygulama tarafından yönetilmez.'
                return
            }

            if ($env:DPI_BYPASS_SETTING_KIND -eq 'PowerManagement') {
                if ($powerRestorable -notcontains $property) {
                    Result 'MissingProperty' 'Snapshot içindeki özellik bu sürüm tarafından yönetilmiyor.'
                    return
                }

                $power = Get-NetAdapterPowerManagement -Name $adapter.Name -ErrorAction Stop
                $member = $power.PSObject.Properties[$property]
                if ($null -eq $member -or [int]$member.Value -eq 0) {
                    Result 'MissingProperty' 'Sürücü güncellemesinden sonra güç özelliği bulunamadı.'
                    return
                }

                $original = [int]$env:DPI_BYPASS_POWER_VALUE
                if ([int]$member.Value -eq $original) {
                    Result 'AlreadyOriginal' $null
                    return
                }

                switch ($property) {
                    'SelectiveSuspend' {
                        Set-NetAdapterPowerManagement -Name $adapter.Name -SelectiveSuspend $original -NoRestart -Confirm:$false -ErrorAction Stop
                    }
                    'DeviceSleepOnDisconnect' {
                        Set-NetAdapterPowerManagement -Name $adapter.Name -DeviceSleepOnDisconnect $original -NoRestart -Confirm:$false -ErrorAction Stop
                    }
                    'D0PacketCoalescing' {
                        Set-NetAdapterPowerManagement -Name $adapter.Name -D0PacketCoalescing $original -NoRestart -Confirm:$false -ErrorAction Stop
                    }
                }

                $actual = Get-NetAdapterPowerManagement -Name $adapter.Name -ErrorAction Stop
                if ([int]$actual.PSObject.Properties[$property].Value -eq $original) {
                    Result 'Restored' $null
                } else {
                    Result 'Failed' 'Sürücü özgün güç değerini canlı olarak geri almadı.'
                }
                return
            }

            if ($env:DPI_BYPASS_SETTING_KIND -eq 'AdvancedProperty') {
                if ($keywords -notcontains $property) {
                    Result 'MissingProperty' 'Snapshot içindeki anahtar bu sürüm tarafından yönetilmiyor.'
                    return
                }

                $current = Get-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $property -AllProperties -ErrorAction SilentlyContinue
                if ($null -eq $current) {
                    Result 'MissingProperty' 'Sürücü güncellemesinden sonra gelişmiş özellik bulunamadı.'
                    return
                }

                $original = @($env:DPI_BYPASS_REGISTRY_VALUES | ConvertFrom-Json | ForEach-Object { [string]$_ })
                $actualBefore = @($current.RegistryValue | ForEach-Object { [string]$_ })
                if (@(Compare-Object -ReferenceObject $original -DifferenceObject $actualBefore).Count -eq 0) {
                    Result 'AlreadyOriginal' $null
                    return
                }

                Set-NetAdapterAdvancedProperty -Name $adapter.Name -RegistryKeyword $property -RegistryValue $original -NoRestart -Confirm:$false -ErrorAction Stop
                $actual = @(Read-DpiKeyword $adapter $property)
                if (@(Compare-Object -ReferenceObject $original -DifferenceObject $actual).Count -eq 0) {
                    Result 'Restored' $null
                } else {
                    Result 'Failed' 'Sürücü özgün RegistryValue değerini canlı olarak geri almadı.'
                }
                return
            }

            Result 'MissingProperty' 'Bilinmeyen snapshot ayar türü atlandı.'
        } catch {
            Result 'Failed' $_.Exception.Message
        }
        """;
}
