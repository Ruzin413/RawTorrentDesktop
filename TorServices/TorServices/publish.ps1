# Publish script for RawTorrent (TorServices)
$projectPath = ".\TorServices.csproj"
$outputPath = ".\publish"

Write-Host "Publishing RawTorrent as a single-file self-contained EXE..." -ForegroundColor Cyan

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outputPath

if ($LASTEXITCODE -eq 0) {
    Write-Host "Publish successful! EXE is located in: $outputPath" -ForegroundColor Green
} else {
    Write-Host "Publish failed." -ForegroundColor Red
}
