[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'restore-adapter-sdk.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Pinned SDK verification failed.' }
$projects = @("src/MirrorPulse.Adapter.Local.Worker/MirrorPulse.Adapter.Local.Worker.csproj", "tests/MirrorPulse.Adapter.Local.Worker.Tests/MirrorPulse.Adapter.Local.Worker.Tests.csproj")
foreach ($project in $projects) {
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Restore failed for $project." }
    & dotnet build $project --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $project." }
    & dotnet format $project --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Formatting failed for $project." }
}
& dotnet test $projects[1] --configuration Release --no-build --no-restore --logger trx --results-directory artifacts/test-results
if ($LASTEXITCODE -ne 0) { throw 'Local process conformance failed.' }
