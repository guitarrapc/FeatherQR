# Fetches the GS1 data oracles used by `regenerate-gs1`, each pinned so a re-tagged release
# cannot silently change the oracle:
#
#  - The GS1 Barcode Syntax Engine console app (prebuilt Windows release asset), pinned by
#    version and SHA-256. It is the reference implementation of the Syntax Dictionary:
#    https://github.com/gs1/gs1-syntax-engine (Apache-2.0)
#  - The Syntax Engine source at the same release tag, pinned by commit, for the unit-test
#    vectors that `regenerate-gs1` imports (src/c-lib/ai.c, dl.c, scandata.c).
#  - The GS1 Barcode Syntax Dictionary at the release this library pins, pinned by commit:
#    https://github.com/gs1/gs1-syntax-dictionary (Apache-2.0). Its dictionary file and the
#    linters whose lists are not in the dictionary (ISO 3166, ISO 4217, media and package
#    types) are copied into the test fixtures by `import-gs1-syntax-dictionary`, and the
#    linter unit tests are imported as fixtures by `regenerate-gs1`.
#
# Usage: pwsh tools/QRInteropFixtures/get-gs1-syntax-engine.ps1

$ErrorActionPreference = 'Stop'

$engineVersion = '1.4.1'
$engineCommit = 'ec595ff68364dd0056abd3b89d087ff5fbc9bf8e'
$asset = 'gs1encoders-windows-console-app.zip'
$sha256 = '2c0383bd2ca82825c8c40c9c6cce2efe4583e3d31d7c51e08a7f88ed6aade68e'
$url = "https://github.com/gs1/gs1-syntax-engine/releases/download/$engineVersion/$asset"

$dictionaryRelease = '2026-01-27'
$dictionaryCommit = '8ad003f83a4ba6c2179ce15d578fae27d6580909'

$externalDir = Join-Path $PSScriptRoot 'external'
$engineDir = Join-Path $externalDir 'gs1-syntax-engine'
New-Item -ItemType Directory -Force $engineDir | Out-Null
$zipPath = Join-Path $engineDir $asset

if (-not (Test-Path $zipPath)) {
    Write-Host "downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zipPath
}

$actual = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
if ($actual -ne $sha256) {
    Remove-Item $zipPath
    throw "SHA-256 mismatch for ${asset}: expected $sha256, got $actual. Download removed."
}

Expand-Archive -Path $zipPath -DestinationPath $engineDir -Force
$exe = Join-Path $engineDir 'gs1encoders-cpp-app.exe'
& $exe --version

function Get-PinnedClone($name, $repo, $tag, $commit) {
    $dir = Join-Path $externalDir $name
    if (-not (Test-Path (Join-Path $dir '.git'))) {
        Write-Host "cloning $repo at $tag"
        git -c core.autocrlf=false clone --quiet --depth 1 --branch $tag $repo $dir
    }
    $head = (git -C $dir rev-parse HEAD).Trim()
    if ($head -ne $commit) {
        throw "$name at tag $tag is commit $head, expected $commit. Delete $dir and re-run, or re-pin."
    }
    Write-Host "$name $tag ($head) ready at $dir"
}

Get-PinnedClone 'gs1-syntax-engine-src' 'https://github.com/gs1/gs1-syntax-engine.git' $engineVersion $engineCommit
Get-PinnedClone 'gs1-syntax-dictionary' 'https://github.com/gs1/gs1-syntax-dictionary.git' $dictionaryRelease $dictionaryCommit

Write-Host "GS1 Syntax Engine $engineVersion ready at $exe"
