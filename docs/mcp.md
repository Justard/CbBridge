# MCP server — the bridge as tools for AI agents

The bridge offers its run history and runs as **MCP tools** (Model Context Protocol), the standard way AI assistants
connect to systems. An assistant can then answer questions like *"what broke since last night's run, and did those
tests pass on the previous firmware?"*. It can also start a run when you ask it to.

- Endpoint: `POST /mcp` (Streamable HTTP, stateless JSON — no session, no stream)
- Same keys and scopes as the [API](api.md#keys-and-scopes); the tool list shows only what the key may use
- Off unless `Api:Enabled` is `true`

## Tools

| Tool | Scope | What it does |
|---|---|---|
| `list_runs` | history:read | Runs, newest first; filter by text (name, ID, firmware/test configuration, user, label), source, verdict |
| `get_run` | history:read | One run with every test and its trend (NEW FAIL / FIXED) |
| `get_result` | history:read | One test: exact request, framework answer, AI analysis |
| `compare_with_last_pass` | history:read | When it last passed, on which test configuration, what changed in the answer |
| `test_history` | history:read | All executions of one test, pass rate, how often it flipped (flaky or regression?) |
| `find_new_failures` | history:read | Tests that passed in their previous run and fail now, across recent runs |
| `rate_ai_comment` | feedback:write | Mark an AI comment helpful or not |
| `audit_log` | audit:read | Who wrote what to codebeamer |
| `start_run` | runs:start | Run a category / Testablauf / test case / test run on a bench — needs a [codebeamer account on the client](api.md#starting-runs-from-an-agent-without-its-codebeamer-password) |
| `get_job` | runs:read | Progress of a started run |

Read-only tools are marked as such (`readOnlyHint`), so clients can run them without asking each time. `start_run`
executes real tests on hardware: its description tells the model to start only what the user asked for, and most
clients ask you before calling it.

## Connect a client

Give the client:

| | |
|---|---|
| URL | `https://bridge.example.com/mcp` |
| Transport | Streamable HTTP ("HTTP"; not SSE, not stdio) |
| Header | `Authorization: Bearer <the client's key>` |

How to enter these depends on the client. For Claude Code, see the MCP page of its documentation
(<https://docs.claude.com/en/docs/claude-code/mcp>). Many clients use a JSON entry like this:

```json
{
  "mcpServers": {
    "cb-test-bridge": {
      "type": "http",
      "url": "https://bridge.example.com/mcp",
      "headers": { "Authorization": "Bearer ${CBBRIDGE_KEY}" }
    }
  }
}
```

Use one key per person or agent, with only `history:read` unless it must start runs.

**Protocol version.** The bridge implements the `initialize` handshake (MCP 2024-11-05 to 2025-11-25). Clients that
first try the newer handshake-free protocol fall back to it automatically. This was checked with the official MCP
Python SDK, which `tools/mcp/mcp_smoke.py` uses.

## Try it

```bash
pip install mcp
python tools/mcp/mcp_smoke.py --url http://localhost:5000/mcp --key dev-agent-key
python tools/mcp/mcp_smoke.py --url http://localhost:5000/mcp --key dev-agent-key --start 88132   # also starts a run
```

## Security

- API key on every call (401 without, tools filtered by scope).
- Requests whose `Origin` header names another host are refused (403), so a web page in a browser can't reach a
  bridge on `localhost`.
- No codebeamer credentials ever pass through the model: reads use the bridge's history, and `start_run` uses the
  account configured for the client on the server.
- Runs started by an agent appear in the history as source **mcp**, labelled `MCP <client name>`, and their
  codebeamer writes are in the audit log.
