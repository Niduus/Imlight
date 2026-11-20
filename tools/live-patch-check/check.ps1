# Live patch check (src/Imlight.CoreLib/Game/LivePatches): builds the server from -Base (a full release tag) the way
# Ahziael does with Live patches on, then has that server try to put this checkout's server code into itself.
#   CAN GO LIVE          the fix goes into running servers without a restart
#   NEEDS A FULL UPDATE  it waits for the next full update (the reason says why, e.g. a deleted field)
# The repository's generator inputs must be in place (generator-inputs.ps1 on GitHub). -Strict: exit 1 unless it can go live.
param(
    [Parameter(Mandatory = $true)][string]$Base,
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$work = Join-Path ([IO.Path]::GetTempPath()) "live-patch-check-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$baseTree = Join-Path $work 'base'
$server = Join-Path $work 'server'
$rid = if ($IsWindows) { 'win-x64' } else { 'linux-x64' }

git -C $repo worktree add --detach $baseTree $Base | Out-Null
try {
    git -C $baseTree submodule update --init --recursive | Out-Null
    foreach ($part in 'Imcodec.ObjectProperty', 'Imcodec.MessageLayer') {
        $from = Join-Path $repo "submodule/Imcodec/src/$part/GeneratorInput"
        $to = Join-Path $baseTree "submodule/Imcodec/src/$part/GeneratorInput"
        New-Item -ItemType Directory -Force $to | Out-Null
        Copy-Item "$from/*" $to -Force
    }

    dotnet publish (Join-Path $baseTree 'src/Imlight.Director/Imlight.Director.csproj') -c Release -r $rid --self-contained true `
        -o $server -nologo -v q -p:LivePatches=true
    if ($LASTEXITCODE -ne 0) { throw "The server from $Base did not build." }

    if (-not (Test-Path (Join-Path $server 'live-patches/baseline/manifest.txt'))) {
        Write-Host "::notice::$Base was released before live patches, so there is nothing to check against yet."
        exit 0
    }

    $env:DOTNET_MODIFIABLE_ASSEMBLIES = 'debug'
    $exe = Join-Path $server ($(if ($IsWindows) { 'Imlight.Director.exe' } else { 'Imlight.Director' }))
    $output = & $exe --live-patch-check $repo 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    Remove-Item Env:DOTNET_MODIFIABLE_ASSEMBLIES
    $verdict = @($output | Where-Object { $_ -match '^(CAN GO LIVE|NEEDS A FULL UPDATE|NOTE|CHECK FAILED)' })
    $verdict | ForEach-Object { Write-Host $_ }
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content $env:GITHUB_STEP_SUMMARY "### Live patch check (against $Base)`n"
        $verdict | ForEach-Object { Add-Content $env:GITHUB_STEP_SUMMARY "- $_" }
    }

    if ($code -eq 2) { Write-Host "::warning::This fix needs a full update; it can't go into running servers as a live patch. $($verdict -join ' ')" }
    elseif ($code -ne 0) { Write-Host "::warning::The live patch check could not run: $($output | Select-Object -Last 5)" }
    if ($Strict -and $code -ne 0) { exit 1 }
    exit 0
}
finally {
    git -C $repo worktree remove --force $baseTree 2>$null | Out-Null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
