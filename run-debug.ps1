$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'MemCardTest\MemCardTest.csproj'
$exe  = Join-Path $root 'MemCardTest\bin\Debug\net472\MemCardTest.exe'

dotnet build $proj -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
