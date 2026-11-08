function Test-CodexCli {
    if (Test-Executable -Name 'codex.exe') {
        return $true
    }

    if ($env:FACTORY_AGENT_EXECUTABLE -and
        (Test-Path -LiteralPath $env:FACTORY_AGENT_EXECUTABLE -PathType Leaf)) {
        return $true
    }

    $candidates = @()
    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\OpenAI\Codex\bin\codex.exe')
    }
    if ($env:USERPROFILE) {
        $candidates += (Join-Path $env:USERPROFILE '.codex\packages\standalone\current\bin\codex.exe')
        $candidates += (Join-Path $env:USERPROFILE '.codex\packages\standalone\current\codex.exe')
    }
    if ($env:CODEX_INSTALL_DIR) {
        $candidates += (Join-Path $env:CODEX_INSTALL_DIR 'codex.exe')
        $candidates += (Join-Path $env:CODEX_INSTALL_DIR 'bin\codex.exe')
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $true
        }
    }
    return $false
}

function Install-CodexCli {
    $installerPath = Join-Path $env:TEMP ("factory-agent-installer-{0}.ps1" -f [guid]::NewGuid().ToString('N'))
    try {
        Invoke-WebRequest -UseBasicParsing -Uri 'https://chatgpt.com/codex/install.ps1' -OutFile $installerPath
        Write-Host 'Review the downloaded official installer before executing it.'
        Write-Host "Downloaded installer: $installerPath"
        Start-Process -FilePath 'notepad.exe' -ArgumentList ('"{0}"' -f $installerPath) -Wait

        $approval = Read-Host 'After review, type RUN to execute the installer (anything else cancels)'
        if ($approval -cne 'RUN') {
            return $false
        }

        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPath
        if ($LASTEXITCODE -ne 0) {
            throw "Native agent CLI installer failed with exit code $LASTEXITCODE."
        }
        return $true
    }
    finally {
        if (Test-Path -LiteralPath $installerPath -PathType Leaf) {
            Remove-Item -LiteralPath $installerPath -Force
        }
    }
}
