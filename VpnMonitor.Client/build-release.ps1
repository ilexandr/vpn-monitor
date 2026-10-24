$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot
try {
    dotnet restore
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true

    Write-Host ""
    Write-Host "Готово:" -ForegroundColor Green
    Write-Host "$PSScriptRoot\bin\Release\net8.0-windows\win-x64\publish\"
}
finally {
    Pop-Location
}
