[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Repository,

    [Parameter(Mandatory = $true)]
    [string] $CurrentTag,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $ForgejoToken = $env:FORGEJO_TOKEN,

    [string] $ForgejoApiUrl = "https://forgejo.romaine.life/api/v1",

    [string] $WorkshopOutputPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($ForgejoToken)) {
    throw "A Forgejo token is required to resolve pull requests for release notes."
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [switch] $AllowFailure
    )

    $output = @(& git @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return $output
}

function Invoke-ForgejoGet {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $headers = @{
        Accept = "application/json"
        Authorization = "token $ForgejoToken"
        "User-Agent" = "SpireLens-release-notes"
    }

    $response = Invoke-RestMethod `
        -Method Get `
        -Uri "$($ForgejoApiUrl.TrimEnd('/'))/$($Path.TrimStart('/'))" `
        -Headers $headers
    # Invoke-RestMethod writes JSON arrays as one pipeline object. Enumerate
    # explicitly so paging and per-release/PR lookups see individual records.
    foreach ($item in $response) { $item }
}

function Get-WorkshopSection {
    param(
        [AllowNull()]
        [string] $Body
    )

    if ([string]::IsNullOrWhiteSpace($Body)) {
        return $null
    }

    $match = [regex]::Match(
        $Body,
        "(?ims)^\s*##\s+Workshop notes\s*\r?\n(?<content>.*?)(?=^\s*##\s+|\z)")
    if (-not $match.Success) {
        return $null
    }

    $content = [regex]::Replace(
        $match.Groups["content"].Value,
        "(?s)<!--.*?-->",
        "")
    return $content.Trim()
}

function Test-NoPlayerFacingChanges {
    param(
        [AllowEmptyString()]
        [string] $Content
    )

    $normalized = $Content.Trim()
    $normalized = [regex]::Replace($normalized, "^\s*[-*+]\s*", "")
    $normalized = $normalized.Trim().TrimEnd(".")
    return (
        $normalized -ieq "No player-facing changes" -or
        $normalized -ieq "None" -or
        $normalized -ieq "N/A")
}

function ConvertTo-ReleaseNoteLines {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Content
    )

    $notes = [System.Collections.Generic.List[string]]::new()
    foreach ($rawLine in ($Content -split "\r?\n")) {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $line = [regex]::Replace($line, "^[-*+]\s+", "")
        $line = [regex]::Replace($line, "^\d+[.)]\s+", "")
        $line = [regex]::Replace($line, "^\[[ xX]\]\s*", "")
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $notes.Add("- $line")
        }
    }

    return $notes.ToArray()
}

function Test-DocsOnlyCommit {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommitSha
    )

    # Only a known, single-parent commit with a nonempty Markdown-only diff
    # is safe to omit from Workshop. Merge/root/unknown commits stay included.
    try {
        $commit = @(Invoke-Git -Arguments @("rev-list", "--parents", "-n", "1", $CommitSha) -AllowFailure)
        if ($LASTEXITCODE -ne 0 -or $commit.Count -ne 1 -or
            @($commit[0] -split '\s+').Count -ne 2) {
            return $false
        }
        $paths = @(
            Invoke-Git -Arguments @("diff-tree", "--no-commit-id", "--name-only", "--no-renames", "-r", $CommitSha) -AllowFailure
        )
        if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) {
            return $false
        }
    }
    catch {
        return $false
    }
    return @($paths | Where-Object { $_ -notmatch '\.md$' }).Count -eq 0
}

# A failed publication may reserve a tag without releasing it. Such a tag
# must not hide changes from the next successful release's changelog.
$publishedTags = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
for ($page = 1; ; $page++) {
    $releases = @(Invoke-ForgejoGet -Path "repos/$Repository/releases?limit=50&page=$page")
    foreach ($release in $releases) {
        if (-not $release.draft) { [void]$publishedTags.Add([string]$release.tag_name) }
    }
    if ($releases.Count -lt 50) { break }
}
$previousTag = $null
foreach ($candidate in @(Invoke-Git -Arguments @("tag", "--merged", "$CurrentTag^", "--sort=-version:refname"))) {
    if ($candidate -ne $CurrentTag -and $publishedTags.Contains($candidate)) {
        $previousTag = $candidate
        break
    }
}

$revisionRange = if ([string]::IsNullOrWhiteSpace($previousTag)) {
    $CurrentTag
}
else {
    "$previousTag..$CurrentTag"
}

$repositoryDetails = Invoke-ForgejoGet -Path "repos/$Repository"
$defaultBranch = [string] $repositoryDetails.default_branch
if ([string]::IsNullOrWhiteSpace($defaultBranch)) {
    throw "Forgejo did not report a default branch for '$Repository'."
}

$commitShas = @(
    Invoke-Git -Arguments @("rev-list", "--reverse", $revisionRange)
)
if ($commitShas.Count -eq 0) {
    throw "No commits were found for release range '$revisionRange'."
}

$pullRequestsByNumber = @{}
$directCommitSubjects = [System.Collections.Generic.List[string]]::new()
$workshopDirectCommitSubjects = [System.Collections.Generic.List[string]]::new()

# Fetch merged PRs once rather than making a request for every historical commit.
$pullRequestsByCommit = @{}
for ($page = 1; ; $page++) {
    $batch = @(Invoke-ForgejoGet -Path "repos/$Repository/pulls?state=closed&limit=50&page=$page")
    foreach ($pullRequest in $batch) {
        if ($pullRequest.merged -and $pullRequest.merge_commit_sha) {
            $pullRequestsByCommit[[string]$pullRequest.merge_commit_sha] = $pullRequest
        }
    }
    if ($batch.Count -lt 50) { break }
}

foreach ($commitSha in $commitShas) {
    $associatedPullRequests = @($pullRequestsByCommit[[string]$commitSha] | Where-Object { $null -ne $_ })
    $mergedPullRequests = @(
        $associatedPullRequests |
            Where-Object {
                $null -ne $_.merged_at -and
                $_.base.ref -eq $defaultBranch
            }
    )

    if ($mergedPullRequests.Count -eq 0) {
        $subject = @(
            Invoke-Git -Arguments @("show", "-s", "--format=%s", $commitSha)
        )[0].Trim()
        if (-not [string]::IsNullOrWhiteSpace($subject)) {
            $directCommitSubjects.Add($subject)
            if (-not [string]::IsNullOrWhiteSpace($WorkshopOutputPath) -and
                -not (Test-DocsOnlyCommit -CommitSha $commitSha)) {
                $workshopDirectCommitSubjects.Add($subject)
            }
        }
        continue
    }

    foreach ($pullRequest in $mergedPullRequests) {
        $pullRequestsByNumber[[int] $pullRequest.number] = $pullRequest
    }
}

$releaseNoteLines = [System.Collections.Generic.List[string]]::new()
$seenReleaseNoteLines = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$workshopNoteLines = [System.Collections.Generic.List[string]]::new()
$seenWorkshopNoteLines = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

$orderedPullRequests = @(
    $pullRequestsByNumber.Values |
        Sort-Object `
            @{ Expression = { [DateTimeOffset] $_.merged_at } },
            @{ Expression = { [int] $_.number } }
)

foreach ($pullRequest in $orderedPullRequests) {
    $workshopSection = Get-WorkshopSection -Body $pullRequest.body
    if ($null -ne $workshopSection -and
        (Test-NoPlayerFacingChanges -Content $workshopSection)) {
        continue
    }

    $candidateLines = if ([string]::IsNullOrWhiteSpace($workshopSection)) {
        @("- $($pullRequest.title) (#$($pullRequest.number))")
    }
    else {
        @(ConvertTo-ReleaseNoteLines -Content $workshopSection)
    }

    $includeInWorkshop = -not [string]::IsNullOrWhiteSpace($WorkshopOutputPath) -and
        -not (Test-DocsOnlyCommit -CommitSha $pullRequest.merge_commit_sha)

    foreach ($candidateLine in $candidateLines) {
        if ($seenReleaseNoteLines.Add($candidateLine)) {
            $releaseNoteLines.Add($candidateLine)
        }
        if ($includeInWorkshop -and $seenWorkshopNoteLines.Add($candidateLine)) {
            $workshopNoteLines.Add($candidateLine)
        }
    }
}

foreach ($directCommitSubject in $directCommitSubjects) {
    $candidateLine = "- $directCommitSubject"
    if ($seenReleaseNoteLines.Add($candidateLine)) {
        $releaseNoteLines.Add($candidateLine)
    }
}

foreach ($directCommitSubject in $workshopDirectCommitSubjects) {
    $candidateLine = "- $directCommitSubject"
    if ($seenWorkshopNoteLines.Add($candidateLine)) {
        $workshopNoteLines.Add($candidateLine)
    }
}

if ($releaseNoteLines.Count -eq 0) {
    $releaseNoteLines.Add("- No player-facing changes.")
}

$resolvedOutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
    $OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$releaseNoteText = ($releaseNoteLines -join "`n") + "`n"
[IO.File]::WriteAllText(
    $resolvedOutputPath,
    $releaseNoteText,
    [Text.UTF8Encoding]::new($false))

Write-Host "Release notes written for '$revisionRange':"
Write-Host $releaseNoteText

if (-not [string]::IsNullOrWhiteSpace($WorkshopOutputPath)) {
    $resolvedWorkshopPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
        $WorkshopOutputPath)
    if ($resolvedWorkshopPath -eq $resolvedOutputPath) {
        throw "WorkshopOutputPath must differ from OutputPath to preserve the full release notes."
    }
    $workshopDirectory = Split-Path -Parent $resolvedWorkshopPath
    if (-not [string]::IsNullOrWhiteSpace($workshopDirectory)) {
        New-Item -ItemType Directory -Path $workshopDirectory -Force | Out-Null
    }
    if ($workshopNoteLines.Count -eq 0) {
        $workshopNoteLines.Add("- No player-facing changes.")
    }
    $workshopNoteText = ($workshopNoteLines -join "`n") + "`n"
    [IO.File]::WriteAllText(
        $resolvedWorkshopPath,
        $workshopNoteText,
        [Text.UTF8Encoding]::new($false))
    Write-Host "Workshop notes written for '$revisionRange' (Markdown-only maintenance omitted):"
    Write-Host $workshopNoteText
}
