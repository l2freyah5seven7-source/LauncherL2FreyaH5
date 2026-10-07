$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$settingsPath = Join-Path $scriptDirectory 'launcher.settings.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$repository = "$($settings.GitHubOwner)/$($settings.GitHubRepository)"
$publisherScript = Join-Path $scriptDirectory 'Publish-ClientPatch.ps1'
$allowlistPath = Join-Path $scriptDirectory 'patch-files.txt'
$stateDirectory = Join-Path $env:LOCALAPPDATA 'AscensionLauncher\ClientPatchPublisher'
$statePath = Join-Path $stateDirectory 'state.json'
$script:CurrentSnapshot = $null
$script:Changes = @()
$script:Deletions = @()
$script:State = $null

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Ascension - Publicador de parches'
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size(900, 620)
$form.MinimumSize = New-Object System.Drawing.Size(720, 460)
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)

$layout = New-Object System.Windows.Forms.TableLayoutPanel
$layout.Dock = 'Fill'
$layout.Padding = New-Object System.Windows.Forms.Padding(12)
$layout.ColumnCount = 1
$layout.RowCount = 4
$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle('AutoSize'))) | Out-Null
$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle('Percent', 100))) | Out-Null
$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle('AutoSize'))) | Out-Null
$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle('AutoSize'))) | Out-Null
$form.Controls.Add($layout)

$top = New-Object System.Windows.Forms.TableLayoutPanel
$top.Dock = 'Top'
$top.AutoSize = $true
$top.ColumnCount = 3
$top.RowCount = 2
$top.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('Percent', 100))) | Out-Null
$top.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('AutoSize'))) | Out-Null
$top.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('AutoSize'))) | Out-Null
$label = New-Object System.Windows.Forms.Label
$label.Text = 'Carpeta raíz del cliente:'
$label.AutoSize = $true
$label.Margin = New-Object System.Windows.Forms.Padding(0, 4, 0, 4)
$top.Controls.Add($label, 0, 0)
$rootBox = New-Object System.Windows.Forms.TextBox
$rootBox.Dock = 'Fill'
$rootBox.Text = Split-Path -Parent $scriptDirectory
$top.Controls.Add($rootBox, 0, 1)
$browseButton = New-Object System.Windows.Forms.Button
$browseButton.Text = 'Examinar…'
$browseButton.AutoSize = $true
$top.Controls.Add($browseButton, 1, 1)
$scanButton = New-Object System.Windows.Forms.Button
$scanButton.Text = 'Analizar'
$scanButton.AutoSize = $true
$scanButton.Margin = New-Object System.Windows.Forms.Padding(8, 3, 0, 3)
$top.Controls.Add($scanButton, 2, 1)
$layout.Controls.Add($top, 0, 0)

$changesView = New-Object System.Windows.Forms.ListView
$changesView.Dock = 'Fill'
$changesView.View = [System.Windows.Forms.View]::Details
$changesView.FullRowSelect = $true
$changesView.GridLines = $true
$changesView.Columns.Add('Cambio', 110) | Out-Null
$changesView.Columns.Add('Ruta relativa al cliente', 690) | Out-Null
$layout.Controls.Add($changesView, 0, 1)

$status = New-Object System.Windows.Forms.Label
$status.Text = 'Analiza la carpeta para registrar la base o detectar cambios.'
$status.AutoSize = $true
$status.Dock = 'Fill'
$status.Padding = New-Object System.Windows.Forms.Padding(0, 8, 0, 8)
$layout.Controls.Add($status, 0, 2)

$bottom = New-Object System.Windows.Forms.FlowLayoutPanel
$bottom.FlowDirection = 'RightToLeft'
$bottom.Dock = 'Fill'
$bottom.AutoSize = $true
$publishButton = New-Object System.Windows.Forms.Button
$publishButton.Text = 'Publicar actualización'
$publishButton.AutoSize = $true
$publishButton.Enabled = $false
$publishButton.Padding = New-Object System.Windows.Forms.Padding(8, 3, 8, 3)
$bottom.Controls.Add($publishButton)
$layout.Controls.Add($bottom, 0, 3)

$browseButton.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Selecciona la carpeta raíz del cliente Lineage II.'
    $dialog.SelectedPath = $rootBox.Text
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $rootBox.Text = $dialog.SelectedPath
        $script:State = $null
        $script:CurrentSnapshot = $null
        $script:Changes = @()
        $script:Deletions = @()
        $changesView.Items.Clear()
        $publishButton.Enabled = $false
    }
})

function Test-ExcludedPath {
    param([string]$RelativePath)

    if ($RelativePath -match '^(?i:(?:\.git|Replay|Screenshot|LauncherSource|L2Launcher)/)' -or
        $RelativePath -match '^(?i:LineageII\.exe|LineageII\.cfg|system\.zip|\.l2launcher-version)$' -or
        $RelativePath -match '^(?i:system/[^/]+\.log)$') {
        return $true
    }
    return $false
}

function Get-ClientSnapshot {
    param([string]$Root)

    $snapshot = @{}
    $directories = New-Object 'System.Collections.Generic.Stack[string]'
    $directories.Push($Root)
    $processed = 0
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                continue
            }
            $relative = $item.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
            if ($item.PSIsContainer) {
                if (-not (Test-ExcludedPath -RelativePath ($relative + '/'))) {
                    $directories.Push($item.FullName)
                }
                continue
            }
            if (Test-ExcludedPath -RelativePath $relative) {
                continue
            }

            $snapshot[$relative] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $processed++
            if (($processed % 100) -eq 0) {
                $status.Text = "Analizando… $processed archivos verificados"
                [System.Windows.Forms.Application]::DoEvents()
            }
        }
    }
    return $snapshot
}

function Save-PublisherState {
    param([object]$State)

    if (-not (Test-Path -LiteralPath $stateDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    }
    $temporary = $statePath + '.tmp'
    $json = $State | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText(
        $temporary,
        $json,
        (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
}

function Get-StateSnapshot {
    param([object]$State)

    $snapshot = @{}
    foreach ($property in $State.snapshot.PSObject.Properties) {
        $snapshot[$property.Name] = [string]$property.Value
    }
    return $snapshot
}

function Read-ManagedFiles {
    param([hashtable]$Snapshot)

    $paths = @(Get-Content -LiteralPath $allowlistPath -Encoding UTF8 |
        ForEach-Object { $_.Trim().Replace('\', '/') } |
        Where-Object { $_ -and -not $_.StartsWith('#') } |
        Select-Object -Unique)
    foreach ($path in $paths) {
        if (-not $Snapshot.ContainsKey($path)) {
            throw "El archivo que ya se publica no existe en la carpeta elegida: $path"
        }
    }
    return $paths
}

function Add-ChangeRow {
    param([string]$Kind, [string]$Path)

    $item = New-Object System.Windows.Forms.ListViewItem($Kind)
    [void]$item.SubItems.Add($Path)
    [void]$changesView.Items.Add($item)
}

$scanButton.Add_Click({
    try {
        $root = [System.IO.Path]::GetFullPath($rootBox.Text)
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            throw "No existe la carpeta: $root"
        }
        $rootBox.Text = $root
        $publishButton.Enabled = $false
        $changesView.Items.Clear()
        $status.Text = 'Calculando huellas SHA-256…'
        [System.Windows.Forms.Application]::DoEvents()

        $snapshot = Get-ClientSnapshot -Root $root
        $existingState = Test-Path -LiteralPath $statePath -PathType Leaf
        if (-not $existingState) {
            $managed = @(Read-ManagedFiles -Snapshot $snapshot)
            $script:State = [PSCustomObject]@{
                schemaVersion = 1
                root = $root
                snapshot = $snapshot
                managedFiles = $managed
            }
            Save-PublisherState -State $script:State
            $script:CurrentSnapshot = $snapshot
            $script:Changes = @()
            $script:Deletions = @()
            $status.Text = "Base inicial guardada ($($snapshot.Count) archivos). No se subió ningún archivo. Los archivos del parche actual se conservaron."
            [System.Windows.Forms.MessageBox]::Show(
                $form,
                "Se guardó la base local con $($snapshot.Count) archivos. Esta acción no sube el cliente completo. Desde ahora, los archivos nuevos o modificados se detectarán para publicar. Los archivos eliminados requerirán confirmación.",
                'Base inicial registrada',
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
            return
        }

        $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([System.IO.Path]::GetFullPath([string]$state.root) -ne $root) {
            $reset = [System.Windows.Forms.MessageBox]::Show(
                $form,
                'La carpeta raíz cambió. ¿Quieres registrar el estado actual como una nueva base local? No se subirá ningún archivo.',
                'Cambiar base local',
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning)
            if ($reset -ne [System.Windows.Forms.DialogResult]::Yes) {
                return
            }
            $managed = @(Read-ManagedFiles -Snapshot $snapshot)
            $script:State = [PSCustomObject]@{
                schemaVersion = 1
                root = $root
                snapshot = $snapshot
                managedFiles = $managed
            }
            Save-PublisherState -State $script:State
            $script:CurrentSnapshot = $snapshot
            $script:Changes = @()
            $script:Deletions = @()
            $status.Text = "Nueva base local guardada ($($snapshot.Count) archivos). No se subió ningún archivo."
            return
        }
        $previous = Get-StateSnapshot -State $state
        $changes = @(
            foreach ($path in ($snapshot.Keys | Sort-Object)) {
                if (-not $previous.ContainsKey($path)) {
                    [PSCustomObject]@{ Kind = 'Nuevo'; Path = $path }
                }
                elseif ($previous[$path] -cne $snapshot[$path]) {
                    [PSCustomObject]@{ Kind = 'Modificado'; Path = $path }
                }
            }
        )
        $deletions = @(
            foreach ($path in ($previous.Keys | Sort-Object)) {
                if (-not $snapshot.ContainsKey($path)) {
                    [PSCustomObject]@{ Kind = 'Eliminado'; Path = $path }
                }
            }
        )
        foreach ($change in $changes) {
            Add-ChangeRow -Kind $change.Kind -Path $change.Path
        }
        foreach ($deletion in $deletions) {
            Add-ChangeRow -Kind $deletion.Kind -Path $deletion.Path
        }
        $script:CurrentSnapshot = $snapshot
        $script:State = $state
        $script:Changes = $changes
        $script:Deletions = $deletions
        $publishButton.Enabled = ($changes.Count + $deletions.Count) -gt 0
        if ($publishButton.Enabled) {
            $status.Text = "$($changes.Count) archivo(s) nuevo(s) o modificado(s); $($deletions.Count) posible(s) eliminación(es)."
        }
        else {
            $status.Text = 'El cliente coincide con la última base registrada; no hay cambios para publicar.'
        }
    }
    catch {
        $status.Text = 'No se pudo analizar la carpeta.'
        [System.Windows.Forms.MessageBox]::Show(
            $form,
            $_.Exception.Message,
            'Error al analizar el cliente',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
})

function Get-NextPatchVersion {
    $result = @(& gh api "repos/$repository/releases/latest" --jq .tag_name 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo consultar el último Release de GitHub: $($result -join [Environment]::NewLine)"
    }
    $tag = ([string]($result -join '')).Trim()
    if ($tag -notmatch '^v(\d+)\.(\d+)\.(\d+)\.(\d+)$') {
        throw "El tag del último parche no tiene el formato esperado: $tag"
    }
    $patchNumber = [long]$Matches[4] + 1
    return "$($Matches[1]).$($Matches[2]).$($Matches[3]).$patchNumber"
}

$publishButton.Add_Click({
    $selectionPath = $null
    try {
        if ($null -eq $script:CurrentSnapshot -or
            ($script:Changes.Count + $script:Deletions.Count) -eq 0) {
            return
        }
        $deletedPaths = @($script:Deletions | ForEach-Object { $_.Path })
        $message = "Se publicarán $($script:Changes.Count) archivo(s) nuevo(s)/modificado(s) en el Release de parches."
        if ($deletedPaths.Count -gt 0) {
            $shownPaths = ($deletedPaths | Select-Object -First 12) -join "`r`n"
            if ($deletedPaths.Count -gt 12) {
                $shownPaths += "`r`n… y $($deletedPaths.Count - 12) más"
            }
            $message += "`r`n`r`nTambién se ELIMINARÁN de las instalaciones de los jugadores $($deletedPaths.Count) archivo(s):`r`n$shownPaths`r`n`r`nConfirma que quieres publicar estas eliminaciones."
        }
        $confirmation = [System.Windows.Forms.MessageBox]::Show(
            $form,
            $message,
            'Confirmar publicación',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($confirmation -ne [System.Windows.Forms.DialogResult]::Yes) {
            return
        }

        if ($deletedPaths.Count -gt 0) {
            $launcherRelease = @(& gh api 'repos/l2freyah5seven7-source/LauncherL2FreyaH5/releases/tags/v1.0.4' --jq .tag_name 2>&1)
            if ($LASTEXITCODE -ne 0 -or ([string]($launcherRelease -join '')).Trim() -ne 'v1.0.4') {
                throw 'Las eliminaciones requieren que publiques e instales primero el launcher v1.0.4. Hasta entonces, publica solo cambios de archivos y vuelve a analizar antes de intentar eliminar.'
            }
        }

        $nextVersion = Get-NextPatchVersion
        $previous = Get-StateSnapshot -State $script:State
        $managed = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($path in $script:State.managedFiles) {
            [void]$managed.Add([string]$path)
        }
        foreach ($change in $script:Changes) {
            [void]$managed.Add($change.Path)
        }
        foreach ($path in $deletedPaths) {
            [void]$managed.Remove($path)
        }
        $activeFiles = @(
            foreach ($path in ($managed | Sort-Object)) {
                if ($script:CurrentSnapshot.ContainsKey($path)) {
                    $path
                }
            }
        )
        if ($activeFiles.Count -eq 0 -and $deletedPaths.Count -eq 0) {
            throw 'No quedaron archivos para publicar.'
        }

        $selectionPath = Join-Path $env:TEMP ("ascension-patch-selection-{0}.json" -f [Guid]::NewGuid().ToString('N'))
        $selection = [PSCustomObject]@{
            files = $activeFiles
            deletedFiles = $deletedPaths
        } | ConvertTo-Json -Depth 5
        [System.IO.File]::WriteAllText(
            $selectionPath,
            $selection,
            (New-Object System.Text.UTF8Encoding($false)))

        $status.Text = "Publicando parche v$nextVersion en GitHub…"
        $publishButton.Enabled = $false
        [System.Windows.Forms.Application]::DoEvents()
        $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publisherScript `
            -Repository $repository `
            -ClientRoot $rootBox.Text `
            -Version $nextVersion `
            -SelectionFile $selectionPath 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "No se pudo publicar el parche:`r`n$($output -join [Environment]::NewLine)"
        }

        $script:State = [PSCustomObject]@{
            schemaVersion = 1
            root = [System.IO.Path]::GetFullPath($rootBox.Text)
            snapshot = $script:CurrentSnapshot
            managedFiles = $activeFiles
        }
        Save-PublisherState -State $script:State
        $patchList = @(
            '# Archivos administrados por el publicador, relativos a la raíz del cliente.'
            $activeFiles | Sort-Object
        ) -join [Environment]::NewLine
        [System.IO.File]::WriteAllText(
            $allowlistPath,
            $patchList + [Environment]::NewLine,
            (New-Object System.Text.UTF8Encoding($false)))

        $changesView.Items.Clear()
        $script:Changes = @()
        $script:Deletions = @()
        $publishButton.Enabled = $false
        $status.Text = "Publicado v$nextVersion correctamente. Los clientes lo recibirán al pulsar JUGAR."
        [System.Windows.Forms.MessageBox]::Show(
            $form,
            "Parche v$nextVersion publicado correctamente en GitHub.`r`n`r`nLos jugadores con el launcher compatible recibirán estos archivos al pulsar JUGAR.",
            'Publicación completada',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    }
    catch {
        $publishButton.Enabled = $true
        $status.Text = 'No se pudo completar la publicación.'
        [System.Windows.Forms.MessageBox]::Show(
            $form,
            $_.Exception.Message,
            'Error al publicar el parche',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
    finally {
        if ($selectionPath -and (Test-Path -LiteralPath $selectionPath)) {
            Remove-Item -LiteralPath $selectionPath -Force
        }
    }
})

[System.Windows.Forms.Application]::Run($form)
