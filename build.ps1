$ErrorActionPreference = "Stop"
dotnet restore "$PSScriptRoot\SessionMapExporter.sln"
dotnet build "$PSScriptRoot\SessionMapExporter.sln" -c Release
Write-Host ""
Write-Host "Build complete. See SessionMapExporter\bin\Release\net10.0-windows\"
