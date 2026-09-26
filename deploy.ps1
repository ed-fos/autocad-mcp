<#
    Nodal.AcadMcp deploy
    --------------------
    Builds BOTH halves and installs them locally:

      * the add-in, as an AutoCAD autoloader bundle in %APPDATA%, so AutoCAD finds it
        without a manual NETLOAD
      * the MCP server, published to %LOCALAPPDATA%\Nodal.AcadMcp\server, which is the
        path you point your MCP client at

    The bundle keeps its payload in a VERSIONED Contents subfolder (the same pattern
    Autodesk documents for the autoloader). A running AutoCAD holds a lock on
    whichever folder it loaded, so each deploy writes a NEW folder and repoints
    PackageContents.xml at it. That means:
      * dotnet build never fails because acad.exe has the DLL open
      * the next AutoCAD start picks up the new build automatically

    The manifest sets LoadOnAutoCADStartup="false" on purpose. The bridge executes
    arbitrary code, so it is opt-in per session: type NODALMCPSTART in AutoCAD.

    Runs on Windows PowerShell 5.1 (which ships with Windows) as well as pwsh 7.

    Usage:  powershell -ExecutionPolicy Bypass -File deploy.ps1        # build + deploy both
            ... -File deploy.ps1 -NoBuild          # deploy the add-in already built
            ... -File deploy.ps1 -Package          # also produce a distributable zip
            ... -File deploy.ps1 -Configuration Debug

    -Package always publishes a fresh SELF-CONTAINED server into the zip (even with
    -NoBuild), so the release needs no .NET runtime on the target machine. The locally
    deployed server stays framework-dependent, which is faster to iterate on.
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$Package,
    [string]$FromDir,
    [string]$Configuration = 'Release',
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'

$root        = Split-Path -Parent $MyInvocation.MyCommand.Path
$addinProj   = Join-Path $root 'src\Nodal.AcadMcp.Addin\Nodal.AcadMcp.Addin.csproj'
$serverProj  = Join-Path $root 'src\Nodal.AcadMcp.Server\Nodal.AcadMcp.Server.csproj'
$bundleName  = 'Nodal.AcadMcp.bundle'
$bundleRoot  = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\$bundleName"
$serverRoot  = Join-Path $env:LOCALAPPDATA 'Nodal.AcadMcp\server'
$tfmAddin    = 'net10.0-windows'

# ---------------------------------------------------------------- locate the SDK
# PATH first, so this works on a machine that is not the author's. A user-local SDK
# install (~\.dotnet) is common and is often not on PATH, so fall back to it, and let
# -DotnetPath override everything.
function Test-HasNet10Sdk {
    param([string]$Path)

    # A dotnet on PATH is very often the RUNTIME-only install, which has no SDKs at all and
    # cannot build anything. Finding the executable is not the same as finding a usable SDK,
    # so probe it rather than trust the name.
    try { $sdks = & $Path --list-sdks 2>$null } catch { return $false }
    if (-not $sdks) { return $false }
    return [bool]($sdks | Where-Object { $_ -match '^10\.' })
}

function Resolve-Dotnet {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "-DotnetPath '$Explicit' does not exist." }
        return $Explicit
    }

    $candidates = @()

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }

    # A user-local SDK install is common and is frequently NOT on PATH.
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')

    $seen = @{}
    foreach ($c in $candidates) {
        if (-not $c -or $seen.ContainsKey($c) -or -not (Test-Path $c)) { continue }
        $seen[$c] = $true
        if (Test-HasNet10Sdk -Path $c) { return $c }
    }

    throw ("Could not find a dotnet installation with a .NET 10 SDK. Checked: " +
           ($seen.Keys -join ', ') + ". Install the .NET 10 SDK, or pass -DotnetPath.")
}

$dotnet = Resolve-Dotnet -Explicit $DotnetPath
Write-Host "Using SDK: $dotnet" -ForegroundColor DarkGray

# ---------------------------------------------------------------- build
if (-not $NoBuild) {
    Write-Host "Building add-in ($Configuration)..." -ForegroundColor Cyan
    & $dotnet build $addinProj -c $Configuration -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw "Add-in build failed with exit code $LASTEXITCODE." }

    Write-Host "Publishing MCP server ($Configuration)..." -ForegroundColor Cyan
    & $dotnet publish $serverProj -c $Configuration -v quiet --nologo -o $serverRoot
    if ($LASTEXITCODE -ne 0) { throw "Server publish failed with exit code $LASTEXITCODE." }
}

$buildDir = if ($FromDir) {
    $FromDir
} else {
    Join-Path $root ('src\Nodal.AcadMcp.Addin\bin\' + $Configuration + '\' + $tfmAddin)
}
if (-not (Test-Path (Join-Path $buildDir 'Nodal.AcadMcp.Addin.dll'))) {
    throw "No build output at $buildDir. Run without -NoBuild first."
}

# ---------------------------------------------------------------- versioned payload
# File-time based so every deploy is a fresh folder even within the same minute.
$stamp       = (Get-Item (Join-Path $buildDir 'Nodal.AcadMcp.Addin.dll')).LastWriteTime.ToString('yyyyMMdd-HHmmss')
$contentsRel = "Contents/$stamp"
$contentsAbs = Join-Path $bundleRoot "Contents\$stamp"

New-Item -ItemType Directory -Force -Path $contentsAbs | Out-Null

Get-ChildItem -Path $buildDir -File |
    Where-Object { $_.Extension -in '.dll', '.pdb', '.json' } |
    ForEach-Object { Copy-Item $_.FullName -Destination $contentsAbs -Force }

Write-Host "Add-in  -> $contentsAbs" -ForegroundColor Green
if (-not $NoBuild) { Write-Host "Server  -> $serverRoot" -ForegroundColor Green }

# ---------------------------------------------------------------- manifest
# SeriesMin/Max R26.0 == AutoCAD 2027. ADT covers AutoCAD Architecture.
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage
  SchemaVersion="1.0"
  AppVersion="$stamp"
  Author="Ed Foster"
  Name="Nodal AcadMcp"
  ProductCode="{7E2C9A14-6B3D-4F58-9C21-0A5E4D8B7F36}"
  UpgradeCode="{2B8F41D7-5C6A-4E39-BD02-9F7A3C1E6D84}"
  Description="Nodal MCP bridge for AutoCAD and AutoCAD Architecture">
  <CompanyDetails Name="Nodal" Url="https://github.com/ed-fos/autocad-mcp" Email="" />
  <RuntimeRequirements Platform="AutoCAD|ADT" SeriesMin="R26.0" SeriesMax="R26.0"
                       OS="Win64" SupportPath="./$contentsRel" />
  <Components>
    <!-- No AppType attribute: "ManagedDotNet" is not a valid value and makes the autoloader
         silently skip the entry. Autodesk's own shipped bundles omit it and let AutoCAD infer
         the module type from the extension.

         LoadOnAutoCADStartup is TRUE, and that is not a security regression — loading the
         module is not the same as opening the bridge. Initialize() registers commands and the
         ribbon and opens NO socket; the endpoint that executes arbitrary code stays shut until
         the user clicks Start or types NODALMCPSTART.

         It was briefly FALSE, mirroring Autodesk's own MCP bundle, and that was tested and
         found broken: with the module unloaded AutoCAD does not know NODALMCPSTART exists, so
         the very command meant to demand-load it reports "Unknown command" and a clean install
         cannot be started at all. Demand-loading a command needs the command declared in this
         manifest; loading at startup and withholding the socket is simpler and safer. -->
    <ComponentEntry
      AppName="NodalAcadMcp"
      ModuleName="./$contentsRel/Nodal.AcadMcp.Addin.dll"
      LoadOnAutoCADStartup="true"
      LoadOnRequest="true" />
  </Components>
</ApplicationPackage>
"@

# UTF8 without BOM — the autoloader is fussy about a leading BOM before the XML decl.
[System.IO.File]::WriteAllText(
    (Join-Path $bundleRoot 'PackageContents.xml'),
    $manifest,
    (New-Object System.Text.UTF8Encoding $false))

Write-Host "Manifest -> $bundleRoot\PackageContents.xml (version $stamp)" -ForegroundColor Green

# ---------------------------------------------------------------- prune old payloads
# Anything still locked by a running AutoCAD simply fails to delete; that is fine.
$kept = 0; $pruned = 0
Get-ChildItem -Path (Join-Path $bundleRoot 'Contents') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne $stamp } |
    Sort-Object Name -Descending |
    ForEach-Object {
        try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop; $pruned++ }
        catch { $kept++ }
    }

# ---------------------------------------------------------------- optional release zip
if ($Package) {
    $distDir = Join-Path $root 'artifacts'
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null

    $stage = Join-Path $distDir 'stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage $bundleName) | Out-Null

    Copy-Item (Join-Path $bundleRoot '*') -Destination (Join-Path $stage $bundleName) -Recurse -Force

    # SELF-CONTAINED for the release artifact, unlike the local deploy above. The audience is
    # CAD users, not developers: a framework-dependent exe would silently need a .NET 10
    # runtime they have no reason to have installed, and the failure ("you must install
    # .NET") looks like a broken download. Bigger zip, zero install steps.
    Write-Host "Publishing self-contained server for the release zip..." -ForegroundColor Cyan
    & $dotnet publish $serverProj -c $Configuration -v quiet --nologo `
        -r win-x64 --self-contained true -o (Join-Path $stage 'server')
    if ($LASTEXITCODE -ne 0) { throw "Self-contained server publish failed with exit code $LASTEXITCODE." }

    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE'),
              (Join-Path $root 'NOTICE'), (Join-Path $root 'SECURITY.md'),
              (Join-Path $root 'CHANGELOG.md') -Destination $stage -Force

    $zip = Join-Path $distDir "Nodal.AcadMcp-acad2027-$stamp.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    # The entry paths are built by hand, on purpose. The ZIP format requires '/' as the path
    # separator (APPNOTE 4.4.17.1), and on Windows PowerShell 5.1 BOTH of the obvious ways to
    # write a zip get this wrong and emit '\':
    #   * Compress-Archive
    #   * ZipFile.CreateFromDirectory, because .NET Framework builds entry names with
    #     Path.DirectorySeparatorChar (fixed only in .NET Core 3.0+)
    # Explorer is lenient about it, but plenty of extractors are not: they produce one flat
    # directory of files literally named "server\Nodal.AcadMcp.Server.exe", at which point the
    # install instructions ("copy the bundle folder") have no folder to copy. Verified by
    # inspecting the entry names of a built zip, not assumed.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression

    $archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        $stageRoot = (Resolve-Path $stage).Path.TrimEnd('\') + '\'
        foreach ($file in (Get-ChildItem $stage -Recurse -File)) {
            $relative = $file.FullName.Substring($stageRoot.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $relative,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }

    # Fail loudly rather than shipping a subtly broken archive.
    $check = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $bad = @($check.Entries | Where-Object { $_.FullName -like '*\*' }).Count
        if ($bad -gt 0) { throw "$bad zip entries use backslash separators; the archive is malformed." }
        Write-Host "Verified $($check.Entries.Count) zip entries use '/' separators." -ForegroundColor DarkGray
    }
    finally { $check.Dispose() }

    Remove-Item $stage -Recurse -Force

    Write-Host "Package -> $zip" -ForegroundColor Green
}

Write-Host ""
Write-Host "Deployed. Pruned $pruned old payload(s); $kept still locked by a running AutoCAD." -ForegroundColor Cyan
Write-Host "Restart AutoCAD, then type NODALMCPSTART to open the bridge for that session." -ForegroundColor Cyan
Write-Host "Point your MCP client at: $serverRoot\Nodal.AcadMcp.Server.exe" -ForegroundColor Cyan
