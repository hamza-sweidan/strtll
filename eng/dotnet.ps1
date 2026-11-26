# Runs dotnet with telemetry off. If the .NET SDK was unpacked into the user profile
# instead of being installed system-wide, that copy is used.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $local 'dotnet.exe')) {
    $env:DOTNET_ROOT = $local
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    & (Join-Path $local 'dotnet.exe') @args
} else {
    & dotnet @args
}
exit $LASTEXITCODE
