# CB Test Bridge

A web console that connects a **test management system** (ALM) to **your own test framework**:

1. **Extract** test runs from ALM by pasting a link — project, category or item — or use **Run multiple**
   to pick test cases grouped by their Testabläufe, creating a new test run per test case.
2. **Review and edit** them: steps, parameters, releases/versions, test configuration, time limit.
3. **Run** them one by one on the test system you choose, with hard time limits that really stop a test.
4. **Post results back** to ALM — live, once after review, or not at all.
5. **Automate it** from Jenkins or any CI through a small API ([CI / Jenkins](docs/ci-jenkins.md)).
6. **Explain failures** (optional): failed tests go to an AI agent on any platform, whose short comment for
   developers is shown and added to the codebeamer test run ([AI analysis](docs/ai-analysis.md)).
7. **Keep the history:** every run in a database. Shows *NEW FAIL* / *FIXED* against the previous run, a diff
   against the last pass, each test's timeline, **Rerun failed**, shareable result links and an audit log of every
   codebeamer write.
8. **Connect everything else:** per-client API keys with scopes, a read API with OpenAPI, signed event webhooks
   (also Teams/Slack messages), Prometheus metrics, and an **MCP server** so AI assistants can query results and
   start runs ([API](docs/api.md), [MCP](docs/mcp.md)).

The product name, logo and colours are configurable, so any company can run it under its own brand
(see [Branding](docs/branding.md)).

![Console](docs/images/console-references.png)

## Documentation

| Document | For | What it covers |
|---|---|---|
| [User guide](docs/user-guide.md) | Test engineers | Signing in, extracting, editing, My tests, running, posting results |
| [Installation & configuration](docs/installation-and-configuration.md) | Admins | Requirements, install, every setting, connecting real systems, troubleshooting |
| [Branding](docs/branding.md) | Admins | Company logo, name, colours, favicon |
| [Framework contract](docs/framework-contract.md) | Framework developers | The HTTP API your test framework must offer, time limits, verdicts |
| [AI analysis](docs/ai-analysis.md) | Agent developers, admins | Contract for the failure-analysis agent (n8n, Zapier, own service) |
| [CI / Jenkins](docs/ci-jenkins.md) | Admins, CI engineers | Trigger a category, a Testablauf or a single test from Jenkins; JUnit results |
| [API, events and metrics](docs/api.md) | Integrators | Keys and scopes, /api/v1, webhooks, /metrics, OpenAPI |
| [MCP server](docs/mcp.md) | AI agent builders | The bridge's tools for AI assistants |
| [Testing](docs/testing.md) | Developers | Unit tests, end-to-end test, framework contract check, GitHub Actions |
| [Licensing](docs/licensing.md) | Copyright holder, admins | Licence keys: making, issuing, installing; what the lock protects |
| [Architecture](docs/architecture.md) | Developers | Layers, data flow, extension points (e.g. Xray), design decisions |

## Quick start (local, with the built-in stand-ins)

Needs the **.NET 8 SDK** and **Python 3.10+** with `pip install flask`.

```powershell
python mock_codebeamer/app.py                 # fake codebeamer   → http://localhost:5002/cb
python framework_stub/app.py --port 5001      # framework stand-in → http://localhost:5001
dotnet run --project src/CbBridge.Web         # the bridge         → URL printed in the console
```

Or in VS Code: *Run and Debug* → **Everything: bridge + framework stub + fake ALM** → F5.
Sign in with any username/password and paste `10231` (a test case with 14 generated test runs).
Details: [Installation & configuration](docs/installation-and-configuration.md).

## Repository layout

```
CbBridge/
├─ CbBridge.sln
├─ src/CbBridge.Core/           shared logic (class library, .NET 8, no NuGet packages)
│  ├─ Domain/                   payload and result types
│  ├─ Application/              selection, editing, run loop, CI jobs, history, API clients, events, AI analysis
│  └─ Infrastructure/           codebeamer clients, framework client, SQLite history, webhooks, AI agent client
├─ src/CbBridge.Web/            the web console + CI API (ASP.NET Core Blazor Server)
│  ├─ Components/               pages: sign-in, console, history, run, result, benches, audit
│  ├─ Ci/                       /api/ci endpoints for Jenkins, AI callback
│  ├─ Api/                      /api/v1 read API, key and scope checks
│  ├─ Mcp/                      /mcp — MCP server and its tools
│  ├─ wwwroot/api/openapi.json  OpenAPI description (built by tools/openapi)
│  ├─ wwwroot/branding/         ← put your company logo here
│  └─ appsettings.json          all settings
├─ tests/CbBridge.Tests/        unit tests (own small runner, no packages)
├─ tools/ci/                    bridge_ci.py, Jenkinsfile, end-to-end smoke test
├─ tools/framework_check/       contract check for your test framework
├─ tools/agent_stub/, event_sink/, mcp/, openapi/   stand-ins and helpers for local testing
├─ Dockerfile                   container image
├─ .github/workflows/ci.yml     build, tests (Linux + Windows), end-to-end, container
├─ framework_stub/              reference implementation of the framework contract (Flask + CLI)
├─ mock_codebeamer/             fake codebeamer for local development (invented data)
├─ docs/                        documentation
└─ .vscode/                     run/debug setup for VS Code
```

## Status

Working model. 44 unit tests and an end-to-end smoke test run against the bundled stand-ins on every push; **not yet verified against a real ALM
instance** — field names and the result upload format must be checked there first
(see [Installation & configuration → Connecting a real codebeamer](docs/installation-and-configuration.md#connecting-a-real-codebeamer)).

## Licence

Proprietary — © 2026 Justard. All rights reserved. Using the software needs a licence key from the copyright
holder; see [LICENSE](LICENSE) and [Licensing](docs/licensing.md). Prerelease builds (the default while developing and
testing) show the licence but never block; release builds from a version tag enforce it.
