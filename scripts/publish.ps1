$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $project = 'src/FotoSortierer.App/FotoSortierer.App.csproj'
    dotnet restore $project -r win-x64 -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw 'Wiederherstellen der Pakete fehlgeschlagen.' }

    # Lizenzhinweise in die EXE einbetten.
    $notices = Join-Path $repo 'artifacts/notices/RuntimeNotices.txt'
    New-Item (Split-Path $notices -Parent) -ItemType Directory -Force | Out-Null
    $metadataLicense = (Invoke-WebRequest 'https://raw.githubusercontent.com/drewnoakes/metadata-extractor-dotnet/main/LICENSE').Content
    Set-Content $notices -Value "METADATAEXTRACTOR`n`n$metadataLicense" -Encoding utf8
    $assets = Get-Content 'src/FotoSortierer.App/obj/project.assets.json' -Raw | ConvertFrom-Json -AsHashtable
    $foundRuntime = $false
    $foundDesktop = $false
    $packages = @($assets.libraries.Keys)
    foreach ($framework in $assets.project.frameworks.Values) {
        foreach ($dependency in $framework.downloadDependencies) {
            $version = ($dependency.version -replace '[\[\]\(\)]', '').Split(',')[0].Trim()
            $packages += "$($dependency.name)/$version"
        }
    }
    foreach ($cache in $assets.packageFolders.Keys) {
        foreach ($library in ($packages | Select-Object -Unique)) {
            if ($library -notmatch '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64/') { continue }
            $folder = Join-Path $cache $library.ToLowerInvariant()
            if (!(Test-Path $folder)) { continue }
            $licenseFiles = @(Get-ChildItem $folder -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY.NOTICES)(\.txt)?$' })
            if ($licenseFiles.Count -eq 0) { throw "Lizenzen nicht gefunden: $library" }
            foreach ($file in $licenseFiles) {
                Add-Content $notices -Value "`n`n$library – $($file.Name)`n`n$(Get-Content $file.FullName -Raw)" -Encoding utf8
            }
            if ($library -match '^Microsoft.NETCore') { $foundRuntime = $true }
            if ($library -match '^Microsoft.WindowsDesktop') { $foundDesktop = $true }
        }
    }
    if (!$foundRuntime -or !$foundDesktop) { throw 'Runtime-Lizenzpakete nicht vollständig gefunden.' }
    $output = Join-Path $repo 'artifacts/win-x64'
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false `
        "-p:GeneratedNoticesPath=$notices" -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Veröffentlichen fehlgeschlagen.' }
    $files = @(Get-ChildItem $output -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'FotoSortierer.exe') { throw 'Erwartet wurde genau eine FotoSortierer.exe.' }
    Write-Host "Fertig: $output/FotoSortierer.exe"
} finally { Pop-Location }
