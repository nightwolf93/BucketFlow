# Création du dossier de sortie
$outDir = "out/linux"
New-Item -ItemType Directory -Force -Path $outDir

# Build pour Linux
dotnet publish `
    --configuration Release `
    --runtime linux-x64 `
    --self-contained true `
    --output $outDir `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false

# Copie des fichiers de configuration
Copy-Item "appsettings*.json" -Destination $outDir -ErrorAction SilentlyContinue
Copy-Item "keys.json" -Destination $outDir -ErrorAction SilentlyContinue

# Création du script de démarrage
@"
#!/bin/bash
chmod +x ./BucketFlow
./BucketFlow
"@ | Out-File -FilePath "$outDir/start.sh" -Encoding UTF8 -NoNewline

# Conversion des fins de ligne en format Linux
((Get-Content "$outDir/start.sh") -join "`n") + "`n" | Set-Content -NoNewline "$outDir/start.sh" 