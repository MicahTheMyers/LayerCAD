param(
    [Parameter(Mandatory)][string]$Zig,
    [Parameter(Mandatory)][string]$ManifoldSource
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $ManifoldSource).Path
$compiler = (Resolve-Path -LiteralPath $Zig).Path
$files = @(
    'boolean3', 'boolean_result', 'constructors', 'csg_tree', 'edge_op',
    'execution_impl', 'face_op', 'impl', 'manifold', 'minkowski', 'polygon',
    'properties', 'quickhull', 'sdf', 'smoothing', 'sort', 'subdivision', 'tree2d'
) | ForEach-Object { Join-Path $source "src/$_.cpp" }

$build = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "LayerCadManifold-$([Guid]::NewGuid())")
Push-Location $build.FullName
try {
    & $compiler c++ -target x86_64-windows-gnu -std=c++17 -O2 -g0 -shared -DMANIFOLD_PAR=-1 `
        "-I$(Join-Path $source 'include')" @files (Join-Path $PSScriptRoot 'LayerCadManifold.cpp') `
        -o LayerCadManifold.dll
    if ($LASTEXITCODE -ne 0) { throw "Manifold build failed with exit code $LASTEXITCODE." }
    Copy-Item -LiteralPath 'LayerCadManifold.dll' -Destination $PSScriptRoot
}
finally {
    Pop-Location
    Get-ChildItem -LiteralPath $build.FullName -File | Remove-Item
    Remove-Item -LiteralPath $build.FullName
}
