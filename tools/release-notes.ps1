# The patch notes between two points of history, one line per change, for the release's "What's new" list and the
# notes built into the app (Niduus, 2026-11-09: "Have actual notes for patches").
#
# A change that added a note file under docs/release-notes/ (see the README there) gives that note's lines, written as
# "New: Title. What it does | Where: Game Rules > What NPCs Give | Test: how to try it". Any other merged change gives
# its pull request's title, as before. Merges of a branch into another (not a pull request) give nothing.
#
#   pwsh tools/release-notes.ps1 -From v0.10.741 -To HEAD

param(
    [Parameter(Mandatory = $true)][string]$From,
    [Parameter(Mandatory = $true)][string]$To
)

$ErrorActionPreference = 'Stop'
# git writes UTF-8; read it as that on Windows too (› and accented names).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$notesFolder = 'docs/release-notes'

# One note file's notes: blocks split by empty lines; a block's first line is "New: Title", then lines of what it does,
# a "Where:" line and a "Test:" (or "How to test:") line.
function Read-NoteFile([string]$text) {
    $notes = @()
    foreach ($block in (($text -replace "`r", '') -split "`n\s*`n")) {
        $lines = @($block -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') -and -not $_.StartsWith('<!--') })
        if ($lines.Count -eq 0) { continue }
        $more = @(); $where = ''; $test = ''
        foreach ($line in ($lines | Select-Object -Skip 1)) {
            if ($line -match '^Where:\s*(.+)$') { $where = $Matches[1] }
            elseif ($line -match '^(How to test|Test):\s*(.+)$') { $test = $Matches[2] }
            else { $more += ($line -replace '^[-*]\s+', '') }
        }
        $note = ($lines[0] -replace '^[-*]\s+', '').Trim().TrimEnd('.')
        if ($more.Count -gt 0) { $note += '. ' + ($more -join ' ') }
        $note = $note -replace '\s\|\s', ' / '
        if ($where) { $note += " | Where: $($where -replace '\s\|\s', ' / ')" }
        if ($test) { $note += " | Test: $($test -replace '\s\|\s', ' / ')" }
        $notes += $note
    }
    return $notes
}

# Which changes on the main line added note files, and which files.
$added = @{}
$current = $null
foreach ($line in @(git log --first-parent --diff-merges=first-parent --diff-filter=A --name-only --pretty=format:'@%H' "$From..$To" -- $notesFolder)) {
    if ($line.StartsWith('@')) { $current = $line.Substring(1); continue }
    if ($line -and $current -and $line -notmatch 'README\.md$' -and $line.EndsWith('.md')) {
        if (-not $added.ContainsKey($current)) { $added[$current] = @() }
        $added[$current] += $line
    }
}

$result = @()
$log = (@(git log --first-parent --reverse --pretty=format:'%H%x1f%s%x1f%b%x1e' "$From..$To") -join "`n")
foreach ($record in ($log -split [char]0x1e)) {
    $fields = $record.Trim("`n") -split [char]0x1f
    if ($fields.Count -lt 2 -or -not $fields[0]) { continue }
    $hash = $fields[0].Trim(); $subject = $fields[1].Trim(); $body = if ($fields.Count -gt 2) { $fields[2] } else { '' }

    if ($added.ContainsKey($hash)) {
        foreach ($file in $added[$hash]) {
            $result += Read-NoteFile ((@(git show "${hash}:$file") -join "`n"))
        }
        continue
    }

    if ($subject -match '^Merge pull request') {
        $title = @($body -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) | Select-Object -First 1
    }
    elseif ($subject -match '^Merge ') { continue }
    else { $title = $subject }

    if ($title) { $result += ($title.Trim() -replace '\s*\(#\d+\)\s*$', '') }
}

$result
