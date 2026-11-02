#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet.exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Copy-FactoryAssets {
    param([Parameter(Mandatory = $true)][string]$DirectoryName)

    $source = Join-Path $PSScriptRoot $DirectoryName
    $destination = Join-Path (Join-Path $PSScriptRoot 'dist') $DirectoryName

    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "Missing asset directory: $source"
    }

    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
    }
}

try {
    if (-not (Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue)) {
        throw '.NET 10 SDK not found. Run Install-Dependencies.ps1.'
    }

    $installedSdks = & dotnet.exe --list-sdks
    if ($LASTEXITCODE -ne 0 -or -not ($installedSdks | Where-Object { $_ -match '^10\.' })) {
        throw '.NET 10 SDK not found. Run Install-Dependencies.ps1.'
    }

    Push-Location -LiteralPath $PSScriptRoot
    try {
        Invoke-DotNet -Arguments @('restore', 'Factory.slnx')
        Invoke-DotNet -Arguments @('build', 'Factory.slnx', '--configuration', 'Release', '--no-restore')
        Invoke-DotNet -Arguments @('run', '--project', 'tests/Factory.Tests/Factory.Tests.csproj', '--configuration', 'Release', '--no-restore')
        Invoke-DotNet -Arguments @('publish', 'src/Factory/Factory.csproj', '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'false', '--output', 'dist')

        Copy-FactoryAssets -DirectoryName 'skills'
        Copy-FactoryAssets -DirectoryName 'agents'
    }
    finally {
        Pop-Location
    }

    Write-Host ''
    Write-Host 'Build and tests completed. Start: dist\Factory.exe doctor'
    exit 0
}
catch {
    [Console]::Error.WriteLine("ERROR: $($_.Exception.Message)")
    exit 1
}
