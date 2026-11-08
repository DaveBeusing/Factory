#Requires -Version 5.1
[CmdletBinding()]
param([ValidateSet("Codex", "ClaudeCode", "None")][string]$Provider = "Codex")

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

    if ($Provider -eq 'Codex') {
        . (Join-Path $PSScriptRoot 'src/Factory/Providers/Codex/Setup.ps1')
        if (Test-CodexCli) {
            Write-Host '[OK] Codex CLI'
        }
        else {
            Write-Host '[MISSING] Codex CLI (codex.exe)'
            if (-not (Confirm-Installation -Question 'Download and review the official Codex CLI installer?')) {
                return 2
            }
            if (-not (Install-CodexCli)) {
                return 2
            }
        }
    }
    elseif ($Provider -eq 'ClaudeCode') {
        . (Join-Path $PSScriptRoot 'src/Factory/Providers/ClaudeCode/Setup.ps1')
        if (-not (Test-ClaudeCodeCli)) {
            Write-Host '[MISSING] Claude Code CLI. Install the native CLI or set FACTORY_CLAUDE_EXECUTABLE.'
            return 2
        }
        Write-Host '[OK] Claude Code CLI'
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
