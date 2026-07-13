# Builds FuryPlusPlus-<version>.unitypackage from com.furyplusplus.addon.
# 1. Writes stable .meta files beside the sources (GUID = MD5 of the relative path, so
#    re-exports and manual installs share GUIDs).
# 2. Stages <guid>/{asset,asset.meta,pathname} entries and gzip-tars them.

$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
$src = Join-Path $repo 'com.furyplusplus.addon'
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$staging = [System.IO.Path]::GetFullPath(
    (Join-Path $tempRoot 'furyplusplus-unitypackage-staging')
)
if (-not $staging.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Staging path escaped the temporary directory: $staging"
}
$pkgRoot = 'Packages/com.furyplusplus.addon'

$version = (Get-Content (Join-Path $src 'package.json') -Raw | ConvertFrom-Json).version
$out = Join-Path $repo "FuryPlusPlus-$version.unitypackage"

function Get-Guid([string]$relPath) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("com.furyplusplus.addon:" + $relPath))
    ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

function Get-MetaBody([string]$relPath, [bool]$isFolder) {
    $guid = Get-Guid $relPath
    # Unity's canonical meta form: trailing space after empty-valued keys, trailing newline.
    # Unity's YAML parser rejects the file otherwise ("Expect ':' between key and value").
    $tail = "  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
    if ($isFolder) {
        return "fileFormatVersion: 2`nguid: $guid`nfolderAsset: yes`nDefaultImporter:`n$tail"
    }
    switch -Regex ($relPath) {
        '\.cs$' {
            return "fileFormatVersion: 2`nguid: $guid`nMonoImporter:`n  externalObjects: {}`n  serializedVersion: 2`n  defaultReferences: []`n  executionOrder: 0`n  icon: {instanceID: 0}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
        }
        '\.asmdef$' {
            return "fileFormatVersion: 2`nguid: $guid`nAssemblyDefinitionImporter:`n$tail"
        }
        'package\.json$' {
            return "fileFormatVersion: 2`nguid: $guid`nPackageManifestImporter:`n$tail"
        }
        default {
            return "fileFormatVersion: 2`nguid: $guid`nTextScriptImporter:`n$tail"
        }
    }
}

# Relative paths (forward slashes) of everything in the package. Folders need entries too
# (except the package root itself, which Unity mounts via package.json and has no meta).
$folders = Get-ChildItem $src -Recurse -Directory | ForEach-Object {
    $_.FullName.Substring($src.Length + 1).Replace('\', '/')
} | Sort-Object
$files = Get-ChildItem $src -Recurse -File | Where-Object { $_.Name -notlike '*.meta' } | ForEach-Object {
    $_.FullName.Substring($src.Length + 1).Replace('\', '/')
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

function Add-Entry([string]$relPath, [bool]$isFolder) {
    $guid = Get-Guid $relPath
    $dir = Join-Path $staging $guid
    New-Item -ItemType Directory -Force $dir | Out-Null

    $meta = Get-MetaBody $relPath $isFolder
    # Write .meta beside the source too, so manual Packages/ copies share the same GUIDs.
    $metaSrcPath = Join-Path $src ($relPath.Replace('/', '\') + '.meta')
    [System.IO.File]::WriteAllText($metaSrcPath, $meta)

    [System.IO.File]::WriteAllText((Join-Path $dir 'pathname'), "$pkgRoot/$relPath")
    [System.IO.File]::WriteAllText((Join-Path $dir 'asset.meta'), $meta)
    if (-not $isFolder) {
        Copy-Item (Join-Path $src ($relPath.Replace('/', '\'))) (Join-Path $dir 'asset')
    }
}

foreach ($f in $folders) { Add-Entry $f $true }
foreach ($f in $files) { Add-Entry $f $false }

if (Test-Path $out) { Remove-Item $out -Force }
tar -czf $out -C $staging .
if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }

Write-Host "Built: $out"
Write-Host ("Entries: {0} folders, {1} files" -f $folders.Count, $files.Count)
tar -tzf $out | Sort-Object | ForEach-Object { Write-Host "  $_" }
