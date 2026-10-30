#Requires -Version 7.3
#Requires -RunAsAdministrator
# The stress run's first machine, the one the stack runs on (ADR-172). It
# brings the stack up with the stress override and the remote one, waits for
# generator.ps1 to ask for the run, opens the stack's three ports to that
# machine alone, takes its raw.json back, closes the ports and summarises.
#
#   pwsh -File deploy\observability\stress\remote\stack.ps1
#   pwsh -File deploy\observability\stress\remote\stack.ps1 -Restore
#
# The firewall rules go when the run ends, however it ends; the ports stay
# bound to the LAN address, so Grafana still shows the run, until -Restore
# brings the stack up without the overrides, every port on loopback again,
# as the README's step 4 does.

[CmdletBinding()]
param(
    # The generator's address. Unset, the first machine on this subnet to ask
    # for the run is the generator, and the stack's ports open to it alone.
    [string]$GeneratorAddress,

    # Skips the build, for a rerun on images already built from this commit.
    [switch]$NoBuild,

    # Closes the ports and puts every one back on loopback.
    [switch]$Restore
)

. (Join-Path $PSScriptRoot 'common.ps1')

$FirewallGroup = 'Commerce stress run'
$SqlContainer = 'commerce-sql-1'
$BaseFile = Join-Path $ComposeDir 'docker-compose.yml'
$StressFile = Join-Path $ComposeDir 'docker-compose.stress.yml'

function Remove-StressFirewall {
    Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

# The results port first, to the subnet or the one generator; the stack's
# ports, which carry the realm's admin console, only once the generator is known.
function Open-StressFirewall {
    param([string]$Address, [string]$Remote, [switch]$Stack)

    $common = @{
        Group = $FirewallGroup
        Direction = 'Inbound'
        Protocol = 'TCP'
        LocalAddress = $Address
        RemoteAddress = $Remote
        Profile = 'Private'
        Action = 'Allow'
    }
    New-NetFirewallRule @common -DisplayName "$FirewallGroup, results" -LocalPort $ResultsPort | Out-Null

    if ($Stack) {
        $ports = 'gateway', 'keycloak', 'grafana' | ForEach-Object { Get-RemotePort $_ }
        New-NetFirewallRule @common -DisplayName "$FirewallGroup, stack" -LocalPort $ports | Out-Null
    }
}

# Every port the stack publishes beyond loopback, as "service address:port".
function Get-OpenPublishers {
    $lines = Invoke-Native { docker compose -f $BaseFile ps --format json } 'Listing the stack'
    foreach ($line in @($lines | Where-Object { $_ -and $_.Trim() })) {
        foreach ($container in @($line | ConvertFrom-Json)) {
            foreach ($publisher in @($container.Publishers | Where-Object { $_ })) {
                if ($publisher.PublishedPort -and $publisher.URL -ne '127.0.0.1') {
                    "$($container.Service) $($publisher.URL):$($publisher.PublishedPort)"
                }
            }
        }
    }
}

foreach ($tool in 'docker', 'node', 'git') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool is not on PATH."
    }
}

if ($Restore) {
    Remove-StressFirewall
    Invoke-Native { docker compose -f $BaseFile up -d --wait } 'Bringing the stack up without the overrides' -Stream

    $open = @(Get-OpenPublishers)
    if ($open.Count -gt 0) {
        throw "Still published beyond loopback: $($open -join ', ')."
    }
    Write-Host 'The firewall rules are gone and every published port is on loopback again.'
    return
}

$lan = Get-LanAddress
if ($lan.Category -ne 'Private') {
    throw ("$($lan.Interface) is on a $($lan.Category) network, and the stack's development credentials are offered " +
        'on a Private one alone (§14.1). On a network you trust, make it Private first: ' +
        "Set-NetConnectionProfile -InterfaceAlias '$($lan.Interface)' -NetworkCategory Private")
}
$firewall = Get-NetFirewallProfile -Name 'Private'
if (-not $firewall.Enabled -or "$($firewall.DefaultInboundAction)" -eq 'Allow') {
    throw ("Windows Firewall's Private profile is off or admits inbound connections by default, so nothing would " +
        "hold the stack's ports to the generator.")
}

# Docker Desktop's own programs accept the published ports, so a rule already
# naming one would let every peer in past the run's rule, or block it.
$dockerRules = @(Get-NetFirewallApplicationFilter -PolicyStore ActiveStore |
    Where-Object { $_.Program -match '(?i)docker|wslrelay|vpnkit' } |
    Get-NetFirewallRule |
    Where-Object { $_.Direction -eq 'Inbound' -and $_.Enabled -eq 'True' -and
        ($_.Profile -eq 'Any' -or "$($_.Profile)" -match 'Private') })
if ($dockerRules.Count -gt 0) {
    $names = ($dockerRules | ForEach-Object { "'$($_.DisplayName)' ($($_.Action))" }) -join ', '
    throw ("Inbound firewall rules already name Docker's programs on the Private profile: $names. " +
        'An allow lets the whole network past the run, a block stops the generator. Disable each for the run, ' +
        "Disable-NetFirewallRule -DisplayName '<name>', and enable it again afterwards if you want it.")
}
if ($GeneratorAddress -and -not (Test-SameSubnet $lan.Address $GeneratorAddress $lan.PrefixLength)) {
    throw "$GeneratorAddress is not on this machine's subnet, $($lan.Address)/$($lan.PrefixLength)."
}
Write-Host "This machine: $($lan.Address)/$($lan.PrefixLength) on $($lan.Interface), a $($lan.Category) network."

$commit = (Invoke-Native { git -C $RepoRoot rev-parse HEAD } 'Reading the commit').Trim()
if (Invoke-Native { git -C $RepoRoot status --porcelain } 'Reading the tree') {
    Write-Warning "The tree has uncommitted changes, which the stack is built from but the summary's commit, $commit, does not hold."
}

$env:STRESS_BIND_ADDRESS = $lan.Address
$build = if ($NoBuild) { @() } else { @('--build') }
Invoke-Native { docker compose -f $BaseFile -f $StressFile -f $RemoteOverride up -d @build --wait } `
    'Bringing the stack up with the stress overrides' -Stream
Write-Host "Letting the stack idle a minute, as the run's step 1 does."
Start-Sleep -Seconds 60

$sql = Get-Content -Raw (Join-Path $StressDir 'products.sql')
$rows = Invoke-Native {
    docker exec $SqlContainer sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec "$@"' sh `
        /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -d Ordering -h -1 -W -s ',' -Q $sql
} 'Choosing the products'
$pairs = @($rows | ForEach-Object { "$_".Trim() } | Where-Object { $_ -match '^[0-9a-f-]{36},[A-Z]{3}$' } |
    ForEach-Object { , ($_ -split ',') })
if ($pairs.Count -eq 0) {
    throw 'Ordering holds no seeded product the run can order.'
}
$currency = $pairs[0][1]
$products = ($pairs | Where-Object { $_[1] -eq $currency } | ForEach-Object { $_[0] }) -join ','

$run = Get-Date -Format 'yyyyMMdd-HHmmss'
$outRelative = "artifacts/stress/$run"
$out = Join-Path $RepoRoot $outRelative
$raw = Join-Path $out 'raw.json'
New-Item -ItemType Directory -Force $out | Out-Null
$plan = [ordered]@{ run = $run; out = $outRelative; commit = $commit; products = $products; currency = $currency } |
    ConvertTo-Json -Compress

$generator = $GeneratorAddress
$sampler = $null
$aborted = $false

# One request a connection: PLAN learns the generator and starts the sampler,
# as k6 is about to start; RESULT is raw.json, written whole or not at all.
function Receive-Request {
    param([System.Net.Sockets.TcpClient]$Client)

    $peer = ([System.Net.IPEndPoint]$Client.Client.RemoteEndPoint).Address.MapToIPv4().ToString()
    if (($script:generator -and $peer -ne $script:generator) -or
        -not (Test-SameSubnet $lan.Address $peer $lan.PrefixLength)) {
        Write-Warning "Refused $peer, which is not this run's generator."
        return
    }

    $Client.ReceiveTimeout = 30000
    $Client.SendTimeout = 30000
    $stream = $Client.GetStream()
    $reader = [System.IO.StreamReader]::new($stream, $Utf8, $false, 65536, $true)
    $writer = [System.IO.StreamWriter]::new($stream, $Utf8, 65536, $true)
    $writer.NewLine = "`n"
    $request = $reader.ReadLine()

    if ($request -eq $PlanRequest) {
        if (-not $script:generator) {
            $script:generator = $peer
            Remove-StressFirewall
            Open-StressFirewall -Address $lan.Address -Remote $peer -Stack
            Write-Host "The generator is $peer; the stack's ports are open to it alone."
        }
        if (-not $script:sampler) {
            $script:sampler = Start-Process node -NoNewWindow -PassThru -WorkingDirectory $RepoRoot `
                -ArgumentList "`"$(Join-Path $StressDir 'sample.mjs')`"", "`"$out`"" `
                -RedirectStandardError (Join-Path $out 'sampler.log')
            Write-Host 'The sampler is running.'
        }
        $writer.WriteLine($plan)
    } elseif ($request -eq $AbortRequest -and $script:generator) {
        $script:aborted = $true
        $writer.WriteLine('OK')
        Write-Warning "$peer ended the run without raw.json."
    } elseif ($request -eq $ResultRequest -and $script:sampler) {
        $body = Read-Bounded $reader $ResultLimitChars
        $null = $body | ConvertFrom-Json
        [System.IO.File]::WriteAllText("$raw.partial", $body, $Utf8)
        Move-Item -Force "$raw.partial" $raw
        $writer.WriteLine('OK')
        Write-Host "raw.json is back from $peer."
    } else {
        Write-Warning "Refused $peer's request '$request'."
    }
    $writer.Flush()
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse($lan.Address), $ResultsPort)
try {
    Remove-StressFirewall
    if ($generator) {
        Open-StressFirewall -Address $lan.Address -Remote $generator -Stack
    } else {
        Open-StressFirewall -Address $lan.Address -Remote 'LocalSubnet'
    }
    $listener.Start()

    Write-Host ''
    Write-Host "Ready. On the second machine, in its checkout of $commit, run:"
    Write-Host "  pwsh -File deploy\observability\stress\remote\generator.ps1 -StackAddress $($lan.Address)"
    Write-Host ''

    while (-not (Test-Path $raw) -and -not $aborted) {
        if (-not $listener.Pending()) {
            Start-Sleep -Milliseconds 250
            continue
        }
        $client = $listener.AcceptTcpClient()
        try {
            Receive-Request $client
        } catch {
            Write-Warning "A request failed: $_"
        } finally {
            $client.Dispose()
        }
    }

    if ($aborted) {
        throw 'The generator ended the run without raw.json, so there is nothing to summarise.'
    }
    Write-Host 'Waiting for the sampler to stop.'
    $sampler.WaitForExit()
} finally {
    $listener.Stop()
    Remove-StressFirewall
    Write-Host 'The firewall rules are gone.'
    if ($sampler -and -not $sampler.HasExited) {
        $sampler.Kill()
    }
}

Invoke-Native { node (Join-Path $StressDir 'summarise.mjs') $out } 'Summarising the run' -Stream
Write-Host ''
Write-Host "The stack's three ports are still bound to $($lan.Address), though no firewall rule lets the LAN in now."
Write-Host 'Once done with Grafana, put every port back on loopback:'
Write-Host '  pwsh -File deploy\observability\stress\remote\stack.ps1 -Restore'
