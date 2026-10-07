param(
    [Parameter(Mandatory = $true)]
    [string]$Repository,

    [Parameter(Mandatory = $true)]
    [string]$ClientRoot,

    [string]$TargetBranch = 'main',

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$ReplaceExisting,

    [string]$SelectionFile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Repositorio no válido: $Repository"
}
if (-not (Test-Path -LiteralPath $ClientRoot -PathType Container)) {
    throw "No existe la carpeta del cliente: $ClientRoot"
}
$ClientRoot = [System.IO.Path]::GetFullPath($ClientRoot)
$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$allowlistPath = Join-Path $projectDirectory 'patch-files.txt'
$statePath = Join-Path $projectDirectory 'publisher-state.json'
if (-not (Test-Path -LiteralPath $allowlistPath -PathType Leaf)) {
    throw "No existe la lista permitida: $allowlistPath"
}

$deletedPaths = @()
if ($SelectionFile) {
    if (-not (Test-Path -LiteralPath $SelectionFile -PathType Leaf)) {
        throw "No existe la selección de archivos: $SelectionFile"
    }
    $selection = Get-Content -LiteralPath $SelectionFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $selection.files -or $null -eq $selection.deletedFiles) {
        throw 'La selección debe contener las listas files y deletedFiles.'
    }
    $allowlist = @($selection.files |
        ForEach-Object { ([string]$_).Trim().Replace('\', '/') } |
        Where-Object { $_ } |
        Select-Object -Unique)
    $deletedPaths = @($selection.deletedFiles |
        ForEach-Object { ([string]$_).Trim().Replace('\', '/') } |
        Where-Object { $_ } |
        Select-Object -Unique)
}
else {
    $allowlist = @(Get-Content -LiteralPath $allowlistPath -Encoding UTF8 |
        ForEach-Object { $_.Trim().Replace('\', '/') } |
        Where-Object { $_ -and -not $_.StartsWith('#') } |
        Select-Object -Unique)
}
if ($allowlist.Count -eq 0 -and $deletedPaths.Count -eq 0) {
    throw 'patch-files.txt está vacío; añade rutas exactas antes de publicar.'
}

$gh = Get-Command gh.exe -ErrorAction SilentlyContinue
if ($null -eq $gh) {
    throw 'No se encontró GitHub CLI (gh). Instálalo y vuelve a intentarlo.'
}
& $gh.Source auth status --hostname github.com
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub CLI no está autenticado. Ejecuta gh auth login y vuelve a intentarlo.'
}

$currentFiles = @{}
foreach ($relativePath in $allowlist) {
    if ([System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.Contains(':') -or $relativePath.Contains([char]0) -or
        $relativePath -match '(^|/)\.\.?(/|$)') {
        throw "La ruta debe ser relativa y segura: $relativePath"
    }

    $localPath = $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($ClientRoot, $localPath))
    $rootPrefix = $ClientRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "La ruta sale de la carpeta del cliente: $relativePath"
    }
    if ($relativePath -match '^(?i:(?:Replay|Screenshot|LauncherSource|L2Launcher)/)' -or
        $relativePath -match '^(?i:LineageII\.exe|LineageII\.cfg|system\.zip|\.l2launcher-version)$' -or
        $relativePath -match '^(?i:system/[^/]+\.log)$') {
        throw "La ruta está excluida de la publicación: $relativePath"
    }
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        $currentFiles[$relativePath] = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    elseif ($SelectionFile) {
        throw "El archivo seleccionado ya no existe: $relativePath"
    }
}
if ($currentFiles.Count -eq 0 -and $deletedPaths.Count -eq 0) {
    throw 'Ningún archivo permitido existe en la carpeta del cliente.'
}

$validatedDeletedPaths = New-Object 'System.Collections.Generic.List[string]'
$seenDeletedPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($relativePath in $deletedPaths) {
    if ([System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.Contains(':') -or $relativePath.Contains([char]0) -or
        $relativePath -match '(^|/)\.\.?(/|$)' -or
        $relativePath -match '^(?i:(?:Replay|Screenshot|LauncherSource|L2Launcher)/)' -or
        $relativePath -match '^(?i:LineageII\.exe|LineageII\.cfg|system\.zip|\.l2launcher-version)$' -or
        $relativePath -match '^(?i:system/[^/]+\.log)$') {
        throw "La ruta eliminada no es segura o está excluida: $relativePath"
    }
    if (-not $seenDeletedPaths.Add($relativePath)) {
        throw "La ruta eliminada está repetida: $relativePath"
    }
    if ($currentFiles.ContainsKey($relativePath)) {
        throw "La ruta no puede publicarse como archivo y como eliminada: $relativePath"
    }

    $fullPath = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine(
            $ClientRoot,
            $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
    if (-not $fullPath.StartsWith(
            $ClientRoot.TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar) +
                [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "La ruta eliminada sale de la carpeta del cliente: $relativePath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        throw "La ruta marcada para eliminar todavía existe: $relativePath"
    }
    $validatedDeletedPaths.Add($relativePath)
}
$deletedPaths = @($validatedDeletedPaths)

$previousFiles = @{}
if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -ne $state.files) {
        foreach ($property in $state.files.PSObject.Properties) {
            $previousFiles[$property.Name] = [string]$property.Value
        }
    }
}

$changed = @(
    foreach ($path in ($currentFiles.Keys | Sort-Object)) {
        if (-not $previousFiles.ContainsKey($path) -or
            $previousFiles[$path] -cne $currentFiles[$path]) {
            [PSCustomObject]@{ path = $path; sha256 = $currentFiles[$path] }
        }
    }
)
if ($changed.Count -eq 0 -and $deletedPaths.Count -eq 0) {
    Write-Host 'No hay cambios en los archivos enumerados en patch-files.txt.'
    exit 0
}

$tag = "v$Version"
$staging = Join-Path $projectDirectory ('staging\' + $tag)
$maxPartBytes = 700MB
$groups = New-Object 'System.Collections.Generic.List[object]'
$group = New-Object 'System.Collections.Generic.List[object]'
[long]$groupBytes = 0
foreach ($relativePath in ($currentFiles.Keys | Sort-Object)) {
    $sourcePath = [System.IO.Path]::Combine(
        $ClientRoot, $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    $length = (Get-Item -LiteralPath $sourcePath).Length
    if ($length -gt $maxPartBytes) {
        throw "El archivo '$relativePath' supera el límite de paquete de 700 MiB."
    }
    if ($group.Count -gt 0 -and ($groupBytes + $length) -gt $maxPartBytes) {
        $groups.Add(@($group.ToArray()))
        $group.Clear()
        $groupBytes = 0
    }
    $group.Add([PSCustomObject]@{
        path = $relativePath
        sha256 = $currentFiles[$relativePath]
    })
    $groupBytes += $length
}
if ($group.Count -gt 0) {
    $groups.Add(@($group.ToArray()))
}

New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    $assetNames = @()
    for ($index = 0; $index -lt $groups.Count; $index++) {
        $assetName = 'client-patch-{0:D3}.zip' -f ($index + 1)
        $assetNames += $assetName
        $archivePath = Join-Path $staging $assetName
        $archive = [System.IO.Compression.ZipFile]::Open(
            $archivePath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $groups[$index]) {
                $sourcePath = [System.IO.Path]::Combine(
                    $ClientRoot,
                    $file.path.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive,
                    $sourcePath,
                    $file.path,
                    [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    $manifestFiles = @()
    for ($index = 0; $index -lt $groups.Count; $index++) {
        foreach ($file in $groups[$index]) {
            $manifestFiles += [PSCustomObject]@{
                path = $file.path
                sha256 = $file.sha256
                assetIndex = $index
            }
        }
    }
    $manifest = [PSCustomObject]@{
        schemaVersion = if ($deletedPaths.Count -gt 0) { 2 } else { 1 }
        version = $Version
        assets = $assetNames
        files = $manifestFiles
        deletedFiles = $deletedPaths
    }
    $manifestPath = Join-Path $staging 'client-manifest.json'
    $manifestJson = $manifest | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        $manifestPath,
        $manifestJson,
        (New-Object System.Text.UTF8Encoding($false)))
    $signingScript = Join-Path $projectDirectory 'Sign-LauncherAssets.ps1'
    if (-not (Test-Path -LiteralPath $signingScript -PathType Leaf)) {
        throw "Falta el firmador digital: $signingScript"
    }
    & $signingScript -SignFile $manifestPath

    $uploadFiles = @(
        (Resolve-Path -LiteralPath $manifestPath).Path,
        (Resolve-Path -LiteralPath ($manifestPath + '.sig')).Path
    )
    foreach ($assetName in $assetNames) {
        $uploadFiles += (Resolve-Path -LiteralPath (Join-Path $staging $assetName)).Path
    }
    if ($ReplaceExisting) {
        & $gh.Source release view $tag --repo $Repository *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "No existe el Release $tag para reemplazar."
        }

        $arguments = @('release', 'upload', $tag) + $uploadFiles +
            @('--repo', $Repository, '--clobber')
    }
    else {
        $notes = "Parche $Version. Publicado mediante el administrador de parches firmado."
        $arguments = @('release', 'create', $tag) + $uploadFiles +
            @('--repo', $Repository, '--target', $TargetBranch, '--title', $tag, '--notes', $notes)
    }

    Write-Host "Se publicarán $($currentFiles.Count) archivo(s) autorizado(s) ($($changed.Count) con cambios) en $Repository."
    & $gh.Source @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub CLI no pudo crear el Release; el estado local no se avanzó.'
    }

    $stateJson = [PSCustomObject]@{
        version = $tag
        files = $currentFiles
        updatedAt = (Get-Date).ToString('o')
    } | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        $statePath,
        $stateJson,
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Publicado correctamente: https://github.com/$Repository/releases/tag/$tag"
}
finally {
    if (Test-Path -LiteralPath $staging -PathType Container) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
