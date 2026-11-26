param(
    [Parameter(Mandatory = $true)]
    [string]$GameDirectory
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$game = (Resolve-Path -LiteralPath $GameDirectory).Path
if (!(Test-Path (Join-Path $game 'BepInEx/core/BepInEx.dll'))) {
    throw "BepInEx was not found in: $game"
}

$native = Join-Path $PSScriptRoot 'third_party/native'
New-Item -ItemType Directory -Path $native -Force | Out-Null

#googles precomp. windows utilies releases contain static libs and comnmand line tools not shared dlls for p/invoke. keep in mind download native runtime nuget archive, which has packages for win. x64 shared libs.
$webp = Join-Path $native 'libwebp.dll'
$demux = Join-Path $native 'libwebpdemux.dll'
if (!(Test-Path -LiteralPath $webp) -or !(Test-Path -LiteralPath $demux)) {
    Write-Host 'Fetching libwebp 1.6.0 windows x64 shared DLLs from Imazen NativeRuntime 1.6.1 (build-time only)...'
    $url = 'https://api.nuget.org/v3-flatcontainer/imazen.webp.nativeruntime.win-x64/1.6.1/imazen.webp.nativeruntime.win-x64.1.6.1.nupkg'
    $tempDir = Join-Path ([IO.Path]::GetTempPath()) ('gunspray-webp-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        # nupkg is zip archive, rename for expand
        $archive = Join-Path $tempDir 'native-webp.zip'
        Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
        $expanded = Join-Path $tempDir 'unpacked'
        Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
        $winX64 = Join-Path $expanded 'runtimes/win-x64/native'
        if (!(Test-Path -LiteralPath $winX64)) {
            throw "NuGet archive does not contain runtimes/win-x64/native. Package layout may have changed."
        }
        $libraries = @(Get-ChildItem -LiteralPath $winX64 -Recurse -File -Filter '*.dll')
        if ($libraries.Count -eq 0) { throw 'NuGet archive contains no windows x64 native DLLs.' }
        $core = $libraries | Where-Object { $_.Name -ieq 'libwebp.dll' } | Select-Object -First 1
        $demuxFile = $libraries | Where-Object { $_.Name -ieq 'libwebpdemux.dll' } | Select-Object -First 1
        if (!$core -or !$demuxFile) {
            Write-Host 'DLLs found in win-x64 package:'
            $libraries | ForEach-Object { Write-Host ('  ' + $_.Name) }
            throw 'Expected libwebp.dll and libwebpdemux.dll not found in NuGet windows x64 package.'
        }
        # preserve og name so native import table dependencies resolve. copy additional native dll from this package.
        foreach ($lib in $libraries) {
            Copy-Item -LiteralPath $lib.FullName -Destination (Join-Path $native $lib.Name) -Force
        }
        Write-Host 'Native .webp DLLs prepared for embedding:'
        $libraries | ForEach-Object { Write-Host ('  ' + $_.Name) }
    }
    finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Restoring .gif decoder (StbImageSharp 2.30.16 via NuGet)...'
& dotnet restore (Join-Path $PSScriptRoot 'GunSpray.csproj')
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

Write-Host 'Building GunSpray v1.0.0'
& dotnet build (Join-Path $PSScriptRoot 'GunSpray.csproj') -c Release --no-restore "-p:GameDirectory=$game"
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

$builtDll = Join-Path $PSScriptRoot 'bin/Release/netstandard2.0/GunSpray.dll'
if (!(Test-Path $builtDll)) { throw "Expected output not found: $builtDll" }
Write-Host ''
Write-Host 'Built single-file plugin:' -ForegroundColor Green
Write-Host $builtDll -ForegroundColor Green
Write-Host 'Done. Only copy GunSpray.dll to BepInEx/plugins.'
