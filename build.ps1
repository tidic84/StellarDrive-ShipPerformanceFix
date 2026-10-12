# Builds StellarRooms and packs the release zip in the layout StellarModManager expects:
#   StellarDrive-StellarRooms.zip
#     mod.json, Thumbnail.png
#     Mods/StellarRooms.dll
param(
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\StellarDrive Demo",
    [string]$MelonLoaderPath = "",
    [string]$ModsPath = ""
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$mod = "StellarRooms"
$args = @("build", "$root\src\$mod\$mod.csproj", "-c", "Release", "-p:GamePath=$GamePath")
if ($MelonLoaderPath) { $args += "-p:MelonLoaderPath=$MelonLoaderPath" }
if ($ModsPath) { $args += "-p:ModsPath=$ModsPath" }
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$stage = Join-Path $root "dist\stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\Mods" | Out-Null
Copy-Item "$root\bin\Release\$mod.dll" "$stage\Mods\"
Copy-Item "$root\Mods\*" "$stage\"

$zip = Join-Path $root "dist\StellarDrive-$mod.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
# Write entries by hand: Windows PowerShell's CreateFromDirectory stores "\" separators, which is not valid zip.
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry) | Out-Null
    }
} finally {
    $archive.Dispose()
}
Remove-Item $stage -Recurse -Force
Write-Host "Package: $zip"
