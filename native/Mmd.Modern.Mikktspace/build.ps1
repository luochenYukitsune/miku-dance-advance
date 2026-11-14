param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '../../../.dev-cache/mikktspace-native'),
    [string]$Compiler = '',
    [string[]]$IncludeDirectories = @(),
    [string[]]$LibraryDirectories = @(),
    [switch]$UseCMake,
    [string]$Generator = '',
    [string]$CCompiler = ''
)
$ErrorActionPreference = 'Stop'
$expected = @{
    'mikktspace.h' = '17fc433894f24c73753d548086cc4d8c5c0379f4a6edfb98b5da243e4f0bc3d0'
    'mikktspace.c' = 'de87e74107df766ce68108801262bd8d53899414236b59810509a8fc2a51e288'
}
foreach ($name in $expected.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "upstream/$name") -Algorithm SHA256).Hash -ne $expected[$name]) {
        throw "Pinned upstream hash mismatch: $name"
    }
}
if ($UseCMake) {
    $configure = @('-S', $PSScriptRoot, '-B', $BuildDirectory, '-DCMAKE_BUILD_TYPE=Release')
    if ($Generator) { $configure += @('-G', $Generator) }
    if ($CCompiler) { $configure += "-DCMAKE_C_COMPILER=$CCompiler" }
    & cmake @configure
    if ($LASTEXITCODE -ne 0) { throw 'MikkTSpace CMake configuration failed.' }
    & cmake --build $BuildDirectory --config Release
    if ($LASTEXITCODE -ne 0) { throw 'MikkTSpace native build failed.' }
    & cmake --install $BuildDirectory --config Release --prefix $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'MikkTSpace installation failed.' }
    return
}
if ($Generator -or $CCompiler) { throw '-Generator and -CCompiler require -UseCMake.' }
$nativeCompiler = $Compiler
if (-not $nativeCompiler) {
    $compilerCommand = Get-Command -Name cl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $compilerCommand) { throw 'MSVC cl.exe is not on PATH. Pass -Compiler with its exact file and the required -IncludeDirectories/-LibraryDirectories, or select -UseCMake.' }
    $nativeCompiler = $compilerCommand.Source
}
$nativeBuildPath = [System.IO.Path]::GetFullPath($BuildDirectory)
$nativeOutputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $nativeBuildPath, $nativeOutputPath | Out-Null
$objectDirectory = $nativeBuildPath + [System.IO.Path]::DirectorySeparatorChar
$compileArguments = @('/nologo', '/LD', '/MT', '/O2', '/fp:precise', '/W4', '/TC', '/std:c11',
    "/Fo$objectDirectory", "/Fe$(Join-Path $nativeOutputPath 'mmd_mikktspace.dll')")
foreach ($includeDirectory in $IncludeDirectories) { $compileArguments += "/I$([System.IO.Path]::GetFullPath($includeDirectory))" }
$compileArguments += @((Join-Path $PSScriptRoot 'mmd_mikktspace.c'), (Join-Path $PSScriptRoot 'upstream/mikktspace.c'),
    '/link', '/INCREMENTAL:NO', "/IMPLIB:$(Join-Path $nativeBuildPath 'mmd_mikktspace.lib')", "/PDB:$(Join-Path $nativeBuildPath 'mmd_mikktspace.pdb')")
foreach ($libraryDirectory in $LibraryDirectories) { $compileArguments += "/LIBPATH:$([System.IO.Path]::GetFullPath($libraryDirectory))" }
& $nativeCompiler @compileArguments
if ($LASTEXITCODE -ne 0) { throw 'MikkTSpace direct MSVC native build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE.MikkTSpace.txt') -Destination $nativeOutputPath
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UPSTREAM.json') -Destination $nativeOutputPath
