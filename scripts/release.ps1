param(
  [string]$Project = "MarkdownViewer/MarkdownViewer.csproj",
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [string]$Version = "",
  [string]$ArtifactsDir = "artifacts",
  [string]$WebView2Bootstrapper = "third_party/WebView2/MicrosoftEdgeWebView2Setup.exe",
  [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$Message) {
  Write-Host "[release] $Message" -ForegroundColor Cyan
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "dotnet コマンドが見つかりません。"
}

$makeNsisPath = ""
$makeNsisCommand = Get-Command makensis -ErrorAction SilentlyContinue
if (-not $makeNsisCommand) {
  $makeNsisCommand = Get-Command makensis.exe -ErrorAction SilentlyContinue
}
if ($makeNsisCommand) {
  $makeNsisPath = $makeNsisCommand.Source
} else {
  $makeNsisCandidates = @(
    "C:\Program Files (x86)\NSIS\makensis.exe",
    "C:\Program Files\NSIS\makensis.exe"
  )

  foreach ($candidate in $makeNsisCandidates) {
    if (Test-Path $candidate) {
      $makeNsisPath = $candidate
      break
    }
  }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

$projectPath = (Resolve-Path $Project).Path
$projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)

$publishDir = Join-Path $repoRoot "$ArtifactsDir/publish/$projectName-$Runtime"
$packageDir = Join-Path $repoRoot "$ArtifactsDir/package"
$installerScriptPath = Join-Path $PSScriptRoot "installer.nsi"
$webView2BootstrapperPath = Join-Path $repoRoot $WebView2Bootstrapper

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
$hashLines = @(
  "$zipHash  $([System.IO.Path]::GetFileName($zipPath))",
  "$exeHash  $([System.IO.Path]::GetFileName($packageExePath))"
)

if (-not $SkipInstaller) {
  if ([string]::IsNullOrWhiteSpace($makeNsisPath)) {
    throw "NSIS の makensis が見つかりません。インストーラ生成をスキップするには -SkipInstaller を指定してください。"
  }

  if (-not (Test-Path $installerScriptPath)) {
    throw "NSIS スクリプトが見つかりません: $installerScriptPath"
  }

  $installerName = if ($Version -ne "") {
    "$projectName-$Version-setup.exe"
  } else {
    "$projectName-setup.exe"
  }
  $installerPath = Join-Path $packageDir $installerName

  if (Test-Path $installerPath) {
    Remove-Item -Path $installerPath -Force
  }

  Write-Step "NSIS インストーラを作成します: $installerPath"
  $makeNsisArgs = @(
    "/DAPP_NAME=$projectName",
    "/DAPP_VERSION=$Version",
    "/DPUBLISH_DIR=$publishDir",
    "/DOUTPUT_DIR=$packageDir",
    "/DOUTPUT_NAME=$installerName",
    "/DAPP_EXE=$projectName.exe"
  )
  if (Test-Path $webView2BootstrapperPath) {
    Write-Step "WebView2 Bootstrapper を同梱します: $webView2BootstrapperPath"
    $makeNsisArgs += "/DWEBVIEW2_BOOTSTRAPPER=$webView2BootstrapperPath"
  } else {
    Write-Step "WebView2 Bootstrapper は見つからないため同梱しません: $webView2BootstrapperPath"
  }
  $makeNsisArgs += $installerScriptPath
  $makeNsisProcess = Start-Process -FilePath $makeNsisPath -ArgumentList $makeNsisArgs -Wait -PassThru -NoNewWindow
  if ($makeNsisProcess.ExitCode -ne 0) {
    throw "makensis が失敗しました。ExitCode=$($makeNsisProcess.ExitCode)"
  }

  if (-not (Test-Path $installerPath)) {
    throw "インストーラが見つかりません: $installerPath"
  }

  $installerHash = (Get-FileHash -Path $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
  $hashLines += "$installerHash  $([System.IO.Path]::GetFileName($installerPath))"
}

$hashLines | Set-Content -Path $hashFile -Encoding ASCII

Write-Step "完了"
Write-Host "Publish:  $publishDir"
Write-Host "Package:  $zipPath"
Write-Host "Hashes:   $hashFile"
