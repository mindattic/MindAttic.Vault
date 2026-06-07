<#
  SessionStart hook - inject the Codex BIBLE digest as authoritative context.
  Reads docs/BIBLE.digest.md and emits Claude Code hook JSON. PS 5.1 / Win-1252 safe:
  every non-ASCII char is escaped to \uXXXX so the JSON is pure ASCII on any code page.
  If the digest is missing or empty, emits {}.
#>
$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # .claude/hooks -> repo root
$digestPath = Join-Path $repoRoot 'docs\BIBLE.digest.md'

if (-not (Test-Path -LiteralPath $digestPath)) { Write-Output '{}'; exit 0 }

$digest = Get-Content -LiteralPath $digestPath -Raw -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($digest)) { Write-Output '{}'; exit 0 }

$preamble = @'
[MindAttic Codex] The following is the AUTHORITATIVE project digest for MindAttic.Vault (CODE: VLT),
generated from docs/BIBLE.md. Treat it as the source of truth for what this project IS, is NOT, and
its Laws. Full detail lives in docs/BIBLE.md; amendments in docs/AMENDMENTS.md win over the bible.
Do not contradict it; if a change is needed, amend the bible rather than working around it.

'@

$context = $preamble + $digest

# JSON-encode by hand with ASCII escaping (no dependency on ConvertTo-Json depth quirks).
$sb = New-Object System.Text.StringBuilder
foreach ($ch in $context.ToCharArray()) {
    $code = [int][char]$ch
    switch ($ch) {
        '"'  { [void]$sb.Append('\"') }
        '\'  { [void]$sb.Append('\\') }
        "`b" { [void]$sb.Append('\b') }
        "`f" { [void]$sb.Append('\f') }
        "`n" { [void]$sb.Append('\n') }
        "`r" { [void]$sb.Append('\r') }
        "`t" { [void]$sb.Append('\t') }
        default {
            if ($code -lt 0x20 -or $code -gt 0x7E) { [void]$sb.Append('\u' + $code.ToString('x4')) }
            else { [void]$sb.Append($ch) }
        }
    }
}
$escaped = $sb.ToString()

$json = '{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"' + $escaped + '"}}'
Write-Output $json
exit 0
