# Read-only inventory for Kale Content Ops handover.
# Does not checkout, reset, clean, commit, push, pull, or change source code.

$Repo = 'C:\Work\KaleContentOps'
$BranchRef = 'origin/bugfix/fix-value-is-null-sparate'

if (-not (Test-Path -LiteralPath (Join-Path $Repo '.git'))) {
    throw "Repository tidak ditemukan di: $Repo"
}

Set-Location $Repo

Write-Output '=== REPOSITORY ROOT ==='
git rev-parse --show-toplevel

Write-Output '=== CURRENT BRANCH ==='
git branch --show-current

Write-Output '=== LOCAL HEAD ==='
git rev-parse HEAD
git log -1 --format='Commit: %H%nDate: %ad%nSubject: %s' --date=iso-strict

Write-Output '=== WORKING TREE STATUS ==='
git status --short --branch

Write-Output '=== CHANGED PATHS ONLY (NO FILE CONTENT) ==='
git status --porcelain=v1

Write-Output '=== LOCAL BRANCH TRACKING ==='
git branch -vv

Write-Output '=== REMOTE NAMES (NO URL / TOKEN) ==='
git remote

Write-Output '=== LOCAL VS REMOTE TRACKING REF ==='
if (git show-ref --verify --quiet "refs/remotes/$BranchRef") {
    git rev-list --left-right --count "$BranchRef...HEAD"
    Write-Output 'Count order: remote-only commits, then local-only commits.'
} else {
    Write-Output "Tracking ref belum ada: $BranchRef"
    Write-Output 'Jangan fetch/pull sebelum status working tree ditinjau.'
}

Write-Output '=== SOLUTION FILES ==='
Get-ChildItem -Path .\* -File -Include *.sln,*.slnx |
    Select-Object -ExpandProperty FullName

Write-Output '=== PROJECT FILES ==='
Get-ChildItem -Path . -Recurse -File -Include *.csproj,*.fsproj |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git)\\' } |
    Select-Object -ExpandProperty FullName

Write-Output '=== MIGRATION COUNT ==='
$MigrationPath = Join-Path $Repo 'KaleContentOps\Migrations'
if (Test-Path $MigrationPath) {
    $Migrations = Get-ChildItem -Path $MigrationPath -File -Filter '*.cs' |
        Where-Object { $_.Name -notlike '*.Designer.cs' }
    $Migrations.Count
    $Migrations | Select-Object -ExpandProperty Name
}
