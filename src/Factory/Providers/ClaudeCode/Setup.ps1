function Test-ClaudeCodeCli {
    if ($env:FACTORY_CLAUDE_EXECUTABLE) {
        $path = [Environment]::ExpandEnvironmentVariables($env:FACTORY_CLAUDE_EXECUTABLE.Trim().Trim('"'))
        return $path -match '^(?:[a-z]:[\\/]|\\\\)' -and
            $path.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $path -PathType Leaf)
    }

    return (Test-Executable -Name 'claude.exe') -or
        ($env:USERPROFILE -and (Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.local\bin\claude.exe') -PathType Leaf))
}
