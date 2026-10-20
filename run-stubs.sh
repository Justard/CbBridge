#!/usr/bin/env bash
# Starts the fake codebeamer (port 5002) and the framework stand-in (port 5001). Ctrl+C stops both.
cd "$(dirname "$0")"
(cd mock_codebeamer && python3 app.py) &
(cd framework_stub && python3 app.py) &
echo "Fake codebeamer: http://localhost:5002/cb   Framework stub: http://localhost:5001"
wait
