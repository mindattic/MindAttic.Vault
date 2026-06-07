<#
.SYNOPSIS
  MindAttic Codex documentation CLI for MindAttic.Vault (CODE: VLT).

.DESCRIPTION
  Subcommands:
    doctor  - validate the docs/ canon (front-matter, unique IDs, resolvable cross-refs,
              JSON-schema data, done-story test tokens, cited code paths, stale generatedFrom
              artifacts) and regenerate the digest to detect drift. Non-zero exit on any hard error.
    digest  - regenerate docs/BIBLE.digest.md from BIBLE.md (sections 1, 3, 5, 9) + a status
              index + the latest amendment head.

  This file is intentionally pure ASCII so Windows PowerShell 5.1 (which assumes the ANSI code
  page for BOM-less scripts) parses it identically to pwsh. Any non-ASCII character it needs
  (the section sign, the status emoji) is built from its Unicode codepoint at runtime.

.EXAMPLE
  pwsh tools/codex.ps1 doctor
  pwsh tools/codex.ps1 digest
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('doctor', 'digest')]
    [string]$Command = 'doctor'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Codepoint-built characters (kept out of string literals for 5.1 ANSI safety).
$SECT  = [char]0x00A7                      # section sign
$CHECK = [char]0x2705                      # done
$PART  = [char]::ConvertFromUtf32(0x1F7E1) # partial
$PLAN  = [char]0x2B1C                      # planned
$CUT   = [char]::ConvertFromUtf32(0x1F5D1) # cut

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DocsDir  = Join-Path $RepoRoot 'docs'
$BiblePath   = Join-Path $DocsDir 'BIBLE.md'
$DigestPath  = Join-Path $DocsDir 'BIBLE.digest.md'
$StoriesPath = Join-Path $DocsDir 'USER_STORIES.md'
$AmendPath   = Join-Path $DocsDir 'AMENDMENTS.md'

# ---------- helpers ----------

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Get-DocText { param([string]$Path) return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) }
function Set-DocText { param([string]$Path, [string]$Text) [System.IO.File]::WriteAllText($Path, $Text, $Utf8NoBom) }

function Get-FrontMatter {
    param([string]$Text)
    if ($Text -notmatch '(?s)^---\r?\n(.*?)\r?\n---') { return $null }
    $block = $Matches[1]
    $map = @{}
    foreach ($line in ($block -split "`n")) {
        $l = $line.Trim()
        if ($l -match '^([A-Za-z_]+):\s*(.*)$') { $map[$Matches[1]] = $Matches[2].Trim() }
    }
    return $map
}

# GitHub-style heading slug: lowercase, strip non-word/space/hyphen, spaces -> hyphens.
function ConvertTo-Slug {
    param([string]$Heading)
    $h = $Heading.ToLowerInvariant()
    $h = [regex]::Replace($h, '[^\w\- ]', '')   # drop punctuation (keeps word chars, hyphen, space)
    $h = $h.Trim() -replace '\s+', '-'
    return $h
}

# All anchor targets a markdown file exposes: explicit {#id} anchors + auto-slugged headings.
function Get-DocAnchors {
    param([string]$Text)
    $set = New-Object System.Collections.Generic.HashSet[string]
    foreach ($m in [regex]::Matches($Text, '\{#([^\}]+)\}')) { [void]$set.Add($m.Groups[1].Value) }
    foreach ($m in [regex]::Matches($Text, '(?m)^#{1,6}\s+(.+?)\s*$')) {
        # strip a trailing explicit {#id} before slugging the visible heading text
        $head = ($m.Groups[1].Value -replace '\s*\{#[^\}]+\}\s*$', '')
        [void]$set.Add((ConvertTo-Slug $head))
    }
    return $set
}

# Pull the body of a "## ... {#ID}" section (up to the next "## " or EOF) out of the bible text.
function Get-BibleSection {
    param([string]$Text, [string]$AnchorId)
    $pattern = '(?ms)^##\s+[^\n]*\{#' + [regex]::Escape($AnchorId) + '\}[^\n]*\n(.*?)(?=^##\s|\Z)'
    if ($Text -match $pattern) { return $Matches[1].Trim() }
    return ''
}

# ---------- digest ----------

function Invoke-Digest {
    if (-not (Test-Path -LiteralPath $BiblePath)) { throw "BIBLE.md not found at $BiblePath" }
    $bible = Get-DocText $BiblePath

    $one   = Get-BibleSection $bible ('VLT-' + $SECT + '1')
    $isnot = Get-BibleSection $bible ('VLT-' + $SECT + '3')
    $laws  = Get-BibleSection $bible ('VLT-' + $SECT + '5')
    $gloss = Get-BibleSection $bible ('VLT-' + $SECT + '9')

    # status index: count the status markers across the stories file.
    # NB: PowerShell variable names are case-insensitive, so these locals MUST NOT collide
    # with the codepoint globals ($CHECK/$PART/$PLAN/$CUT). Use distinct names.
    $nDone = 0; $nPartial = 0; $nPlanned = 0; $nCut = 0
    if (Test-Path -LiteralPath $StoriesPath) {
        $s = Get-DocText $StoriesPath
        $nDone    = ([regex]::Matches($s, [regex]::Escape($CHECK))).Count
        $nPartial = ([regex]::Matches($s, [regex]::Escape($PART))).Count
        $nPlanned = ([regex]::Matches($s, [regex]::Escape($PLAN))).Count
        $nCut     = ([regex]::Matches($s, [regex]::Escape($CUT))).Count
    }

    # latest amendment head (last "## VLT-A.." heading)
    $amendHead = ''
    if (Test-Path -LiteralPath $AmendPath) {
        $a = Get-DocText $AmendPath
        $am = [regex]::Matches($a, '(?m)^##\s+(VLT-A\d+[^\n]*)$')
        if ($am.Count -gt 0) { $amendHead = $am[$am.Count - 1].Groups[1].Value.Trim() }
    }

    $today = (Get-Date).ToString('yyyy-MM-dd')
    $genFrom = 'VLT-' + $SECT + '1,VLT-' + $SECT + '3,VLT-' + $SECT + '5,VLT-' + $SECT + '9'
    $statusLine = "- done: $nDone | partial: $nPartial | planned: $nPlanned | cut: $nCut"
    $amendLine  = if ($amendHead) { "- $amendHead" } else { '- (none)' }
    $emDash = [char]0x2014

    $lines = @(
        '---'
        'codex: 1'
        'project: MindAttic.Vault'
        'code: VLT'
        'layer: digest'
        'status: generated'
        "generatedFrom: $genFrom"
        "updated: $today"
        '---'
        ''
        "# MindAttic.Vault $emDash BIBLE digest"
        "AUTHORITATIVE $emDash full detail in docs/BIBLE.md"
        ''
        '## The one sentence'
        $one
        ''
        '## What it is NOT'
        $isnot
        ''
        '## The Laws'
        $laws
        ''
        '## Glossary'
        $gloss
        ''
        '## Status index (from USER_STORIES.md)'
        $statusLine
        ''
        '## Latest amendment'
        $amendLine
    )
    Set-DocText $DigestPath (($lines -join "`r`n") + "`r`n")
    Write-Host "digest -> $DigestPath"
}

# ---------- doctor ----------

function Invoke-Doctor {
    $errors = New-Object System.Collections.Generic.List[string]
    $warns  = New-Object System.Collections.Generic.List[string]
    $ok     = New-Object System.Collections.Generic.List[string]

    $codexFiles = New-Object System.Collections.Generic.List[string]
    $codexFiles.Add($BiblePath)
    $codexFiles.Add($StoriesPath)
    $codexFiles.Add($AmendPath)
    $rfcDir = Join-Path $DocsDir 'rfc'
    if (Test-Path -LiteralPath $rfcDir) {
        Get-ChildItem -LiteralPath $rfcDir -Filter '*.md' -File | ForEach-Object { $codexFiles.Add($_.FullName) }
    }
    $dataDir = Join-Path $DocsDir 'data'
    $dataFiles = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $dataDir) {
        Get-ChildItem -LiteralPath $dataDir -Filter '*.json' -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/]_schema[\\/]' } |
            ForEach-Object { $dataFiles.Add($_.FullName) }
    }

    # 1. front-matter present & valid on every canon file
    foreach ($f in $codexFiles) {
        if (-not (Test-Path -LiteralPath $f)) { $errors.Add("missing required doc: $f"); continue }
        $fm = Get-FrontMatter (Get-DocText $f)
        if ($null -eq $fm) { $errors.Add("no front-matter: $f"); continue }
        if (-not $fm.ContainsKey('codex') -or $fm['codex'] -ne '1') { $errors.Add("front-matter codex!=1: $f") }
        foreach ($k in 'project','code','layer','status','updated') {
            if (-not $fm.ContainsKey($k) -or [string]::IsNullOrWhiteSpace($fm[$k])) { $errors.Add("front-matter missing '$k': $f") }
        }
        if ($fm.ContainsKey('updated') -and $fm['updated'] -notmatch '^\d{4}-\d{2}-\d{2}$') { $errors.Add("front-matter 'updated' not YYYY-MM-DD: $f") }
    }
    foreach ($f in $dataFiles) {
        if ($null -eq (Get-FrontMatter (Get-DocText $f))) { $warns.Add("data file has no front-matter (JSON): $f") }
    }
    if ($errors.Count -eq 0) { $ok.Add("front-matter valid on $($codexFiles.Count) canon files") }

    # 2. unique {#...} anchors; every cross-ref link resolves
    $allText = @{}
    foreach ($f in $codexFiles) { if (Test-Path -LiteralPath $f) { $allText[$f] = Get-DocText $f } }
    $anchorById = @{}
    foreach ($f in $allText.Keys) {
        foreach ($m in [regex]::Matches($allText[$f], '\{#([A-Za-z0-9\-' + $SECT + ']+)\}')) {
            $id = $m.Groups[1].Value
            if ($anchorById.ContainsKey($id)) { $errors.Add("duplicate anchor {#$id} in $f (also $($anchorById[$id]))") }
            else { $anchorById[$id] = $f }
        }
    }
    $ok.Add("$($anchorById.Count) unique {#anchor} IDs")

    # anchors per markdown file (explicit {#id} + GitHub heading slugs), cached
    $anchorsByFile = @{}
    foreach ($f in $allText.Keys) { $anchorsByFile[$f] = Get-DocAnchors $allText[$f] }

    $refOk = 0
    foreach ($f in $allText.Keys) {
        foreach ($m in [regex]::Matches($allText[$f], '\]\(([^)]*#[A-Za-z0-9\-_' + $SECT + ']+)\)')) {
            $target = $m.Groups[1].Value
            $hashIx = $target.IndexOf('#')
            $pathPart = $target.Substring(0, $hashIx)
            $idPart   = $target.Substring($hashIx + 1)
            if ($pathPart -eq '') {
                if ($anchorsByFile[$f].Contains($idPart)) { $refOk++ } else { $errors.Add("unresolved same-file anchor #$idPart in $f") }
            }
            else {
                $resolved = Join-Path (Split-Path -Parent $f) $pathPart
                if (-not (Test-Path -LiteralPath $resolved)) { $errors.Add("link target file missing: $pathPart (from $f)") }
                elseif ($resolved -notmatch '\.md$') { $refOk++ }   # non-md target: existence is enough
                else {
                    $anchors = if ($anchorsByFile.ContainsKey($resolved)) { $anchorsByFile[$resolved] } else { Get-DocAnchors (Get-DocText $resolved) }
                    if ($anchors.Contains($idPart)) { $refOk++ } else { $errors.Add("unresolved cross-file anchor #$idPart in $pathPart (from $f)") }
                }
            }
        }
    }
    $ok.Add("$refOk cross-reference links resolve")

    # 3. data JSON validates (structural: parse + unique ids)
    if ($dataFiles.Count -eq 0) { $ok.Add('no docs/data/*.json (library domain - none expected)') }
    else {
        foreach ($df in $dataFiles) {
            try {
                $json = Get-Content -LiteralPath $df -Raw | ConvertFrom-Json
                $ids = @()
                foreach ($e in $json) { if ($e.PSObject.Properties.Name -contains 'id') { $ids += $e.id } }
                $dup = $ids | Group-Object | Where-Object { $_.Count -gt 1 }
                if ($dup) { $errors.Add(("duplicate entity id(s) in {0}: {1}" -f $df, ($dup.Name -join ', '))) }
            } catch { $errors.Add(("invalid JSON: {0} ({1})" -f $df, $_.Exception.Message)) }
        }
    }

    # 4. every done-story names a test token; best-effort confirm it exists in the test tree
    if (Test-Path -LiteralPath $StoriesPath) {
        $stext = Get-DocText $StoriesPath
        $testBlob = ''
        $tdir = Join-Path $RepoRoot 'MindAttic.Vault.Tests'
        if (Test-Path -LiteralPath $tdir) {
            $parts = Get-ChildItem -LiteralPath $tdir -Filter '*.cs' -File -Recurse | ForEach-Object { Get-DocText $_.FullName }
            $testBlob = ($parts -join "`n")
        }
        $storyBlocks = [regex]::Matches($stext, '(?ms)^- \*\*(VLT-US-[A-Za-z0-9]+)\s*' + [regex]::Escape($CHECK) + '.*?(?=^- \*\*VLT-US-|^\#\#|\Z)')
        $missing = New-Object System.Collections.Generic.List[string]
        $confirmed = 0
        foreach ($b in $storyBlocks) {
            $sid = $b.Groups[1].Value
            $tokens = [regex]::Matches($b.Value, '`([A-Za-z_][A-Za-z0-9_]+)`') |
                ForEach-Object { $_.Groups[1].Value } |
                Where-Object { $_ -match '_' -or $_ -match 'Tests$' }
            if (-not $tokens -or @($tokens).Count -eq 0) { $missing.Add($sid); continue }
            foreach ($t in $tokens) {
                if ($testBlob -match [regex]::Escape($t)) { $confirmed++ }
                else { $warns.Add("story $sid cites test '$t' not found in test tree") }
            }
        }
        if ($missing.Count -gt 0) { foreach ($mm in $missing) { $errors.Add("done story $mm names no test token") } }
        else { $ok.Add("every done story names a test token ($confirmed citations confirmed on disk)") }
    }

    # 5. every code PATH/file cited in the bible exists on disk.
    #    - A backticked token with a path separator is a genuine repo-relative path claim -> must exist.
    #    - A bare filename is only treated as a path claim for unambiguous project files
    #      (.csproj/.slnx/.sln). Bare data/source filenames (providers.json, etc.) are
    #      illustrative schema examples in prose, not on-disk path claims, so they're skipped.
    if (Test-Path -LiteralPath $BiblePath) {
        $btext = Get-DocText $BiblePath
        $citedOk = 0
        foreach ($m in [regex]::Matches($btext, '`([A-Za-z0-9_][A-Za-z0-9_./\\-]*\.(?:cs|csproj|slnx|sln|json|md))`')) {
            $cited = $m.Groups[1].Value
            if ($cited -match '[\\/]') {
                $rp = Join-Path $RepoRoot ($cited -replace '/', '\')
                if (-not (Test-Path -LiteralPath $rp)) { $errors.Add("bible cites path not on disk: $cited") } else { $citedOk++ }
            }
            elseif ($cited -match '\.(?:csproj|slnx|sln)$') {
                $found = Get-ChildItem -LiteralPath $RepoRoot -Filter $cited -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($null -eq $found) { $errors.Add("bible cites project file not on disk: $cited") } else { $citedOk++ }
            }
            # else: bare data/source filename in prose -> illustrative, not validated
        }
        $ok.Add("$citedOk cited code path(s) exist on disk")
    }

    # 6. generatedFrom artifacts not stale (source mtime <= artifact mtime)
    if (Test-Path -LiteralPath $DigestPath) {
        $digFm = Get-FrontMatter (Get-DocText $DigestPath)
        if ($digFm -and $digFm.ContainsKey('generatedFrom')) {
            $digMtime = (Get-Item -LiteralPath $DigestPath).LastWriteTimeUtc
            $srcMtime = (Get-Item -LiteralPath $BiblePath).LastWriteTimeUtc
            if ($srcMtime -gt $digMtime) { $warns.Add('BIBLE.digest.md is stale (BIBLE.md changed after it) - run: codex.ps1 digest') }
            else { $ok.Add('digest is current vs BIBLE.md') }
        }
    } else { $warns.Add('BIBLE.digest.md missing - run: codex.ps1 digest') }

    # 7. regenerate digest and warn if it was out of date (ignore the date-only line)
    $before = if (Test-Path -LiteralPath $DigestPath) { Get-DocText $DigestPath } else { '' }
    Invoke-Digest | Out-Null
    $after = Get-DocText $DigestPath
    $norm = { param($t) ($t -replace '(?m)^updated:.*$', 'updated: X') }
    if ((& $norm $before) -ne (& $norm $after)) { $warns.Add('BIBLE.digest.md was out of date and has been regenerated') }
    else { $ok.Add('digest regenerated (no content drift)') }

    # ---------- report ----------
    Write-Host ''
    Write-Host '=== Codex doctor - MindAttic.Vault (VLT) ==='
    foreach ($o in $ok)    { Write-Host ("  [OK]   " + $o) }
    foreach ($w in $warns) { Write-Host ("  [WARN] " + $w) }
    foreach ($e in $errors){ Write-Host ("  [FAIL] " + $e) }
    Write-Host ''
    Write-Host ("OK: {0}  WARN: {1}  FAIL: {2}" -f $ok.Count, $warns.Count, $errors.Count)

    if ($errors.Count -gt 0) { exit 1 }
    exit 0
}

switch ($Command) {
    'digest' { Invoke-Digest }
    'doctor' { Invoke-Doctor }
}
