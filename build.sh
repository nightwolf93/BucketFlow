#!/bin/bash

# Création du dossier de sortie
outDir="out/linux"
mkdir -p $outDir

# Build pour Linux
dotnet publish \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --output $outDir \
    /p:PublishSingleFile=true \
    /p:PublishTrimmed=true

# Copie des fichiers de configuration
cp appsettings*.json $outDir 2>/dev/null || true
cp keys.json $outDir 2>/dev/null || true

# Création du script de démarrage
cat > "$outDir/start.sh" << 'EOF'
#!/bin/bash
chmod +x ./BucketFlow
./BucketFlow
EOF

# S'assurer que le script de démarrage est exécutable
chmod +x "$outDir/start.sh"