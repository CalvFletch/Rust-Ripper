# Rebuild + redeploy the ripper daemon without file-lock fights.
# The daemon runs from tools/run (a copy of bin), so `dotnet build` into bin
# never collides with the running process. Usage:  ./tools/deploy-daemon.ps1
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $repo "RustRipper.Cli\bin\Release\net9.0"
$run = Join-Path $PSScriptRoot "run"

dotnet build (Join-Path $repo "RustRipper.Cli\RustRipper.Cli.csproj") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit 1 }

Get-Process -Name ripper -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
robocopy $bin $run /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Error "robocopy failed"; exit 1 }

Start-Process -FilePath (Join-Path $run "ripper.exe") -ArgumentList "serve", "--port", "17071" -WindowStyle Hidden
Write-Host "daemon deploying; poll http://127.0.0.1:17071/status"
