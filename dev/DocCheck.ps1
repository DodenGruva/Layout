<#
.SYNOPSIS
    Mechanical consistency checks for the Layout documentation set.

.DESCRIPTION
    Everything in the doc scheme is a convention, and conventions decay. This turns "keep the docs
    consistent" into a command that FAILS.

    MECHANICAL CHECKS ONLY. This script must never try to judge prose. Every check here compares a
    number, a path, or a byte against something authoritative -- usually the source.

    dev/archive/ is excluded from every check EXCEPT 6, 8 and 9, which exist because of it.

    Dev-only. Never shipped in the mod zip -- same as RenderIcon.ps1.

.EXAMPLE
    .\dev\DocCheck.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$problems = New-Object System.Collections.Generic.List[string]

function Fail([string]$check, [string]$msg) { $problems.Add(("[{0}] {1}" -f $check, $msg)) }

function Get-LivingDocs {
    Get-ChildItem $repo -Recurse -File -Include *.md |
        Where-Object { $_.FullName -notlike '*\dev\archive\*' -and
                       $_.FullName -notlike '*\bin\*' -and
                       $_.FullName -notlike '*\obj\*' }
}

# ------------------------------------------------------------------ read the authorities
$modinfoPath = Join-Path $repo 'modinfo.json'
$modinfoRaw  = [System.IO.File]::ReadAllText($modinfoPath)
$modVersion  = ([regex]::Match($modinfoRaw, '"version"\s*:\s*"([^"]+)"')).Groups[1].Value

$packetsPath = Join-Path $repo 'src\Network\PacketTypes.cs'
$protoInSrc  = ([regex]::Match([System.IO.File]::ReadAllText($packetsPath),
                 'ProtocolVersion\s*=\s*(\d+)')).Groups[1].Value

$guideDataPath = Join-Path $repo 'src\Guide\GuideData.cs'
$dvInSrc = ([regex]::Match([System.IO.File]::ReadAllText($guideDataPath),
             'CurrentDataVersion\s*=\s*(\d+)')).Groups[1].Value

$srcCount = (Get-ChildItem (Join-Path $repo 'src') -Recurse -File -Filter *.cs).Count

$statusPath = Join-Path $repo 'STATUS.md'
$status     = [System.IO.File]::ReadAllText($statusPath)

# ------------------------------------------------------------------ 1. modinfo version vs STATUS.md
if ($status -notmatch [regex]::Escape($modVersion)) {
    Fail 1 "modinfo.json is v$modVersion but STATUS.md never mentions it. Regenerate STATUS.md."
}

# ------------------------------------------------------------------ 2. source-file count vs STATUS.md
$declaredSrc = ([regex]::Match($status, '\*\*Source files\*\*\s*\|\s*\*\*(\d+)\*\*')).Groups[1].Value
if (-not $declaredSrc) {
    Fail 2 "STATUS.md does not declare a source-file count in the expected form."
} elseif ([int]$declaredSrc -ne $srcCount) {
    Fail 2 "STATUS.md says $declaredSrc source files; src/ actually holds $srcCount."
}

# ------------------------------------------------------------------ 3. protocol: source vs STATUS.md vs WIRE_HISTORY.md
$declaredProto = ([regex]::Match($status, '\*\*Wire protocol\*\*\s*\|\s*\*\*(\d+)\*\*')).Groups[1].Value
if ([string]$declaredProto -ne [string]$protoInSrc) {
    Fail 3 "STATUS.md says protocol $declaredProto; PacketTypes.cs says $protoInSrc."
}
$wirePath = Join-Path $repo 'dev\WIRE_HISTORY.md'
if (-not (Test-Path $wirePath)) {
    Fail 3 "dev/WIRE_HISTORY.md is missing."
} else {
    $wire = [System.IO.File]::ReadAllText($wirePath)
    $rows = [regex]::Matches($wire, '(?m)^\|\s*(\d+)\s*\|') | ForEach-Object { [int]$_.Groups[1].Value }
    if ($rows.Count -eq 0) {
        Fail 3 "dev/WIRE_HISTORY.md has no protocol rows."
    } elseif (($rows | Measure-Object -Maximum).Maximum -ne [int]$protoInSrc) {
        Fail 3 ("dev/WIRE_HISTORY.md's highest protocol row is {0}; source says {1}." -f `
                ($rows | Measure-Object -Maximum).Maximum, $protoInSrc)
    }
    if ($wire -notmatch "DataVersion $dvInSrc") {
        Fail 3 "dev/WIRE_HISTORY.md does not record the current DataVersion ($dvInSrc)."
    }
}
$declaredDv = ([regex]::Match($status, '\*\*DataVersion\*\*\s*\|\s*\*\*(\d+)\*\*')).Groups[1].Value
if ([string]$declaredDv -ne [string]$dvInSrc) {
    Fail 3 "STATUS.md says DataVersion $declaredDv; GuideData.cs says $dvInSrc."
}

# ------------------------------------------------------------------ 4. CHANGELOG has the current version
$changelog = [System.IO.File]::ReadAllText((Join-Path $repo 'CHANGELOG.md'))
if ($changelog -notmatch ('(?m)^##\s.*' + [regex]::Escape($modVersion))) {
    Fail 4 "CHANGELOG.md has no heading for the current version ($modVersion)."
}

# ------------------------------------------------------------------ 5. every session record has an INDEX row
$sessDir = Join-Path $repo 'dev\sessions'
$index   = [System.IO.File]::ReadAllText((Join-Path $sessDir 'INDEX.md'))
foreach ($f in Get-ChildItem $sessDir -Filter 'SESSION_*.md' -File) {
    if ($f.BaseName -match 'SESSION_(\d+)') {
        $n = $Matches[1]
        if ($index -notmatch "(?m)^\|\s*$n\s*\|") { Fail 5 "$($f.Name) has no row in dev/sessions/INDEX.md." }
    }
}

# ------------------------------------------------------------------ 6. publication safety -- ARCHIVE INCLUDED
# The repo is public. A personal path in a frozen copy ships exactly as readily as one in a living file,
# so this check gets no archive exemption. Highest-value check here.
#
# A real path has a segment AFTER the username (drive, Users, a name, then more). The docs quote the
# bare string `C:\Users` when stating this very rule, so requiring the trailing segment is what separates
# a leak from a specification. The username is read from the environment rather than hard-coded, because
# baking a real name into a public repo's checker would itself be the leak it is trying to prevent.
#
#
# --cached AND --others: tracked files PLUS untracked-but-not-ignored ones. Tracked alone is a trap --
# a brand-new file is invisible to `git ls-files`, and a newly written file is exactly where a stray
# absolute path is most likely to be. This check missed a planted home-directory path for that reason the
# first time it was tested. --exclude-standard keeps bin/ and obj/ out.
#
# Note for anyone editing this file: do not write a literal home-directory path in a comment here, even as
# an example. This script scans itself, and it will -- correctly -- fail on it.
#
$tracked  = & git -C $repo ls-files --cached --others --exclude-standard
$userName = $env:USERNAME
foreach ($rel in $tracked) {
    $full = Join-Path $repo $rel
    if (-not (Test-Path $full)) { continue }
    if ($rel -match '\.(png|zip|dll|ico)$') { continue }
    $txt = [System.IO.File]::ReadAllText($full)
    if ($txt -match '(?i)[A-Za-z]:\\Users\\[A-Za-z0-9._-]+\\') {
        Fail 6 "$rel contains an absolute personal filesystem path."
    }
    if ($userName -and $txt -match ('(?i)\b' + [regex]::Escape($userName) + '\b')) {
        Fail 6 "$rel contains the current user's account name."
    }
}

# ------------------------------------------------------------------ 7. modinfo.json is ASCII outside the BOM
# The description was double-encoded once and shipped mojibake to players.
$mb = [System.IO.File]::ReadAllBytes($modinfoPath)
$start = if ($mb.Length -ge 3 -and $mb[0] -eq 0xEF -and $mb[1] -eq 0xBB -and $mb[2] -eq 0xBF) { 3 } else { 0 }
for ($i = $start; $i -lt $mb.Length; $i++) {
    if ($mb[$i] -gt 0x7F) { Fail 7 ("modinfo.json has a non-ASCII byte (0x{0:X2}) at offset {1}." -f $mb[$i], $i); break }
}

# ------------------------------------------------------------------ 8. nothing outside the archive links into it
#
# The rule is that nothing SENDS A READER to the archive for current truth. The mechanical proxy for that
# is a markdown LINK -- "[text](dev/archive/...)" -- not a prose mention. Documents legitimately say "the
# frozen original is in dev/archive/", and the overhaul plan's entire subject is the archive; none of those
# are directing anyone to read it as current. A link is; prose is not. Keeping this crisp is what stops the
# check drifting into judging prose, which it must never do.
#
foreach ($f in Get-LivingDocs) {
    if ($f.FullName -eq (Join-Path $repo 'dev\archive\README.md')) { continue }
    $txt = [System.IO.File]::ReadAllText($f.FullName)
    foreach ($m in [regex]::Matches($txt, '\]\(\s*(?:\./)?((?:dev/)?archive/[^)\s]+)\s*\)')) {
        Fail 8 ("{0} LINKS into the archive as if it were current: {1}" -f `
                $f.FullName.Substring($repo.Length + 1), $m.Groups[1].Value)
    }
}

# ------------------------------------------------------------------ 9. every archived file carries the banner
$archRoot = Join-Path $repo 'dev\archive'
if (Test-Path $archRoot) {
    foreach ($f in Get-ChildItem $archRoot -Recurse -File) {
        if ($f.Name -eq 'README.md' -and $f.Directory.FullName -eq $archRoot) { continue }
        $head = (Get-Content $f.FullName -TotalCount 6) -join ' '
        if ($head -notmatch 'ARCHIVED \d{4}-\d{2}-\d{2}.*SUPERSEDED') {
            Fail 9 "$($f.Name) in dev/archive/ is missing the archived banner."
        }
    }
}

# ------------------------------------------------------------------ 10. every pointer resolves
# The highest-value check: after the overhaul CLAUDE.md is almost nothing but pointers, indexed by
# hand-copied trigger strings. A broken pointer is the single most expensive failure mode in the scheme.
$living = @('CLAUDE.md','STATUS.md','dev\GOTCHAS.md','dev\TODO.md','dev\WIRE_HISTORY.md',
            'dev\sessions\INDEX.md','dev\ARCHITECTURE.md') |
          Where-Object { Test-Path (Join-Path $repo $_) }

foreach ($rel in $living) {
    $txt = [System.IO.File]::ReadAllText((Join-Path $repo $rel))
    # .json is deliberately excluded: layout.json / layout-client.json are generated into the game's
    # ModConfig at runtime and are correctly named by documents that will never contain them.
    foreach ($m in [regex]::Matches($txt, '`([A-Za-z0-9_./-]+\.(?:md|ps1|cs))`')) {
        $target = $m.Groups[1].Value
        $candidates = @(
            (Join-Path $repo $target),
            (Join-Path $repo "dev\$target"),
            (Join-Path $repo "src\$target"),
            (Join-Path (Split-Path (Join-Path $repo $rel) -Parent) $target)
        )
        # Tier 3 files moved intact keep bare filenames; resolve those by convention.
        if ($target -match '^SESSION_\d+\.md$')       { $candidates += (Join-Path $repo "dev\sessions\$target") }
        if ($target -match '^(INDEX|TEMPLATE)\.md$')  { $candidates += (Join-Path $repo "dev\sessions\$target") }
        if ($target -match '^PLAN_[A-Z_]+\.md$')      { $candidates += (Join-Path $repo "dev\plans\$target") }
        if ($target -match '^(DONE|CHANGELOG_ARCHITECTURE)\.md$') { $candidates += (Join-Path $repo "dev\history\$target") }
        # A document may name a file that has been superseded -- STATUS.md says what it replaces, GOTCHAS
        # cites where a trap was found. Resolving into the archive is correct here: the file still exists,
        # frozen. Check 8 is what stops anyone LINKING to it as current.
        $candidates += (Get-ChildItem (Join-Path $repo 'dev\archive') -Recurse -File -Filter (Split-Path $target -Leaf) -ErrorAction SilentlyContinue |
                        ForEach-Object { $_.FullName })
        # bare source filenames resolve anywhere under src/
        if ($target -match '^[A-Za-z0-9_]+\.cs$') {
            $candidates += (Get-ChildItem (Join-Path $repo 'src') -Recurse -File -Filter $target -ErrorAction SilentlyContinue |
                            ForEach-Object { $_.FullName })
        }
        if (-not ($candidates | Where-Object { $_ -and (Test-Path $_) })) {
            Fail 10 "$rel points at '$target', which does not exist."
        }
    }
}

# every GOTCHAS reference in the living docs must name a real entry
$gotchasPath = Join-Path $repo 'dev\GOTCHAS.md'
if (Test-Path $gotchasPath) {
    $g = [System.IO.File]::ReadAllText($gotchasPath)
    $ids = @{}
    foreach ($m in [regex]::Matches($g, '(?m)^###\s+([GR]\d+)\s')) { $ids[$m.Groups[1].Value] = $true }
    foreach ($rel in $living) {
        if ($rel -eq 'dev\GOTCHAS.md') { continue }
        $txt = [System.IO.File]::ReadAllText((Join-Path $repo $rel))
        foreach ($m in [regex]::Matches($txt, '(?:GOTCHAS[^\r\n]{0,40}?|\*\*)\b([GR]\d+)\b')) {
            $id = $m.Groups[1].Value
            if (-not $ids.ContainsKey($id)) { Fail 10 "$rel cites GOTCHAS $id, which has no entry." }
        }
    }
}

# ------------------------------------------------------------------ 11. no present-tense wire claims in Tier 1
#
# EVERY defect found in the 2026-07-30 documentation audit was the same shape: a claim about the PRESENT
# left in a document whose job is durable fact. `ARCHITECTURE.md` said DataVersion was "currently 12" when
# the source said 13. Nothing caught it, because checks 1-3 only compare STATUS.md against source.
#
# This does NOT check whether the number is right. It checks that a Tier 1 document does not make the claim
# AT ALL -- which is the actual rule (see ARCHITECTURE.md's own header), and is stricter and cheaper than
# verifying a number that should not be there in the first place.
#
# HISTORICAL CITATIONS MUST SURVIVE. "v11 added IsWireframe (DataVersion/protocol 11)" records WHEN a
# decision was made -- permanent fact that cannot rot -- and the overhaul plan's acceptance criteria call
# out condemning those as a blunt-criterion mistake. So the test is not "mentions a version"; it is
# "mentions a version IN A SENTENCE THAT CLAIMS CURRENCY". Adjacency to a currency word is the proxy.
#
# WHAT MAKES A CLAIM "CURRENT" IS A NUMBER, NOT THE WORD DataVersion. The trigger is a currency word sitting
# next to a LITERAL NUMBER on a line about the wire -- "(currently 12)". A line that merely mentions
# DataVersion near the word "current" is usually the CORRECT pattern: "GuideData.CurrentDataVersion is the
# authority on which version is current" is a pointer, not a claim, and is exactly what a durable document
# should say instead of quoting a number. Requiring the digit is what separates the two.
#
# Fenced code blocks are skipped -- a struct listing legitimately names the field.
#
$tier1 = @('dev\ARCHITECTURE.md','dev\GOTCHAS.md') | Where-Object { Test-Path (Join-Path $repo $_) }
$currency = 'currently|current|as of|at present|right now|today'
foreach ($rel in $tier1) {
    $lines  = [System.IO.File]::ReadAllLines((Join-Path $repo $rel))
    $inFence = $false
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*```') { $inFence = -not $inFence; continue }
        if ($inFence) { continue }
        if ($line -notmatch '(?i)DataVersion|protocol') { continue }
        $m = [regex]::Match($line, "(?i)(($currency).{0,40}?\d)|(\d.{0,40}?($currency))")
        if ($m.Success) {
            Fail 11 ("{0}:{1} states a CURRENT wire number in a Tier 1 document ('{2}'). Cite WHEN it changed, or point at the source symbol -- never what it is now." -f `
                    $rel, ($i + 1), ($m.Value -replace '\s+', ' '))
        }
    }
}

# ------------------------------------------------------------------ 12. a plan's status must not contradict its body
#
# PLAN_RENDER_PERFORMANCE.md carried "Status: proposed, not started" for work that shipped in Session 28 --
# while its own body, thirteen lines further down, read "Stage 1 result - DELIVERED v0.3.57". That file is
# where CLAUDE.md sends anyone touching the renderer, so the first thing they read was that none of it had
# happened.
#
# Only the STATUS LINE is judged, not the prose. A delivered plan may still say a later stage is "not
# started" -- that is a real and useful statement -- so the contradiction is only reported when the
# document-level status itself claims the work is unstarted.
#
$planDir = Join-Path $repo 'dev\plans'
if (Test-Path $planDir) {
    foreach ($f in Get-ChildItem $planDir -Filter 'PLAN_*.md' -File) {
        $txt = [System.IO.File]::ReadAllText($f.FullName)
        $statusLine = ([regex]::Match($txt, '(?im)^[>\s*]*\*{0,2}Status\b[^\r\n]*')).Value
        if (-not $statusLine) { continue }
        $unstarted = $statusLine -match '(?i)not started|proposed|unstarted'
        $done      = $statusLine -match '(?i)delivered|implemented|complete|shipped'
        if ($unstarted -and -not $done -and $txt -match '(?i)\bDELIVERED\b|\bSHIPPED\b') {
            Fail 12 ("dev/plans/{0} says it is unstarted but its body reports delivered work. Status line: '{1}'" -f `
                    $f.Name, ($statusLine.Trim() -replace '\s+', ' '))
        }
    }
}

# ------------------------------------------------------------------ 13. no mojibake in any tracked text file
#
# THE RECURRING FAILURE. Every markdown file in this repo is UTF-8 with **no BOM** and every one of them
# contains non-ASCII -- em-dashes, the trap marker, arrows, multiplication signs. 61 of the .cs files do too.
# Windows PowerShell 5.1 decodes a BOM-less file as the system ANSI codepage, so a careless round-trip
# turns every em-dash into "a-euro-quote" and every trap marker into gibberish, in one silent pass over the
# whole file. That is not hypothetical here: it shipped to players once already in modinfo.json (GOTCHAS
# G25), and it happened again to ARCHITECTURE.md during the 2026-07-30 audit.
#
# Check 7 already guards modinfo.json, but it only asserts "ASCII only", which the rest of the repo cannot
# satisfy -- the docs are supposed to contain em-dashes. This check asks the other question: are the
# non-ASCII characters the RIGHT ones, or are they the wreckage of a bad decode?
#
# DETECTION. Mojibake from a UTF-8-read-as-ANSI round-trip always begins with A-circumflex, A-tilde or
# a-circumflex followed by another non-ASCII character. In genuine English-plus-code text that pairing
# essentially never occurs, which makes it a clean signature rather than a heuristic. Verified against all
# 148 tracked text files: exactly one hit, and it is a deliberate quotation (see below).
#
# THE PATTERN IS WRITTEN AS \u ESCAPES, AND THIS FILE MUST STAY PURE ASCII. Writing the three characters
# literally is what broke this check the first time it was added: DocCheck.ps1 had no non-ASCII bytes until
# then, PowerShell 5.1 read the new ones as ANSI, and the script died on its own regex. A checker must not
# contain the bytes it hunts -- the same reasoning as check 6's note about never writing a literal home
# path in a comment here.
#
# CODE SPANS ARE EXEMPT, IN MARKDOWN ONLY. SESSION_34.md quotes the mojibake it fixed, inside backticks --
# that is a record of the bug, not an instance of it. A quoted defect is data. Outside markdown there is no
# such convention, so any hit is a failure.
#
$mojibake = '[\u00C2\u00C3\u00E2](?=[^\x00-\x7F])'
foreach ($rel in $tracked) {
    $full = Join-Path $repo $rel
    if (-not (Test-Path $full)) { continue }
    if ($rel -notmatch '\.(md|ps1|json|txt|csproj|cs|vsh|fsh)$') { continue }
    $txt = [System.IO.File]::ReadAllText($full)
    if ($rel -match '\.md$') {
        $txt = [regex]::Replace($txt, '(?s)```.*?```', ' ')   # fenced blocks
        $txt = [regex]::Replace($txt, '`[^`\r\n]*`', ' ')     # inline code spans
    }
    $m = [regex]::Match($txt, $mojibake)
    if ($m.Success) {
        $lineNo = ($txt.Substring(0, $m.Index) -split "`n").Count
        Fail 13 ("{0}:{1} contains MOJIBAKE -- UTF-8 read as ANSI and written back. Restore the file; do not hand-repair it. To edit from PowerShell use [System.IO.File]::ReadAllText / WriteAllText, never Get-Content -Raw + Set-Content. See GOTCHAS G26." -f `
                $rel, $lineNo)
    }
}

# ------------------------------------------------------------------ report
Write-Host ''
if ($problems.Count -eq 0) {
    Write-Host 'DocCheck: PASS' -ForegroundColor Green
    Write-Host ("  v{0} - DataVersion {1}, protocol {2}, {3} source files" -f $modVersion, $dvInSrc, $protoInSrc, $srcCount)
    exit 0
}
Write-Host ("DocCheck: {0} problem(s)" -f $problems.Count) -ForegroundColor Red
$problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
exit 1
