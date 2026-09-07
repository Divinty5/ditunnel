param(
    [Parameter(Mandatory=$true)][string]$RuntimePath,
    [Parameter(Mandatory=$true)][string]$ConfigurationPath,
    [Parameter(Mandatory=$true)][string]$ServerAddress,
    [Parameter(Mandatory=$true)][int]$OwnerProcessId,
    [ValidateSet('ProxyAll','BypassSelected','ProxySelected')][string]$SplitTunnelMode = 'ProxyAll',
    [string]$SplitAddresses = '',
    [string]$SplitDomains = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ownedRoutes = [System.Collections.Generic.List[object]]::new()
$dnsRule = $null
$xrayProcess = $null
$mutex = [Threading.Mutex]::new($false, 'Global\DiTunnel.NetworkHost.v1')
$locked = $false
$cleanupFailed = $false
$splitRoutes = @{}
$nextSplitRefresh = [DateTime]::MinValue
$dnsComment = 'DiTunnel managed DNS v1'
$stopPath = $ConfigurationPath + '.stop'
$stage = 'PRECHECK'
function Test-StopRequested { return (Test-Path -LiteralPath $stopPath) -or $owner.HasExited }
function Enter-Stage([string]$name) {
    $script:stage = $name
    Emit ('STAGE_' + $name)
    if (Test-StopRequested) { throw [OperationCanceledException]::new() }
}
function Emit([string]$state) { [Console]::Out.WriteLine($state); [Console]::Out.Flush() }
function Add-OwnedRoute([string]$prefix, [uint32]$index, [string]$nextHop) {
    New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $index -NextHop $nextHop -RouteMetric 1 -PolicyStore ActiveStore | Out-Null
    $ownedRoutes.Add([pscustomobject]@{ DestinationPrefix = $prefix; InterfaceIndex = $index; NextHop = $nextHop })
}
function Remove-OwnedRoute([object]$route) {
    Get-NetRoute -DestinationPrefix $route.DestinationPrefix -InterfaceIndex $route.InterfaceIndex -NextHop $route.NextHop -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
        Remove-NetRoute -Confirm:$false -ErrorAction Stop
    [void]$ownedRoutes.Remove($route)
}
function Update-SplitRoutes([string[]]$addresses) {
    if ($SplitTunnelMode -eq 'ProxyAll') { return }
    $desired = @{}
    foreach ($address in $addresses) {
        if ($address -and $address -ne $ServerAddress) { $desired[$address] = $true }
    }
    if ($desired.Count -eq 0) { return }
    foreach ($address in @($splitRoutes.Keys)) {
        if (-not $desired.ContainsKey($address)) {
            try { Remove-OwnedRoute $splitRoutes[$address]; $splitRoutes.Remove($address) }
            catch { }
        }
    }
    foreach ($address in $desired.Keys) {
        if ($splitRoutes.ContainsKey($address)) { continue }
        if ($SplitTunnelMode -eq 'BypassSelected') {
            Add-OwnedRoute "$address/32" $upstream.InterfaceIndex $upstream.NextHop
        } else {
            Add-OwnedRoute "$address/32" $index '0.0.0.0'
        }
        $splitRoutes[$address] = $ownedRoutes[$ownedRoutes.Count - 1]
    }
}
function Refresh-SplitRoutesIfDue {
    if ($SplitTunnelMode -eq 'ProxyAll' -or [DateTime]::UtcNow -lt $nextSplitRefresh) { return }
    $script:nextSplitRefresh = [DateTime]::UtcNow.AddSeconds(45)
    $addresses = [System.Collections.Generic.List[string]]::new()
    foreach ($domain in ($SplitDomains -split ';' | Where-Object { $_ })) {
        try {
            [Net.Dns]::GetHostAddresses($domain) |
                Where-Object { $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork } |
                ForEach-Object { $addresses.Add($_.ToString()) }
        } catch { }
    }
    if ($addresses.Count -gt 0) { Update-SplitRoutes @($addresses | Sort-Object -Unique) }
}
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another host is active' }
    # Remove only our own stale DNS policy (e.g. after power loss). ActiveStore routes do not survive reboot.
    Get-DnsClientNrptRule | Where-Object { $_.Comment -eq $dnsComment } | Remove-DnsClientNrptRule -Force
    if (@(Get-DnsClientNrptRule).Count -gt 0) { Emit 'ERROR_DNS_POLICY'; throw 'Existing DNS policy' }
    $owner = Get-Process -Id $OwnerProcessId
    $ownerStart = $owner.StartTime
    Enter-Stage 'PRECHECK'
    $ip = [Net.IPAddress]::Parse($ServerAddress)
    if ($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'IPv4 upstream required' }
    $path = @(Find-NetRoute -RemoteIPAddress $ServerAddress)
    $upstream = $path | Where-Object { $_.PSObject.Properties.Name -contains 'NextHop' } | Select-Object -First 1
    if (-not $upstream -or $upstream.InterfaceAlias -eq 'DiTunnel') { throw 'No physical upstream route' }
    $physicalAdapter = Get-NetAdapter -InterfaceIndex $upstream.InterfaceIndex
    if (-not $physicalAdapter.HardwareInterface) { Emit 'ERROR_OTHER_VPN'; throw 'Disconnect other VPN first' }
    $defaultPrefixes = @('0.0.0.0/0','0.0.0.0/1','128.0.0.0/1','::/0','::/1','8000::/1')
    foreach ($otherRoute in (Get-NetRoute | Where-Object { $_.DestinationPrefix -in $defaultPrefixes })) {
        $otherAdapter = Get-NetAdapter -InterfaceIndex $otherRoute.InterfaceIndex -ErrorAction SilentlyContinue
        if ($otherAdapter -and -not $otherAdapter.HardwareInterface -and $otherAdapter.Status -eq 'Up') {
            Emit 'ERROR_OTHER_VPN'; throw 'Another tunnel has a default route'
        }
    }
    if (Get-NetAdapter -Name 'DiTunnel' -ErrorAction SilentlyContinue) { throw 'Tunnel already exists' }
    # A /32 prevents the proxy connection from being captured by the new split default routes.
    $existing = @(Get-NetRoute -DestinationPrefix "$ServerAddress/32" -ErrorAction SilentlyContinue)
    if ($existing.Count -eq 0) { Add-OwnedRoute "$ServerAddress/32" $upstream.InterfaceIndex $upstream.NextHop }
    Enter-Stage 'XRAY'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $RuntimePath
    $startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($RuntimePath)
    $startInfo.Arguments = 'run -config "' + $ConfigurationPath + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $xrayProcess = [Diagnostics.Process]::Start($startInfo)
    $discardOutput = $xrayProcess.StandardOutput.ReadToEndAsync()
    $discardError = $xrayProcess.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if ($xrayProcess.HasExited -or $owner.HasExited -or (Test-StopRequested)) { throw 'Startup interrupted' }
        $adapter = Get-NetAdapter -Name 'DiTunnel' -ErrorAction SilentlyContinue
        if ($adapter) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $adapter) { throw 'Adapter unavailable' }
    Enter-Stage 'ADDRESSES'
    $index = $adapter.InterfaceIndex
    New-NetIPAddress -InterfaceIndex $index -IPAddress '172.31.255.1' -PrefixLength 30 -PolicyStore ActiveStore | Out-Null
    New-NetIPAddress -InterfaceIndex $index -IPAddress 'fd52:d17::1' -PrefixLength 64 -PolicyStore ActiveStore | Out-Null
    Enter-Stage 'ADDRESS_READY'
    $addressDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (Test-StopRequested) { throw [OperationCanceledException]::new() }
        $assigned = Get-NetIPAddress -InterfaceIndex $index -IPAddress '172.31.255.1' -ErrorAction Stop
        if ($assigned.AddressState -eq 'Preferred') { break }
        if ($assigned.AddressState -eq 'Duplicate') { throw 'Duplicate tunnel address' }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $addressDeadline)
    if ($assigned.AddressState -ne 'Preferred') { throw 'Tunnel address is not ready' }
    Set-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 1 -NlMtuBytes 1400
    Set-NetIPInterface -InterfaceIndex $index -AddressFamily IPv6 -AutomaticMetric Disabled -InterfaceMetric 1 -NlMtuBytes 1400
    Set-DnsClientServerAddress -InterfaceIndex $index -ServerAddresses '1.1.1.1','1.0.0.1'
    Enter-Stage 'ROUTES'
    $splitIps = @($SplitAddresses -split ',' | Where-Object { $_ -and $_ -ne $ServerAddress } | Sort-Object -Unique)
    if ($SplitTunnelMode -eq 'ProxySelected' -and $splitIps.Count -eq 0) { throw 'No selected domain addresses' }
    if ($SplitTunnelMode -ne 'ProxySelected') {
        Add-OwnedRoute '0.0.0.0/1' $index '0.0.0.0'
        Add-OwnedRoute '128.0.0.0/1' $index '0.0.0.0'
        Add-OwnedRoute '::/1' $index '::'
        Add-OwnedRoute '8000::/1' $index '::'
    }
    Update-SplitRoutes $splitIps
    if ($SplitTunnelMode -ne 'ProxySelected') {
        Enter-Stage 'DNS_RULE'
        $dnsRule = Add-DnsClientNrptRule -Namespace '.' -NameServers '1.1.1.1','1.0.0.1' -Comment $dnsComment -PassThru
        Enter-Stage 'DNS_CACHE'
        Clear-DnsClientCache
    }
    Enter-Stage 'ROUTE_CHECK'
    $routeDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        if (Test-StopRequested) { throw [OperationCanceledException]::new() }
        $routeCheckAddress = if ($SplitTunnelMode -eq 'ProxySelected') { $splitIps[0] } else { '1.1.1.1' }
        $probeRoute = Find-NetRoute -RemoteIPAddress $routeCheckAddress | Where-Object { $_.PSObject.Properties.Name -contains 'NextHop' } | Select-Object -First 1
        if ($probeRoute -and $probeRoute.InterfaceIndex -eq $index) { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $routeDeadline)
    if (-not $probeRoute -or $probeRoute.InterfaceIndex -ne $index) { throw 'Tunnel route is not selected' }
    if ($SplitTunnelMode -eq 'ProxySelected') {
        Emit 'CONNECTED'
        while (-not (Test-StopRequested)) {
            $owner.Refresh(); $xrayProcess.Refresh()
            if ($owner.HasExited -or $owner.StartTime -ne $ownerStart -or $xrayProcess.HasExited) { break }
            if ((Get-NetAdapter -InterfaceIndex $upstream.InterfaceIndex).Status -ne 'Up') { Emit 'ERROR_NETWORK_CHANGED'; break }
            Refresh-SplitRoutesIfDue
            Start-Sleep -Milliseconds 500
        }
        return
    }
    Enter-Stage 'PROBE'
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    try {
        $probeCancellation = [Threading.CancellationTokenSource]::new()
        $probeTask = $client.GetAsync('https://1.1.1.1/cdn-cgi/trace', $probeCancellation.Token)
        while (-not $probeTask.IsCompleted) {
            if (Test-StopRequested) { $probeCancellation.Cancel(); throw [OperationCanceledException]::new() }
            Start-Sleep -Milliseconds 100
        }
        $probe = $probeTask.GetAwaiter().GetResult()
        $probe.EnsureSuccessStatusCode() | Out-Null
        $body = $probe.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($body -notmatch '(?m)^ip=') { throw 'Probe response invalid' }
        $probe.Dispose()
    } finally { $client.Dispose(); $handler.Dispose(); if ($probeCancellation) { $probeCancellation.Dispose() } }
    Emit 'CONNECTED'
    while (-not (Test-StopRequested)) {
        $owner.Refresh(); $xrayProcess.Refresh()
        if ($owner.HasExited -or $owner.StartTime -ne $ownerStart -or $xrayProcess.HasExited) { break }
        if ((Get-NetAdapter -InterfaceIndex $upstream.InterfaceIndex).Status -ne 'Up') { Emit 'ERROR_NETWORK_CHANGED'; break }
        Refresh-SplitRoutesIfDue
        Start-Sleep -Milliseconds 500
    }
} catch [OperationCanceledException] { Emit 'CANCELLED' }
catch {
    # Send only allowlisted stage/type information, never command arguments or server credentials.
    Emit ('ERROR_STAGE_' + $stage)
    Emit ('ERROR_CODE_' + $_.Exception.GetType().Name + '_' + $_.Exception.HResult)
    Emit ('ERROR_CATEGORY_' + $_.CategoryInfo.Category)
    $failedCommand = $_.InvocationInfo.MyCommand.Name
    if ($failedCommand -match '^(Add-DnsClientNrptRule|Clear-DnsClientCache|Find-NetRoute|Get-NetIPAddress|New-NetIPAddress|New-NetRoute|Set-DnsClientServerAddress|Set-NetIPInterface)$') {
        Emit ('ERROR_COMMAND_' + $failedCommand)
    }
    if ($_.Exception.PSObject.Properties.Name -contains 'NativeErrorCode') { Emit ('ERROR_NATIVE_' + [int]$_.Exception.NativeErrorCode) }
    Emit 'ERROR_TUN'
}
finally {
    Emit 'STAGE_CLEANUP'
    # Keep the independent host alive through rollback even if the UI disappeared.
    if ($locked) {
        foreach ($route in $ownedRoutes) {
            try {
                Get-NetRoute -DestinationPrefix $route.DestinationPrefix -InterfaceIndex $route.InterfaceIndex -NextHop $route.NextHop -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
                    Remove-NetRoute -Confirm:$false -ErrorAction Stop
            } catch { $cleanupFailed = $true }
        }
        try {
            Get-DnsClientNrptRule | Where-Object { $_.Comment -eq $dnsComment } | Remove-DnsClientNrptRule -Force -ErrorAction Stop
            Clear-DnsClientCache
        } catch { $cleanupFailed = $true }
        try { if ($xrayProcess -and -not $xrayProcess.HasExited) { $xrayProcess.Kill(); $xrayProcess.WaitForExit() } } catch { $cleanupFailed = $true }
        if ($xrayProcess) { $xrayProcess.Dispose() }
        try { Remove-Item -LiteralPath $ConfigurationPath -Force -ErrorAction Stop } catch { $cleanupFailed = $true }
        Remove-Item -LiteralPath $stopPath -Force -ErrorAction SilentlyContinue
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
    if ($cleanupFailed) { Emit 'ERROR_CLEANUP' } else { Emit 'STOPPED' }
}
