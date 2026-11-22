# Bots stay home check (Niduus, 2026-11-22: "I fear that someone may try them on live realm and that's not the mission").
#
# The bots play only inside Niduus's own Imlight server, through its pretend game (headless sessions). They have no
# network client and no login of their own, and this check keeps it that way: it fails (exit 1) when anything in the
# bots folder would give them a way to the official game. It runs in the Build check on every pull request and before
# every bot update is published (bots-release.yml), so such a change can be neither merged nor shipped.
#
# Run it by hand from the repository folder:  pwsh tools/bots-home-check/check.ps1
#
# What fails:
#   - the official game's addresses (wizard101.com, kingsisle.com, gameforge.com) anywhere in the bots' code;
#   - the game's login and patch messages (LOGIN_*_PROTOCOL, PATCH_*_PROTOCOL, MSG_USER_*): a bot never logs in the way
#     the game client does, the Imlight server lets it in from the inside;
#   - a network line that leaves this PC: ConnectAsync / TcpClient.Connect to anything but 127.0.0.1, a TcpListener not
#     on IPAddress.Loopback, raw sockets, UDP, web sockets, DNS look-ups;
#   - a web client (HttpClient, WebRequest) outside Ultimus's chat AI, which only talks to this PC or the home network
#     (UltimusLocalAI.Local), and the kill switch, which only reads bots-killswitch.txt from Niduus/Imlight (BotKillSwitch);
#   - BotHomeGuard taken out of the bots' start (BotsSide) or out of the bots' way into the game (QuestTeamHost.OpenSession).

param([string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path)

$bots = Join-Path $Root 'bots'
$problems = New-Object System.Collections.Generic.List[string]

# The guard itself names the official addresses so it can refuse them.
$guardFile = 'BotShared/BotHomeGuard.cs'
# The only file that may hold a web client: Ultimus's chat AI (this PC or the home network only).
$webAllowed = @('Ultimus/UltimusLocalAI.cs', 'BotShared/BotKillSwitch.cs')

$rules = @(
    @{ Name = "names the official game's address"; Pattern = '(?i)wizard101\.com|kingsisle\.com|gameforge\.com'; Skip = @($guardFile) },
    @{ Name = "uses the game's login or patch messages"; Pattern = '\b(LOGIN|PATCH)_\d+_PROTOCOL\b|\bMSG_USER_[A-Z_]+\b'; Skip = @() },
    @{ Name = 'opens a raw socket, UDP or web socket'; Pattern = '\bnew\s+Socket\s*\(|\bUdpClient\b|\bClientWebSocket\b|\bWebSocket\b'; Skip = @() },
    @{ Name = 'looks up a name on the internet (DNS)'; Pattern = '\bDns\.'; Skip = @() },
    @{ Name = 'uses a web client'; Pattern = '\bHttpClient\b|\bWebRequest\b|\bWebClient\b'; Skip = $webAllowed }
)

$files = Get-ChildItem $bots -Recurse -File -Filter *.cs | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($bots, $file.FullName).Replace('\', '/')
    $number = 0
    foreach ($line in Get-Content $file.FullName) {
        $number++
        foreach ($rule in $rules) {
            if ($rule.Skip -contains $relative) { continue }
            if ($line -match $rule.Pattern) { $problems.Add("bots/${relative}:${number} $($rule.Name): $($line.Trim())") }
        }

        # A connection or listener must stay on this PC.
        if ($line -match '\.Connect(Async)?\s*\((?!\s*\))' -and $line -notmatch '"127\.0\.0\.1"|IPAddress\.Loopback') {
            $problems.Add("bots/${relative}:${number} connects to somewhere other than this PC: $($line.Trim())")
        }

        if ($line -match '\bnew\s+TcpListener\s*\(' -and $line -notmatch 'IPAddress\.Loopback') {
            $problems.Add("bots/${relative}:${number} listens on more than this PC: $($line.Trim())")
        }
    }
}

# The guard must stay where it is.
$guard = Join-Path $bots $guardFile
if (-not (Test-Path $guard)) {
    $problems.Add("bots/$guardFile is missing")
}

$side = Get-Content (Join-Path $bots 'BotsSide.cs') -Raw
foreach ($method in @('Start', 'GameServerReady', 'StartWatching', 'RunMiniServer')) {
    $body = [regex]::Match($side, "internal override (void|bool) $method\([^)]*\)\s*(\{|=>)[\s\S]{0,600}")
    if (-not $body.Success -or $body.Value -notmatch 'BotHomeGuard\.Home') {
        $problems.Add("bots/BotsSide.cs: $method no longer checks BotHomeGuard.Home first")
    }
}

$hostCode = Get-Content (Join-Path $bots 'QuestTeam/QuestTeamHost.cs') -Raw
$open = [regex]::Match($hostCode, 'OpenSession\(IActorRef client, string who\)\s*\{[\s\S]{0,400}')
if (-not $open.Success -or $open.Value -notmatch 'BotHomeGuard\.Home') {
    $problems.Add('bots/QuestTeam/QuestTeamHost.cs: OpenSession no longer checks BotHomeGuard.Home')
}

if ($problems.Count -gt 0) {
    Write-Host "The bots must only ever play on your own Imlight server. This change would give them a way out:"
    $problems | ForEach-Object { Write-Host "  $_"; if ($env:GITHUB_ACTIONS) { Write-Host "::error::$_" } }
    exit 1
}

Write-Host "Bots stay home: $($files.Count) files checked, no way to the official game found."
exit 0
