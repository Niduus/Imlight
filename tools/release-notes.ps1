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
# a "Where:" line and a "Test:" (or "How to test:") line (see docs/release-notes/README.md).
# Notes written another way still read cleanly (Niduus, 2026-11-14: the patch notes "seem to become more... cryptic"):
# "# Heading" lines are left out, **bold** loses its stars, a "Where:" or "Test:" block on its own belongs to the note
# above it, steps under "Test:" (1. 2. 3.) are joined into one line, a "New:" with nothing after it gives each of its
# "- " lines as its own note, and any other block carries on the note above it.
function Read-NoteFile([string]$text) {
    $notes = [System.Collections.Generic.List[object]]::new()
    $kinds = '^(New|Fixed|Changed):\s*(.*)$'
    foreach ($block in (($text -replace "`r", '') -split "`n\s*`n")) {
        $lines = @($block -split "`n" | ForEach-Object { ($_ -replace '\*\*', '').Trim() } |
            Where-Object { $_ -and -not $_.StartsWith('#') -and -not $_.StartsWith('<!--') })
        if ($lines.Count -eq 0) { continue }

        # Which notes this block's lines go to.
        $targets = @()
        $rest = $lines
        if ($lines[0] -match $kinds -and -not $Matches[2]) {
            # "New:" alone, then "- " lines: one note each.
            $kind = $Matches[1]
            $rest = @(); $made = @()
            foreach ($line in ($lines | Select-Object -Skip 1)) {
                if ($line -match '^[-*]\s+(.+)$' -or $line -match '^\d+[.)]\s+(.+)$') {
                    $note = [pscustomobject]@{ Title = "${kind}: $($Matches[1])"; More = @(); Where = ''; Test = '' }
                    $notes.Add($note); $targets = @($note); $made += $note
                }
                elseif ($targets.Count -gt 0 -and $line -notmatch '^(Where|How to test|Test):') { $targets[0].More += $line }
                else { $rest += $line }
            }
            if ($rest.Count -eq 0) { continue }
            $targets = $made
        }
        elseif ($lines[0] -match $kinds) {
            $note = [pscustomobject]@{ Title = ($lines[0] -replace '^[-*]\s+', ''); More = @(); Where = ''; Test = '' }
            $notes.Add($note); $targets = @($note)
            $rest = @($lines | Select-Object -Skip 1)
        }
        elseif ($notes.Count -gt 0) {
            # "Where:", "Test:" or more words for the note above.
            $targets = @($notes[$notes.Count - 1])
        }
        else {
            $note = [pscustomobject]@{ Title = ($lines[0] -replace '^[-*]\s+', ''); More = @(); Where = ''; Test = '' }
            $notes.Add($note); $targets = @($note)
            $rest = @($lines | Select-Object -Skip 1)
        }

        $field = 'More'
        foreach ($line in $rest) {
            $text = $line -replace '^[-*]\s+', ''
            if ($line -match '^Where:\s*(.*)$') { $field = 'Where'; $text = $Matches[1] }
            elseif ($line -match '^(How to test|Test):\s*(.*)$') { $field = 'Test'; $text = $Matches[2] }
            elseif ($field -eq 'Where') { $field = 'More' }
            if (-not $text) { continue }
            foreach ($t in $targets) {
                if ($field -eq 'More') { $t.More += $text }
                elseif ($field -eq 'Where') { if (-not $t.Where) { $t.Where = $text } }
                else { $t.Test = (($t.Test, $text) | Where-Object { $_ }) -join ' ' }
            }
        }
    }

    $out = @()
    foreach ($n in $notes) {
        $note = $n.Title.Trim().TrimEnd('.', ':')
        if ($n.More.Count -gt 0) { $note += '. ' + ($n.More -join ' ') }
        $note = $note -replace '\s\|\s', ' / '
        if ($n.Where) { $note += " | Where: $($n.Where.TrimEnd('.') -replace '\s\|\s', ' / ')" }
        if ($n.Test) { $note += " | Test: $($n.Test -replace '\s\|\s', ' / ')" }
        $out += $note
    }
    return $out
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
            # The note as it reads now when the file is still there, so a note rewritten later reads that way for its
            # own version too (the app's What's New lists every version); else as it was added.
            $text = if (Test-Path $file) { Get-Content -Raw -Encoding utf8 $file } else { (@(git show "${hash}:$file") -join "`n") }
            $result += Read-NoteFile $text
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
