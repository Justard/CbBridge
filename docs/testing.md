# Testing the bridge

Five layers, none needs a NuGet package or a real codebeamer.

| What | Command | Checks |
|---|---|---|
| Unit tests | `dotnet run --project tests/CbBridge.Tests` | Link parsing, payload mapping, Test Configuration, JSON round trips, AI comment parsing and callbacks, diff and trend, API keys and scopes, event signatures, JUnit, metrics, bench registry, and the **run history against the real system SQLite** (migrations, search, previous/last pass, unicode, parallel writes, clean-up) |
| End-to-end | `python tools/ci/smoke_test.py` | A running bridge against the stand-ins: CI runs, AI comment, JUnit, history API, audit, what reached codebeamer, events and their signatures, MCP |
| MCP | `python tools/mcp/mcp_smoke.py --key dev-agent-key` | The MCP server with the official MCP client library |
| Bench side | `cd framework_stub && python -m unittest discover -s tests` | Sentence catalogue checks, units, matching, context, clean-up order, soft stop and kill with emergency safe state — on the fake bench ([details](bench-steps.md)) |
| Your framework | `python tools/framework_check/check_framework.py --url http://bench:5001 --slow slow.json` | Your framework against the [framework contract](framework-contract.md) |

## Unit tests

```bash
dotnet run --project tests/CbBridge.Tests            # all; exit code 0 = passed
dotnet run --project tests/CbBridge.Tests -- Sqlite  # only tests whose name contains "Sqlite"
```

A small runner (`tests/CbBridge.Tests/TestRunner.cs`) finds every method marked `[Test]`. Add a test by writing a
`[Test] public static void Something()` (or an instance method in a class implementing `IDisposable` for clean-up)
and using `Assert.*`. In VS Code or Visual Studio, set the test project as the startup project to debug a test.

## End-to-end

Start the stand-ins and the bridge (Development settings contain test keys and an event receiver):

```bash
cd mock_codebeamer && python app.py &          # fake codebeamer   :5002
cd framework_stub && python app.py &           # framework         :5001
python tools/agent_stub/agent_stub.py &        # AI agent          :5003
python tools/event_sink/event_sink.py &        # event receiver    :5004
Analysis__Enabled=true Analysis__WebhookUrl=http://localhost:5003/analyze \
  dotnet run --project src/CbBridge.Web --urls http://localhost:5000
python tools/ci/smoke_test.py
```

## Pipeline (GitHub Actions)

`.github/workflows/ci.yml` runs on every push and pull request:

1. **Build + unit tests** on Ubuntu **and Windows**. Windows proves the run history works with Windows' own
   `winsqlite3.dll`.
2. **End-to-end:** starts all stand-ins and the bridge, runs the smoke test, and checks that `openapi.json` is
   current and valid.
3. **Container image:** builds the `Dockerfile`, starts it, checks `/health`, and checks that the system SQLite was
   found inside the image.

Logs of all services are printed at the end of the end-to-end job, also when it fails.
