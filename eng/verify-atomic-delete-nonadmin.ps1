[CmdletBinding(DefaultParameterSetName = 'Controller')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Child')][string]$Payload,
    [Parameter(Mandatory, ParameterSetName = 'Child')][string]$ExpectedSid,
    [Parameter(Mandatory, ParameterSetName = 'Child')][string]$Dotnet
)

$ErrorActionPreference = 'Stop'
if ($PSCmdlet.ParameterSetName -eq 'Child') {
    $caller = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($caller)
        if ($caller.User.Value -cne $ExpectedSid -or $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Require the designated ordinary test account.'
        }
    } finally { $caller.Dispose() }
    $env:MP_LOCAL_TEST_WORKER_EXE = Join-Path $Payload 'worker/MirrorPulse.Adapter.Worker.exe'
    $assembly = Join-Path $Payload 'tests/MirrorPulse.Adapter.Local.Worker.Tests.dll'
    $testArgs = @('test', $assembly, '--filter', 'FullyQualifiedName~AtomicWorkerCommitsExactAdmittedOriginalsAndReplaysAfterProcessRestart',
        '--results-directory', $env:TEMP, '--logger', 'trx;LogFileName=ordinary-user.trx', '--logger', 'console;verbosity=minimal')
    & $Dotnet @testArgs
    exit $LASTEXITCODE
}

if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') {
    throw 'The fresh user fixture is restricted to a disposable GitHub-hosted runner.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$name = 'mplocal-' + [guid]::NewGuid().ToString('N').Substring(0, 10)
$base = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$scratch = Join-Path $base $name
$work = Join-Path $scratch 'work'
$output = Join-Path $repo 'artifacts/test-results/atomic-delete-nonadmin'
$dotnetPath = (Get-Command dotnet -CommandType Application).Source
$pwshPath = [Environment]::ProcessPath
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
$runtime = switch ($architecture) { 'X64' { 'win-x64' } 'Arm64' { 'win-arm64' } default { throw 'Unexpected test architecture.' } }
$release = Join-Path $repo 'artifacts/release'
$tests = Join-Path $repo 'tests/MirrorPulse.Adapter.Local.Worker.Tests/bin/Release/net10.0-windows'
$user = $null
$process = $null
$ownedWorkspace = $false
if ($base.Length -eq 0 -or (Get-Item -LiteralPath $base -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'Require an ordinary disposable workspace parent.'
}
try {
    if (Test-Path -LiteralPath $scratch) { throw 'The disposable workspace already exists.' }
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $ownedWorkspace = $true
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $volume = [IO.Path]::GetPathRoot($work).TrimEnd('\')
    if ([IO.DriveInfo]::new($volume).DriveFormat -ne 'NTFS') { throw 'Atomic Delete requires a disposable NTFS volume.' }
    & fsutil usn queryjournal $volume | Out-Null
    if ($LASTEXITCODE -ne 0) {
        & fsutil usn createjournal m=33554432 a=4194304 $volume
        if ($LASTEXITCODE -ne 0) { throw 'The fixture could not enable the disposable volume journal.' }
    }
    $packages = @(Get-ChildItem -LiteralPath $release -Filter native-conformance.json -File -Recurse)
    if ($packages.Count -ne 1) { throw 'Require the previously verified native release fixture.' }
    $worker = Join-Path $packages[0].DirectoryName "verified-signed-payload/worker/$runtime"
    Copy-Item -LiteralPath $worker -Destination (Join-Path $scratch 'worker') -Recurse
    Copy-Item -LiteralPath $tests -Destination (Join-Path $scratch 'tests') -Recurse
    $script = Join-Path $scratch 'run-atomic-delete.ps1'
    Copy-Item -LiteralPath $PSCommandPath -Destination $script
    $password = ConvertTo-SecureString ('Aa1!' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) -AsPlainText -Force
    $user = New-LocalUser -Name $name -Password $password -AccountExpires ([datetime]::UtcNow.AddHours(1)) -UserMayNotChangePassword
    $users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    if (@(Get-LocalGroupMember -SID $users | Where-Object { $_.SID.Value -ceq $user.SID.Value }).Count -eq 0) {
        Add-LocalGroupMember -SID $users -Member $user
    }
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($grant in @(@{Path=$scratch;Rights='ReadAndExecute'}, @{Path=$work;Rights='FullControl'})) {
        $directory = [IO.DirectoryInfo]::new($grant.Path)
        $acl = [IO.FileSystemAclExtensions]::GetAccessControl($directory)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user.SID, $grant.Rights, $inherit, 'None', 'Allow'))
        [IO.FileSystemAclExtensions]::SetAccessControl($directory, $acl)
    }
    $credential = [pscredential]::new($env:COMPUTERNAME + '\' + $name, $password)
    $childEnvironment = @{}
    foreach ($variable in Get-ChildItem Env:) {
        if ($variable.Name -notin @('SystemRoot', 'WINDIR', 'SystemDrive', 'COMSPEC', 'PATH', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432')) {
            $childEnvironment[$variable.Name] = $null
        }
    }
    $childEnvironment['TEMP'] = $work
    $childEnvironment['TMP'] = $work
    $childEnvironment['DOTNET_CLI_HOME'] = $work
    $childEnvironment['DOTNET_NOLOGO'] = '1'
    $childEnvironment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $arguments = @('-NoProfile', '-NonInteractive', '-File', ('"' + $script + '"'), '-Payload', ('"' + $scratch + '"'),
        '-ExpectedSid', $user.SID.Value, '-Dotnet', ('"' + $dotnetPath + '"'))
    $launch = @{FilePath=$pwshPath;Credential=$credential;LoadUserProfile=$true;WindowStyle='Hidden';PassThru=$true;
        WorkingDirectory=$scratch;ArgumentList=$arguments;Environment=$childEnvironment;
        RedirectStandardOutput=(Join-Path $scratch 'stdout.log');RedirectStandardError=(Join-Path $scratch 'stderr.log')}
    $process = Start-Process @launch
    if (-not $process.WaitForExit(90000)) { throw 'The ordinary-user atomic Delete fixture exceeded its Worker test budget.' }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $trx = Join-Path $work 'ordinary-user.trx'
    if (Test-Path -LiteralPath $trx -PathType Leaf) {
        Copy-Item -LiteralPath $trx -Destination (Join-Path $output 'ordinary-user.trx')
        [xml]$report = Get-Content -LiteralPath $trx -Raw
        $result = @($report.TestRun.Results.UnitTestResult)
        if ($result.Count -eq 1 -and $result[0].outcome -ne 'Passed') { Write-Host $result[0].Output.ErrorInfo.Message }
    } else {
        Write-Host "Ordinary-user test exit code: $($process.ExitCode)."
        throw 'The ordinary-user fixture produced no test result.'
    }
    if ($process.ExitCode -ne 0 -or $result.Count -ne 1 -or $result[0].outcome -cne 'Passed') {
        throw 'The actual Local Worker could not complete its original Delete under the ordinary test user.'
    }
} finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    if ($null -ne $user) { Remove-LocalUser -SID $user.SID }
    if ($ownedWorkspace) {
        $resolved = (Resolve-Path -LiteralPath $scratch).Path
        if ([IO.Path]::GetDirectoryName($resolved) -cne [IO.Path]::GetFullPath($base) -or
            [IO.Path]::GetFileName($resolved) -cne $name -or (Get-Item -LiteralPath $resolved).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'The disposable Local fixture escaped its designated parent.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
