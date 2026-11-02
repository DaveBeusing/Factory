#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-Executable {
    param([Parameter(Mandatory = $true)][string]$Name)

    return $null -ne (Get-Command -Name $Name -CommandType Application -ErrorAction SilentlyContinue)
}

function Confirm-Installation {
    param([Parameter(Mandatory = $true)][string]$Question)

    while ($true) {
        $answer = (Read-Host "$Question [Y/N]").Trim()
        switch -Regex ($answer) {
            '^(y|yes)$' { return $true }
            '^(n|no)$' { return $false }
            default { Write-Host 'Please enter Y or N.' }
        }
    }
}

function Install-WingetPackage {
    param([Parameter(Mandatory = $true)][string]$PackageId)

    if (-not (Test-Executable -Name 'winget.exe')) {
        throw 'winget is unavailable. Install Microsoft App Installer or install the missing prerequisite manually.'
    }

    & winget.exe install --id $PackageId --exact --source winget --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "Installation of $PackageId failed with exit code $LASTEXITCODE."
    }
}

function Test-DotNet10 {
    if (-not (Test-Executable -Name 'dotnet.exe')) {
        return $false
    }

    $installedSdks = & dotnet.exe --list-sdks
    return ($LASTEXITCODE -eq 0) -and [bool]($installedSdks | Where-Object { $_ -match '^10\.' })
}

function Test-NativeAgentCli {
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

function Install-NativeAgentCli {
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

function Invoke-DependencySetup {
    if ($env:OS -ne 'Windows_NT') {
        throw 'Install-Dependencies.ps1 currently supports Windows only.'
    }

    Write-Host 'Factory - native dependency verification'

    if (Test-DotNet10) {
        Write-Host '[OK] .NET 10 SDK'
    }
    else {
        Write-Host '[MISSING] .NET 10 SDK'
        if (-not (Confirm-Installation -Question 'Install .NET 10 SDK?')) {
            return 2
        }
        Install-WingetPackage -PackageId 'Microsoft.DotNet.SDK.10'
    }

    if (Test-Executable -Name 'git.exe') {
        Write-Host '[OK] Git'
    }
    else {
        Write-Host '[MISSING] Git'
        if (-not (Confirm-Installation -Question 'Install Git?')) {
            return 2
        }
        Install-WingetPackage -PackageId 'Git.Git'
    }

    if (Test-NativeAgentCli) {
        Write-Host '[OK] Native agent CLI'
    }
    else {
        Write-Host '[MISSING] Native agent CLI (codex.exe)'
        if (-not (Confirm-Installation -Question 'Download and review the official native agent CLI installer?')) {
            return 2
        }
        if (-not (Install-NativeAgentCli)) {
            return 2
        }
    }

    if (Test-Executable -Name 'gh.exe') {
        Write-Host '[OK] GitHub CLI'
    }
    else {
        Write-Host '[OPTIONAL] GitHub CLI not found'
        if (Confirm-Installation -Question 'Install GitHub CLI?') {
            Install-WingetPackage -PackageId 'GitHub.cli'
        }
    }

    Write-Host ''
    Write-Host 'Checks complete. Restart PowerShell if PATH was updated.'
    Write-Host 'Sign in manually to the agent CLI and to GitHub CLI when needed.'
    return 0
}

try {
    $exitCode = Invoke-DependencySetup
    exit $exitCode
}
catch {
    [Console]::Error.WriteLine("ERROR: $($_.Exception.Message)")
    exit 3
}
