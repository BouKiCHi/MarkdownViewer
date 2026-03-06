param(
  [string]$Project = "MarkdownViewer/MarkdownViewer.csproj",
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [string]$Version = "",
  [string]$ArtifactsDir = "artifacts"
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$Message) {
  Write-Host "[release] $Message" -ForegroundColor Cyan
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "dotnet コマンドが見つかりません。"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

$projectPath = (Resolve-Path $Project).Path
$projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)

$publishDir = Join-Path $repoRoot "$ArtifactsDir/publish/$projectName-$Runtime"
$packageDir = Join-Path $repoRoot "$ArtifactsDir/package"

if (Test-Path $publishDir) {
  Remove-Item -Path $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

$publishArgs = @(
  "publish",
  $projectPath,
  "-c", $Configuration,
  "-r", $Runtime,
  "-o", $publishDir,
  "/p:DebugSymbols=false",
  "/p:DebugType=none",
  "/p:CopyOutputSymbolsToPublishDirectory=false"
)

if ($Version -ne "") {
  $publishArgs += "/p:Version=$Version"
}

Write-Step "dotnet publish を実行します"
& dotnet @publishArgs

$exePath = Join-Path $publishDir "$projectName.exe"
if (-not (Test-Path $exePath)) {
  throw "実行ファイルが見つかりません: $exePath"
}

$packageExeName = if ($Version -ne "") {
  "$projectName-$Version-$Runtime.exe"
} else {
  "$projectName-$Runtime.exe"
}
$packageExePath = Join-Path $packageDir $packageExeName
Copy-Item -Path $exePath -Destination $packageExePath -Force

$zipName = if ($Version -ne "") {
  "$projectName-$Version-$Runtime.zip"
} else {
  "$projectName-$Runtime.zip"
}
$zipPath = Join-Path $packageDir $zipName

if (Test-Path $zipPath) {
  Remove-Item -Path $zipPath -Force
}

Write-Step "配布用 zip を作成します: $zipPath"
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$hashFile = Join-Path $packageDir "SHA256SUMS.txt"
$zipHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$exeHash = (Get-FileHash -Path $packageExePath -Algorithm SHA256).Hash.ToLowerInvariant()

@(
  "$zipHash  $([System.IO.Path]::GetFileName($zipPath))",
  "$exeHash  $([System.IO.Path]::GetFileName($packageExePath))"
) | Set-Content -Path $hashFile -Encoding ASCII

Write-Step "完了"
Write-Host "Publish:  $publishDir"
Write-Host "Package:  $zipPath"
Write-Host "Hashes:   $hashFile"
