"""
Builds src/CbBridge.Web/wwwroot/api/openapi.json (served at /api/openapi.json).
The API is hand-written in C#; this file describes it. Change both together.
    python tools/openapi/build_openapi.py
"""
import json
import pathlib

S = lambda t, **k: {"type": t, **k}
N = lambda t, **k: {"type": [t, "null"], **k}
ref = lambda n: {"$ref": f"#/components/schemas/{n}"}
nref = lambda n: {"anyOf": [ref(n), {"type": "null"}]}
VERDICT = {"type": "string", "enum": ["PASSED", "FAILED", "BLOCKED", "TIMEOUT", "ABORTED", "ERROR", "NOTRUN", "QUEUED", "RUNNING"]}

schemas = {
    "Problem": S("object", properties={"status": S("integer"), "title": S("string"), "detail": S("string")}),
    "RunSummary": S("object", required=["id", "source", "startedAt", "summary", "link"], properties={
        "id": S("string", examples=["20261019-071558-4b2cc5"]),
        "source": S("string", enum=["console", "ci"]),
        "startedBy": S("string"), "label": N("string"), "selection": S("string"), "frameworkUrl": S("string"),
        "startedAt": S("string", format="date-time"), "finishedAt": N("string", format="date-time"),
        "summary": S("object", properties={k: S("integer") for k in ["total", "passed", "failed", "blocked", "timeout", "error", "notRun"]}),
        "link": S("string", format="uri", description="Page in the bridge UI")}),
    "Analysis": S("object", properties={
        "state": S("string", enum=["Pending", "Done", "NoAnswer", "Failed"]), "comment": N("string"),
        "commentUpload": N("string"), "feedback": N("integer", enum=[1, -1, None]), "feedbackBy": N("string"),
        "agentAnswer": {"description": "Full answer of the AI agent (detail view only)"}}),
    "Result": S("object", required=["runId", "position", "testKey", "name", "verdict"], properties={
        "runId": S("string"), "position": S("integer", description="1-based place in the run"),
        "testKey": S("string", description="'tc:<id>' for test cases, 'item:<id>' for test runs, 'local:<id>' for My tests", examples=["tc:10232"]),
        "itemId": S("string"), "testCaseId": N("string"), "testablauf": N("string"), "name": S("string"),
        "isLocal": S("boolean"), "testConfiguration": N("string"), "verdict": VERDICT, "durationSec": S("number"),
        "message": S("string"), "codebeamerTestRunId": N("string"), "upload": S("string", enum=["NotPosted", "Posted", "DryRun", "Failed"]),
        "savedAt": S("string", format="date-time"), "analysis": nref("Analysis"),
        "previousVerdict": {**VERDICT, "description": "Same test in the run before (lists only)"},
        "trend": N("string", enum=["NEW FAIL", "FIXED", None]),
        "request": {"description": "Exact JSON sent to the framework (detail only)"},
        "response": {"description": "Framework's full answer (detail only)"},
        "uploadMessage": N("string"), "link": S("string", format="uri")}),
    "RunDetail": {"allOf": [ref("RunSummary"), S("object", properties={"results": S("array", items=ref("Result"))})]},
    "HistoryPoint": S("object", properties={
        "runId": S("string"), "position": S("integer"), "at": S("string", format="date-time"), "verdict": VERDICT,
        "durationSec": S("number"), "testConfiguration": N("string"), "source": S("string"), "label": N("string"),
        "aiComment": N("string"), "link": S("string", format="uri")}),
    "AuditEntry": S("object", properties={
        "at": S("string", format="date-time"), "actor": S("string"), "action": S("string", examples=["post result FAILED"]),
        "target": S("string", examples=["item:88132"]), "outcome": S("string", enum=["Posted", "DryRun", "Failed", "NotPosted"]),
        "detail": N("string")}),
    "CiRunRequest": S("object", description="Exactly one of category, testablauf, testRun, testCase (ID or codebeamer link).", properties={
        "category": S("string"), "testablauf": S("string"), "testRun": S("string"), "testCase": S("string"),
        "onlyTestCases": S("array", items=S("string")), "release": S("string"), "versions": S("array", items=S("string")),
        "codebeamerUrl": S("string"), "frameworkUrl": S("string"), "createNewRuns": S("boolean", default=True),
        "postBack": S("boolean", default=True), "analyze": S("boolean", default=False),
        "timeLimitSec": S("integer"), "overallLimitSec": S("integer"), "label": S("string")}),
    "CiJob": S("object", properties={
        "id": S("string"), "state": S("string", enum=["Queued", "Running", "Finished", "Failed", "Aborted"]),
        "kind": S("string"), "target": S("string"), "frameworkUrl": S("string"), "label": N("string"),
        "runId": N("string", description="ID in the history (/api/v1/runs/{runId})"),
        "summary": S("object"), "results": S("array", items=S("object")), "logFrom": S("integer"), "log": S("array", items=S("string")),
        "error": N("string")}),
    "Event": S("object", required=["event", "at", "run"], properties={
        "event": S("string", enum=["run.started", "run.finished", "test.failed", "analysis.done"]),
        "at": S("string", format="date-time"),
        "run": S("object", properties={k: S("string") for k in ["id", "source", "startedBy", "selection", "label", "frameworkUrl", "link"]}),
        "tests": S("integer", description="run.started"), "summary": S("object", description="run.finished"),
        "problems": S("array", description="run.finished: failed tests (max 50)", items=S("object")),
        "test": S("object", description="test.failed, analysis.done"), "analysis": S("object", description="analysis.done")}),
}

err = lambda *codes: {str(c): {"description": d, "content": {"application/problem+json": {"schema": ref("Problem")}}}
                      for c, d in [(401, "Unknown or missing API key"), (403, "Key lacks the scope"),
                                   (404, "Not found, or this API is disabled"), (503, "Run history not available")] if c in codes}
ok = lambda schema, desc="OK": {"200": {"description": desc, "content": {"application/json": {"schema": schema}}}}
p_run = {"name": "runId", "in": "path", "required": True, "schema": S("string")}
p_pos = {"name": "position", "in": "path", "required": True, "schema": S("integer")}
q = lambda name, schema, desc="": {"name": name, "in": "query", "schema": schema, "description": desc}
v1 = [{"apiKey": []}, {"bearer": []}]

def op(tag, summary, scope, sec=v1, **k):
    return {"tags": [tag], "summary": summary, "description": f"Scope: `{scope}`." + (("\n\n" + k.pop("desc")) if "desc" in k else ""),
            "security": sec, **k}

paths = {
    "/health": {"get": {"tags": ["Bridge"], "summary": "Liveness (no key)", "security": [],
                        "responses": ok(S("object", properties={"status": S("string", enum=["ok"])}))}},
    "/api/v1/status": {"get": op("Bridge", "Who am I, what is switched on", "history:read",
                                 responses={**ok(S("object")), **err(401, 403, 404)})},
    "/api/v1/runs": {"get": op("History", "List runs, newest first", "history:read",
        parameters=[q("text", S("string"), "Search in names, IDs, test configuration, label, user, bench"),
                    q("source", S("string", enum=["console", "ci"])), q("verdict", VERDICT, "Runs containing this verdict"),
                    q("from", S("string", format="date-time")), q("to", S("string", format="date-time")),
                    q("limit", S("integer", default=50, maximum=500)), q("offset", S("integer", default=0))],
        responses={**ok(S("object", properties={"total": S("integer"), "offset": S("integer"), "runs": S("array", items=ref("RunSummary"))})), **err(401, 403, 404, 503)})},
    "/api/v1/runs/{runId}": {"get": op("History", "One run with all its results (and each test's previous verdict)", "history:read",
        parameters=[p_run], responses={**ok(ref("RunDetail")), **err(401, 403, 404, 503)})},
    "/api/v1/runs/{runId}/results/{position}": {"get": op("History", "One result with request, framework answer and AI analysis", "history:read",
        parameters=[p_run, p_pos], responses={**ok(ref("Result")), **err(401, 403, 404, 503)})},
    "/api/v1/runs/{runId}/results/{position}/compare": {"get": op("History", "Compare with the previous run and the last passing run", "history:read",
        parameters=[p_run, p_pos], responses={**ok(S("object", properties={
            "result": ref("Result"), "previous": nref("Result"), "lastPassing": nref("Result"),
            "responseChangesSinceLastPass": S("array", items=S("string"), description="Changed lines of the framework answer, '+ ' now / '- ' then")})),
            **err(401, 403, 404, 503)})},
    "/api/v1/runs/{runId}/results/{position}/feedback": {"post": op("History", "Rate the AI comment", "feedback:write",
        parameters=[p_run, p_pos],
        requestBody={"required": True, "content": {"application/json": {"schema": S("object", properties={
            "helpful": N("boolean", description="true / false, null clears"), "note": N("string")})}}},
        responses={**ok(S("object")), **err(401, 403, 404, 503)})},
    "/api/v1/tests/{testKey}/history": {"get": op("History", "All executions of one test, newest first", "history:read",
        parameters=[{"name": "testKey", "in": "path", "required": True, "schema": S("string"), "description": "'tc:10232', 'item:88131', or a bare ID"},
                    q("limit", S("integer", default=50))],
        responses={**ok(S("object", properties={"testKey": S("string"), "count": S("integer"), "passRate": N("number"),
                                                "history": S("array", items=ref("HistoryPoint"))})), **err(401, 403, 404, 503)})},
    "/api/v1/audit": {"get": op("Audit", "Who wrote what to codebeamer", "audit:read",
        parameters=[q("limit", S("integer", default=200)), q("target", S("string"), "e.g. item:88132")],
        responses={**ok(S("object", properties={"entries": S("array", items=ref("AuditEntry"))})), **err(401, 403, 404, 503)})},
    "/api/ci/runs": {
        "post": op("CI", "Start a run (Jenkins / any CI)", "runs:start", sec=[{"apiKey": [], "codebeamer": []}],
            desc="`Authorization` carries the CI agent's own codebeamer login (Basic or Bearer); the API key goes in `X-Api-Key`.",
            requestBody={"required": True, "content": {"application/json": {"schema": ref("CiRunRequest")}}},
            responses={"202": {"description": "Accepted", "content": {"application/json": {"schema": S("object", properties={
                "id": S("string"), "state": S("string"), "statusUrl": S("string"), "junitUrl": S("string")})}}},
                "400": {"description": "Bad request"}, **err(401, 403, 404)}),
        "get": op("CI", "Latest 50 jobs", "runs:read", sec=[{"apiKey": []}], responses={**ok(S("array", items=S("object"))), **err(401, 403, 404)})},
    "/api/ci/runs/{id}": {"get": op("CI", "Job status, results, log", "runs:read", sec=[{"apiKey": []}],
        parameters=[{"name": "id", "in": "path", "required": True, "schema": S("string")}, q("logFrom", S("integer"), "Only log lines from here on")],
        responses={**ok(ref("CiJob")), **err(401, 403, 404)})},
    "/api/ci/runs/{id}/junit": {"get": op("CI", "JUnit XML", "runs:read", sec=[{"apiKey": []}],
        parameters=[{"name": "id", "in": "path", "required": True, "schema": S("string")}],
        responses={"200": {"description": "JUnit", "content": {"application/xml": {"schema": S("string")}}}, **err(401, 403, 404)})},
    "/api/ci/runs/{id}/abort": {"post": op("CI", "Stop a job and the running test", "runs:start", sec=[{"apiKey": []}],
        parameters=[{"name": "id", "in": "path", "required": True, "schema": S("string")}], responses={**ok(S("object")), **err(401, 403, 404)})},
    "/api/analysis/callback/{analysisId}": {"post": {"tags": ["AI analysis"], "summary": "AI agent sends its comment (asynchronous answer)",
        "description": "Single-use; token from the analysis request in `X-Callback-Token`. See docs/ai-analysis.md.", "security": [],
        "parameters": [{"name": "analysisId", "in": "path", "required": True, "schema": S("string")},
                       {"name": "X-Callback-Token", "in": "header", "required": True, "schema": S("string")}],
        "requestBody": {"required": True, "content": {"application/json": {"schema": S("object", properties={"comment": S("string")})}}},
        "responses": {**ok(S("object")), "404": {"description": "Unknown/expired ID or wrong token"}}}},
}

doc = {
    "openapi": "3.1.0",
    "info": {"title": "CB Test Bridge API", "version": "1.0.0",
             "description": "Read the run history, start CI runs, rate AI comments. Keys and scopes: docs/api.md. "
                            "The same functions are available to AI agents as MCP tools at /mcp."},
    "servers": [{"url": "/"}],
    "tags": [{"name": n} for n in ["Bridge", "History", "Audit", "CI", "AI analysis"]],
    "paths": paths,
    "webhooks": {"bridgeEvent": {"post": {"summary": "Event sent to Events:Subscribers",
        "description": "Headers: X-Bridge-Event (name), X-Bridge-Signature: sha256=<hex HMAC of the body with the subscriber's Secret>. "
                       "With Format=text the body is {\"text\": \"…\"} instead.",
        "requestBody": {"content": {"application/json": {"schema": ref("Event")}}},
        "responses": {"200": {"description": "Any 2xx = delivered (3 attempts otherwise)"}}}}},
    "components": {
        "schemas": schemas,
        "securitySchemes": {
            "apiKey": {"type": "apiKey", "in": "header", "name": "X-Api-Key"},
            "bearer": {"type": "http", "scheme": "bearer", "description": "Same key as X-Api-Key (not for /api/ci)"},
            "codebeamer": {"type": "http", "scheme": "basic", "description": "CI agent's codebeamer account (/api/ci/runs only)"}}},
}

out = pathlib.Path(__file__).resolve().parents[2] / "src/CbBridge.Web/wwwroot/api/openapi.json"
out.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("wrote", out)
