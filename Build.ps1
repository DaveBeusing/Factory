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

function Remove-LegacyCodexAssets {
    param([Parameter(Mandatory = $true)][string]$DistributionDirectory)

    $output = Get-Item -LiteralPath $DistributionDirectory -Force
    if (-not $output.PSIsContainer -or ($output.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Expected a local distribution directory: $DistributionDirectory"
    }
    $prefix = $output.FullName.TrimEnd('\') + '\'
    foreach ($name in @('agents', 'skills')) {
        $path = Join-Path $output.FullName $name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $legacy = Get-Item -LiteralPath $path -Force
        if (-not $legacy.PSIsContainer -or
            -not $legacy.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            ($legacy.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Expected a generated asset directory under dist: $path"
        }
        $links = Get-ChildItem -LiteralPath $legacy.FullName -Recurse -Force |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
            Select-Object -First 1
        if ($links) { throw "Legacy asset directory contains a link: $($links.FullName)" }
        Remove-Item -LiteralPath $legacy.FullName -Recurse -Force
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
        Invoke-DotNet -Arguments @('build', 'tools/Factory.Versioning/Factory.Versioning.csproj', '--configuration', 'Release', '--no-restore')
        $versionTool = Join-Path $PSScriptRoot 'tools/Factory.Versioning/bin/Release/net10.0/Factory.Versioning.dll'
        Invoke-DotNet -Arguments @($versionTool, 'self-test')
        $versionOutput = & dotnet.exe $versionTool build-version --repo $PSScriptRoot
        if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch '^\d+\.\d+\.\d+(?:-(?:preview|rc)\.\d+)?$') {
            throw "Cannot calculate a valid Factory version: $versionOutput"
        }
        $buildVersion = [string]$versionOutput

        $sourceSha = $null
        if (Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue) {
            $gitSha = & git.exe -C $PSScriptRoot rev-parse --verify HEAD 2>$null
            if ($LASTEXITCODE -eq 0 -and $gitSha -match '^[0-9a-fA-F]{40,64}$') {
                $sourceSha = [string]$gitSha
            }
        }
        $informationalVersion = $buildVersion
        if ($sourceSha) {
            $informationalVersion += '+g' + $sourceSha.Substring(0, 12)
        }

        $versionProperties = @(
            "-p:Version=$buildVersion",
            "-p:InformationalVersion=$informationalVersion",
            '-p:IncludeSourceRevisionInInformationalVersion=false'
        )
        Invoke-DotNet -Arguments (@('build', 'Factory.slnx', '--configuration', 'Release', '--no-restore') + $versionProperties)
        Invoke-DotNet -Arguments (@('run', '--project', 'tests/Factory.Tests/Factory.Tests.csproj', '--configuration', 'Release', '--no-restore', '--no-build') + @('--'))
        $distribution = Join-Path $PSScriptRoot 'dist'
        if (Test-Path -LiteralPath $distribution) {
            $output = Get-Item -LiteralPath $distribution -Force
            if (-not $output.PSIsContainer -or ($output.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Expected a local distribution directory: $distribution"
            }
        }
        Invoke-DotNet -Arguments (@('publish', 'src/Factory/Factory.csproj', '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'false', '--output', $distribution) + $versionProperties)
        Remove-LegacyCodexAssets -DistributionDirectory $distribution

        $exe = Join-Path $PSScriptRoot 'dist/Factory.exe'
        $manifest = [ordered]@{
            version = $buildVersion
            informationalVersion = $informationalVersion
            sourceSha = $sourceSha
            executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
            createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'dist/build-manifest.json') -Encoding UTF8
    }
    finally {
        Pop-Location
    }

    Write-Host ''
    Write-Host "Build and tests completed. Version: $informationalVersion"
    Write-Host 'Start: dist\Factory.exe doctor'
    exit 0
}
catch {
    [Console]::Error.WriteLine("ERROR: $($_.Exception.Message)")
    exit 1
}
