param(
    [switch]$InitializeKey,
    [switch]$SignSettings,
    [string]$SignFile,
    [string]$ExportPublicKey
)

$ErrorActionPreference = 'Stop'
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$bundledTool = Join-Path $scriptDirectory 'tools\LauncherSigningTool.dll'
$sourceTool = Join-Path $scriptDirectory 'SigningTool\bin\Release\net8.0-windows\LauncherSigningTool.dll'

if (Test-Path -LiteralPath $bundledTool -PathType Leaf) {
    $tool = $bundledTool
}
elseif (Test-Path -LiteralPath $sourceTool -PathType Leaf) {
    $tool = $sourceTool
}
else {
    $project = Join-Path $scriptDirectory 'SigningTool\LauncherSigningTool.csproj'
    $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    $dotnet = if ($null -ne $dotnetCommand) {
        $dotnetCommand.Source
    }
    else {
        Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    }
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw 'No se encontró .NET 8 SDK. Se requiere para compilar la herramienta de firma.'
    }

    & $dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo compilar la herramienta segura de firma.'
    }

    $tool = $sourceTool
}

$operations = @()
if ($InitializeKey) {
    $operations += 'init'
}
if ($SignSettings) {
    $operations += @('sign', (Join-Path $scriptDirectory 'launcher.settings.json'))
}
if ($SignFile) {
    $operations += @('sign', $SignFile)
}
if ($ExportPublicKey) {
    $operations += @('export-public', $ExportPublicKey)
}
if ($operations.Count -eq 0) {
    throw 'Indica -InitializeKey, -SignSettings, -SignFile o -ExportPublicKey.'
}

$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
$dotnet = if ($null -ne $dotnetCommand) {
    $dotnetCommand.Source
}
else {
    Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'No se encontró .NET. Instala .NET 8 para ejecutar la herramienta de firma.'
}

& $dotnet $tool @operations
if ($LASTEXITCODE -ne 0) {
    throw "La herramienta de firma terminó con el código $LASTEXITCODE."
}
