param(
    [string]$Output = (Join-Path $PSScriptRoot 'publish\release'),
    [string]$ZipPath = ''
)

$ErrorActionPreference = 'Stop'
$dotnet = Join-Path (Split-Path $PSScriptRoot -Parent) '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet).Source
}

function Copy-Tree([string]$Source, [string]$Target) {
    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
        if ($_.Extension -ne '.pdb') {
            $relative = $_.FullName.Substring($Source.Length).TrimStart('\')
            $targetFile = Join-Path $Target $relative
            New-Item -ItemType Directory -Path (Split-Path $targetFile) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $targetFile -Force
        }
    }
}

$project = Join-Path $PSScriptRoot 'src\XiaoZhiLedger.App\XiaoZhiLedger.App.csproj'
& $dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE."
}

$dotnetRoot = Split-Path $dotnet -Parent
$coreVersion = Get-ChildItem (Join-Path $dotnetRoot 'shared\Microsoft.NETCore.App') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$desktopVersion = Get-ChildItem (Join-Path $dotnetRoot 'shared\Microsoft.WindowsDesktop.App') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$hostFxr = Get-ChildItem (Join-Path $dotnetRoot 'host\fxr') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1

$publishRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'publish'))
$outputFullPath = [IO.Path]::GetFullPath($Output)
$publishPrefix = $publishRoot.TrimEnd('\') + '\'
if (-not $outputFullPath.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output must be a child directory of $publishRoot"
}
if (Test-Path -LiteralPath $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}
New-Item -ItemType Directory -Path $outputFullPath -Force | Out-Null
Copy-Tree (Join-Path $PSScriptRoot 'src\XiaoZhiLedger.App\bin\Release\net8.0-windows') $outputFullPath
Copy-Tree $coreVersion.FullName (Join-Path $outputFullPath "shared\Microsoft.NETCore.App\$($coreVersion.Name)")
Copy-Tree $desktopVersion.FullName (Join-Path $outputFullPath "shared\Microsoft.WindowsDesktop.App\$($desktopVersion.Name)")
Copy-Item (Join-Path $hostFxr.FullName 'hostfxr.dll') $outputFullPath -Force
Copy-Item (Join-Path $dotnetRoot 'LICENSE.txt') (Join-Path $outputFullPath 'DOTNET_LICENSE.txt') -Force
Copy-Item (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') (Join-Path $outputFullPath 'DOTNET_THIRD_PARTY_NOTICES.txt') -Force

Copy-Item (Join-Path $PSScriptRoot 'README.md') (Join-Path $outputFullPath 'README.md') -Force
Copy-Item (Join-Path $PSScriptRoot 'docs\RELEASE_GUIDE.txt') (Join-Path $outputFullPath 'RELEASE_GUIDE.txt') -Force

if (-not [string]::IsNullOrWhiteSpace($ZipPath)) {
    $zipFullPath = [IO.Path]::GetFullPath($ZipPath)
    $zipDirectory = Split-Path $zipFullPath -Parent
    New-Item -ItemType Directory -Path $zipDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $zipFullPath) {
        Remove-Item -LiteralPath $zipFullPath -Force
    }
    Compress-Archive -LiteralPath $outputFullPath -DestinationPath $zipFullPath -CompressionLevel Optimal
    Write-Host "ZIP complete: $zipFullPath"
}

Write-Host "Publish complete: $outputFullPath"
