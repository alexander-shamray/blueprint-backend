# Windows PowerShell wrapper around the installed `codebase-index` CLI.
# Mirrors scripts/cbx: whitelist, then PATH CLI, then py -3.12, then python -m.
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Subcommand,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$ErrorActionPreference = "Stop"
$allowed = @(
    "search", "explain", "architecture", "symbol", "refs", "impact", "diff-impact",
    "path", "describe", "verify", "graph", "stats", "doctor", "update", "index"
)

if ($allowed -notcontains $Subcommand) {
    [Console]::Error.WriteLine("cbx: refusing subcommand '$Subcommand'. Allowed: $($allowed -join ', ')")
    exit 2
}

$bin = Get-Command codebase-index -ErrorAction SilentlyContinue
if ($bin) {
    & $bin.Source $Subcommand @Rest
    exit $LASTEXITCODE
}
$pyLauncher = Get-Command py -ErrorAction SilentlyContinue
if ($pyLauncher) {
    & $pyLauncher.Source -3.12 -m codebase_index $Subcommand @Rest
    exit $LASTEXITCODE
}
$py = Get-Command python -ErrorAction SilentlyContinue
if ($py) {
    & $py.Source -m codebase_index $Subcommand @Rest
    exit $LASTEXITCODE
}
[Console]::Error.WriteLine("cbx: neither codebase-index nor python found on PATH")
exit 127
