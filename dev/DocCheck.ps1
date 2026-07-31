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
$statusSrc = ([regex]::Match($status, '\*\*(\d+)\*\*\s*\|?\s*\r?\n?|(\d+)\s*source files')).Value
if ($status -match '(\d+)\s*\|\s*\r?\n?\s*\|\s*\*\*Shape catalog') { }
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
