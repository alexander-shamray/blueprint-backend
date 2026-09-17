# Windows PowerShell wrapper around the installed `codebase-index` CLI.
# Mirrors scripts/cbx: whitelists safe subcommands, falls back to `python -m codebase_index`.
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
    Write-Error "cbx: refusing subcommand '$Subcommand'. Allowed: $($allowed -join ', ')"
    exit 2
}

$bin = Get-Command codebase-index -ErrorAction SilentlyContinue
if ($bin) {
    & $bin.Source $Subcommand @Rest
    exit $LASTEXITCODE
}
$py = Get-Command python, py -ErrorAction SilentlyContinue | Select-Object -First 1
if ($py) {
    & $py.Source -m codebase_index $Subcommand @Rest
    exit $LASTEXITCODE
}
Write-Error "cbx: neither codebase-index nor python found on PATH"
exit 127
