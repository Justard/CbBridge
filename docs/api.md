# API, events and metrics

For agents, dashboards, scripts and other systems. Everything here is **off by default**.

- [Keys and scopes](#keys-and-scopes)
- [Read API (/api/v1)](#read-api-apiv1)
- [Events (webhooks)](#events-webhooks)
- [Metrics](#metrics)
- [OpenAPI](#openapi)

Related: [MCP server](mcp.md) (the same functions as tools for AI agents) · [CI / Jenkins](ci-jenkins.md) ·
[AI analysis](ai-analysis.md).

---

## Keys and scopes

Every client gets **its own key** with only the **scopes** it needs, so one can be revoked without touching the others
and the logs show who did what.

```json
"Api": {
  "Enabled": true,
  "Clients": [
    { "Name": "jenkins",   "KeySha256": "<sha256 of the key>", "Scopes": [ "runs:start", "runs:read" ] },
    { "Name": "dashboard", "KeySha256": "<sha256 of the key>", "Scopes": [ "history:read" ] },
    { "Name": "claude",    "KeySha256": "<sha256 of the key>", "Scopes": [ "history:read", "feedback:write", "runs:read" ] }
  ]
}
```

| Scope | Allows |
|---|---|
| `runs:start` | Start and abort CI runs (`/api/ci`, MCP `start_run`) |
| `runs:read` | CI job status and JUnit (`/api/ci`, MCP `get_job`) |
| `history:read` | Runs, results, comparisons, test history, `/metrics` |
| `feedback:write` | Rate AI comments |
| `audit:read` | Audit log of codebeamer writes |

- **Keep keys out of the file.** Either store only the hash — `echo -n "THE-KEY" | sha256sum` (Linux) or
  `(Get-FileHash -Algorithm SHA256 -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes("THE-KEY")))).Hash`
  (PowerShell) — or set `Key` through an environment variable: `Api__Clients__0__Key=…`.
- Generate keys with `openssl rand -hex 32`.
- `"Disabled": true` switches a client off without deleting it.
- Configuration mistakes (unknown scope, client without key, duplicate name) are logged as warnings at startup.
- The old single key `Ci:ApiKey` still works, as client `ci` with `runs:start` + `runs:read`.

**Sending the key:** header `X-Api-Key: KEY`, or `Authorization: Bearer KEY`. On `/api/ci` only `X-Api-Key`,
because `Authorization` carries the CI agent's codebeamer login there.

### Starting runs from an agent without its codebeamer password

A client may have a codebeamer account of its own, set on the bridge (environment variables):

```
Api__Clients__2__CodebeamerUser=svc-ai-agent
Api__Clients__2__CodebeamerPassword=…        (or Api__Clients__2__CodebeamerToken=…)
```

Then the MCP tool `start_run` runs with that account. The AI agent never sees the credentials.

## Read API (/api/v1)

| Call | Answer |
|---|---|
| `GET /api/v1/status` | Which client you are, your scopes, what is switched on |
| `GET /api/v1/runs?text=&source=&verdict=&from=&to=&limit=&offset=` | Runs, newest first |
| `GET /api/v1/runs/{runId}` | One run with every result and each test's previous verdict / trend |
| `GET /api/v1/runs/{runId}/results/{position}` | One result: request sent, framework answer, AI analysis |
| `GET /api/v1/runs/{runId}/results/{position}/compare` | Previous run, last passing run, changed lines since then |
| `GET /api/v1/tests/{testKey}/history?limit=` | Every execution of one test; `testKey` = `tc:10232`, `item:88131` or a bare ID |
| `POST /api/v1/runs/{runId}/results/{position}/feedback` | `{"helpful": true, "note": "…"}` — rate the AI comment |
| `GET /api/v1/audit?target=&limit=` | Who wrote what to codebeamer |

Every run and result carries a `link` to its page in the bridge. Set `Bridge:PublicUrl` so these links (and those in
events) use the address people actually reach.

```bash
curl -s -H "X-Api-Key: $KEY" "https://bridge.example.com/api/v1/runs?verdict=FAILED&limit=5"
curl -s -H "X-Api-Key: $KEY" "https://bridge.example.com/api/v1/tests/tc:10232/history"
```

A CI job's status (`GET /api/ci/runs/{id}`) contains `runId`, the ID of the same run in the history.

## Events (webhooks)

The bridge tells other systems about runs as they happen.

| Event | When |
|---|---|
| `run.started` | A run begins (console, CI or agent) |
| `test.failed` | A test ends FAILED, TIMEOUT, ERROR or BLOCKED |
| `run.finished` | A run ends — with the summary and the failed tests |
| `analysis.done` | An AI comment arrived |

```json
"Events": {
  "Subscribers": [
    { "Name": "n8n",   "Url": "https://n8n.example.com/webhook/bridge-events", "Secret": "…" },
    { "Name": "teams", "Url": "https://…/IncomingWebhook/…", "Format": "text", "Events": [ "run.finished" ], "OnlyProblems": true }
  ]
}
```

| Setting | Meaning |
|---|---|
| `Events` | Which events; empty = all |
| `Format` | `json` (full event, see the OpenAPI `webhooks` section) or `text` — `{"text": "❌ testablauf 88201: 11/12 passed …"}`, which Teams and Slack incoming webhooks show as a message |
| `OnlyProblems` | Only events where something failed — good for chat channels |
| `Secret` | Signs the body: header `X-Bridge-Signature: sha256=<hex HMAC-SHA256 of the raw body>` |
| `Disabled` | Pause a subscriber |

Each event is sent in the background, with up to three attempts; a slow receiver never slows a run.

**Checking the signature** (Python):

```python
import hashlib, hmac
expected = "sha256=" + hmac.new(SECRET.encode(), raw_body, hashlib.sha256).hexdigest()
ok = hmac.compare_digest(request.headers["X-Bridge-Signature"], expected)
```

`tools/event_sink/event_sink.py` is a complete receiver for local testing.

## Metrics

`GET /metrics` returns Prometheus text, for Grafana, Prometheus, Zabbix, Datadog and similar tools: runs started,
finished and active, busy test systems, tests by verdict and source, codebeamer writes by kind and outcome, AI
analyses by outcome, and event deliveries.

It needs a key with `history:read`, unless `Bridge:MetricsPublic` is `true` (only for a monitoring server inside
your network). `GET /health` is always open and answers `{"status":"ok"}`.

## OpenAPI

`GET /api/openapi.json` — OpenAPI 3.1 description of every endpoint and of the event body. Import it into Postman,
n8n, Power Automate or a code generator.

The file is built from `tools/openapi/build_openapi.py`. After changing an endpoint, run the script and commit both:
the pipeline fails when they don't match.
