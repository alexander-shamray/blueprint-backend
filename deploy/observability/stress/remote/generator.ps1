#Requires -Version 7.3
# The stress run's second machine, the one k6 runs on (ADR-172). It asks
# stack.ps1 for the run, runs k6 from the stress override's own image against
# the stack's LAN address, and hands raw.json back for the summary. Start
# stack.ps1 first; it prints this command once the stack is ready.
#
#   pwsh -File deploy\observability\stress\remote\generator.ps1 -StackAddress 192.168.1.20 -Token <printed>
#   pwsh -File deploy\observability\stress\remote\generator.ps1 -StackAddress 192.168.1.20 -Token <printed> -Level medium
#
# Nothing is opened here: this machine only connects out.

[CmdletBinding()]
param(
    # The stack's machine and the run's token, as stack.ps1 prints them.
    [Parameter(Mandatory)][string]$StackAddress,
    [Parameter(Mandatory)][string]$Token,

    # STRESS_PROFILE, STRESS_STOP, STRESS_MAX_USERS and STRESS_STEPS, as the
    # README's stress run names them; unset, stress.js's own defaults hold.
    [string]$Level,
    [string]$Stop,
    [string]$MaxUsers,
    [string]$Steps
)

. (Join-Path $PSScriptRoot 'common.ps1')

# One request a connection, the reply read to its end.
function Send-Request {
    param([Parameter(Mandatory)][string]$Request, [string]$Body)

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        try {
            $connected = $client.ConnectAsync($StackAddress, $ResultsPort).Wait(10000)
        } catch {
            $connected = $false
        }
        if (-not $connected) {
            throw "stack.ps1 does not answer on ${StackAddress}:$ResultsPort. Start it first, and wait for it to print Ready."
        }

        $client.ReceiveTimeout = 60000
        $stream = $client.GetStream()
        $writer = [System.IO.StreamWriter]::new($stream, $Utf8, 65536, $true)
        $writer.NewLine = "`n"
        $writer.WriteLine("$Request $Token")
        if ($Body) {
            $writer.Write($Body)
        }
        $writer.Flush()
        $writer.Dispose()
        $client.Client.Shutdown([System.Net.Sockets.SocketShutdown]::Send)

        $reader = [System.IO.StreamReader]::new($stream, $Utf8)
        $reply = (Read-Bounded $reader 1MB ([System.Diagnostics.Stopwatch]::StartNew())).Trim()
        if (-not $reply) {
            throw "stack.ps1 refused $Request from this machine; its window says why."
        }
        return $reply
    } finally {
        $client.Dispose()
    }
}

foreach ($tool in 'docker', 'git') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool is not on PATH."
    }
}

$lan = Get-LanAddress
if (-not (Test-SameSubnet $lan.Address $StackAddress $lan.PrefixLength)) {
    throw "$StackAddress is not on this machine's subnet, $($lan.Address)/$($lan.PrefixLength)."
}
Write-Host "This machine: $($lan.Address)/$($lan.PrefixLength) on $($lan.Interface)."

# The override's pinned image, so the version has one copy (ADR-170).
$override = Get-Content -Raw (Join-Path $ComposeDir 'docker-compose.stress.yml')
$image = [regex]::Match($override, '(?m)^  k6:\r?\n(?:    .*\r?\n)*?    image:\s*(\S+)')
if (-not $image.Success) {
    throw 'docker-compose.stress.yml names no image for its k6 service.'
}

# §14.1's local default, from the environment as the README's step 3 passes it, never on k6's command line.
if (-not $env:KEYCLOAK_ADMIN_PASSWORD) {
    $env:KEYCLOAK_ADMIN_PASSWORD = 'admin'
}

# k6 runs this checkout's scripts, which the summary names by the stack's commit alone.
if (Invoke-Native { git -C $RepoRoot status --porcelain -- deploy/observability/stress } 'Reading the tree') {
    throw "deploy/observability/stress has uncommitted changes here, which k6 would run under the stack's commit."
}

# Asking for the run opens the stack's ports to this machine, so from here on
# every way out but a delivered raw.json tells the stack to close them.
$plan = Send-Request $PlanRequest | ConvertFrom-Json
Write-Host "Run $($plan.run): $(@($plan.products -split ',').Count) products in $($plan.currency)."
$handedBack = $false
$exit = 1

try {
    # The summary names the stack's commit, so k6's scripts must be that commit's.
    $commit = (Invoke-Native { git -C $RepoRoot rev-parse HEAD } 'Reading the commit').Trim()
    if ($commit -ne $plan.commit) {
        throw "This checkout is at $commit and the stack's at $($plan.commit); check out the stack's commit first."
    }

    $out = Join-Path $RepoRoot $plan.out
    New-Item -ItemType Directory -Force $out | Out-Null
    $stack = "http://$StackAddress"

    $options = @()
    foreach ($pair in @(
            @('STRESS_PROFILE', $Level), @('STRESS_STOP', $Stop), @('STRESS_MAX_USERS', $MaxUsers),
            @('STRESS_STEPS', $Steps))) {
        if ($pair[1]) {
            $options += '-e', "$($pair[0])=$($pair[1])"
        }
    }

    $arguments = @(
        'run', '--rm',
        '-v', "$($StressDir):/repo/deploy/observability/stress:ro",
        '-v', "$(Join-Path $RepoRoot 'artifacts\stress'):/repo/artifacts/stress",
        '-w', '/repo',
        '-e', 'KEYCLOAK_ADMIN_PASSWORD',
        '-e', "BASE_URL=${stack}:$(Get-RemotePort 'gateway')",
        '-e', "KEYCLOAK_URL=${stack}:$(Get-RemotePort 'keycloak')",
        '-e', "PROM_URL=${stack}:$(Get-RemotePort 'grafana')/api/datasources/proxy/uid/prometheus",
        $image.Groups[1].Value,
        'run', '--address', '127.0.0.1:6565',
        '-e', "STRESS_PRODUCTS=$($plan.products)",
        '-e', "STRESS_CURRENCY=$($plan.currency)",
        '-e', "STRESS_OUT=$($plan.out)",
        '-e', 'STRESS_GENERATOR=remote'
    ) + $options + @('deploy/observability/stress/stress.js')

    & docker @arguments
    $exit = $LASTEXITCODE
    Write-Host "k6 exit: $exit"

    $raw = Join-Path $out 'raw.json'
    if (-not (Test-Path $raw)) {
        throw 'k6 wrote no raw.json, so there is nothing to hand back.'
    }
    $reply = Send-Request $ResultRequest (Get-Content -Raw $raw)
    if ($reply -ne 'OK') {
        throw "stack.ps1 answered '$reply' to raw.json."
    }
    $handedBack = $true
    Write-Host 'raw.json is on the stack machine, which now summarises the run.'
} finally {
    if (-not $handedBack) {
        try {
            Send-Request $AbortRequest | Out-Null
            Write-Host 'Told stack.ps1 the run ended without raw.json; it closes its ports.'
        } catch {
            Write-Warning "stack.ps1 did not take the abort ($_); stop it with Ctrl+C, which closes its ports."
        }
    }
}
exit $exit
