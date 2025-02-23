# Création du dossier de sortie
$outDir = "out/windows"
New-Item -ItemType Directory -Force -Path $outDir

# Build pour Windows
dotnet publish `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $outDir `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false

# Copie des fichiers de configuration
Copy-Item "appsettings*.json" -Destination $outDir -ErrorAction SilentlyContinue
Copy-Item "keys.json" -Destination $outDir -ErrorAction SilentlyContinue

# Création du script de démarrage
@"
@echo off
start BucketFlow.exe
"@ | Out-File -FilePath "$outDir/start.bat" -Encoding UTF8 -NoNewline 