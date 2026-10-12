# Starts the fake codebeamer (port 5002) and the framework stand-in (port 5001) in their own windows.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Start-Process python -ArgumentList "app.py" -WorkingDirectory (Join-Path $root "mock_codebeamer")
Start-Process python -ArgumentList "app.py" -WorkingDirectory (Join-Path $root "framework_stub")
Write-Host "Fake codebeamer: http://localhost:5002/cb   Framework stub: http://localhost:5001"
