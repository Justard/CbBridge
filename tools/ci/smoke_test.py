"""
End-to-end check of a running bridge in Development mode against the local stand-ins:
fake codebeamer (5002), framework stub (5001), agent stub (5003), event sink (5004), bridge (5000).
Used by the GitHub Actions pipeline; also handy after a change:

    python tools/ci/smoke_test.py            exit code 0 = everything worked
"""
import json
import sys
import time
import urllib.error
import urllib.request

BRIDGE = "http://localhost:5000"
CB = "http://localhost:5002/cb"
SINK = "http://localhost:5004"
JENKINS = {"X-Api-Key": "dev-jenkins-key"}
AGENT = {"X-Api-Key": "dev-agent-key"}
BASIC = {"Authorization": "Basic amRvZTpwdw=="}  # jdoe:pw — the fake codebeamer accepts anything but "wrong"
failures = []


def call(method, url, headers=None, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={"Content-Type": "application/json", **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            raw = r.read()
            return r.status, json.loads(raw) if raw and r.headers.get_content_type().endswith("json") else raw.decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


def check(name, condition, detail=""):
    print(("  ok    " if condition else "  FAIL  ") + name + (f" — {detail}" if detail and not condition else ""))
    if not condition:
        failures.append(name)


def wait_job(job_id, seconds=90):
    for _ in range(seconds):
        status, job = call("GET", f"{BRIDGE}/api/ci/runs/{job_id}", JENKINS)
        if status == 200 and job["state"] in ("Finished", "Failed", "Aborted"):
            return job
        time.sleep(1)
    return None


def main():
    print("bridge smoke test")
    for _ in range(60):
        try:
            if call("GET", f"{BRIDGE}/health")[0] == 200:
                break
        except OSError:
            pass
        time.sleep(1)
    check("health", call("GET", f"{BRIDGE}/health")[0] == 200)
    check("openapi served", call("GET", f"{BRIDGE}/api/openapi.json")[0] == 200)

    # auth and scopes
    check("v1 without key → 401", call("GET", f"{BRIDGE}/api/v1/runs")[0] == 401)
    check("jenkins key cannot read history → 403", call("GET", f"{BRIDGE}/api/v1/runs", JENKINS)[0] == 403)
    check("CI start without codebeamer login → 401", call("POST", f"{BRIDGE}/api/ci/runs", JENKINS, {"testRun": "88131"})[0] == 401)

    # a passing and a failing CI run, with AI analysis
    status, started = call("POST", f"{BRIDGE}/api/ci/runs", {**JENKINS, **BASIC}, {"testRun": "88131", "timeLimitSec": 30})
    check("start passing run → 202", status == 202, str(started))
    passing = wait_job(started["id"]) if status == 202 else None
    status, started = call("POST", f"{BRIDGE}/api/ci/runs", {**JENKINS, **BASIC}, {"testRun": "88132", "analyze": True, "timeLimitSec": 30})
    check("start failing run → 202", status == 202, str(started))
    job = wait_job(started["id"]) if status == 202 else None
    check("failing run finished", job is not None and job["state"] == "Finished", str(job and job["state"]))
    if not job:
        return
    result = job["results"][0]
    check("verdict FAILED", result["verdict"] == "FAILED")
    check("AI comment arrived", bool(result.get("analysisComment")), str(result))
    status, junit = call("GET", f"{BRIDGE}/api/ci/runs/{job['id']}/junit", JENKINS)
    check("JUnit has the failure and the AI comment", status == 200 and 'failures="1"' in junit and "AI:" in junit)

    # history API
    run_id = job["runId"]
    status, run = call("GET", f"{BRIDGE}/api/v1/runs/{run_id}", AGENT)
    check("run in history", status == 200 and run["summary"]["failed"] == 1, str(run)[:200])
    status, detail = call("GET", f"{BRIDGE}/api/v1/runs/{run_id}/results/1", AGENT)
    check("result has request and framework answer", status == 200 and detail["request"] and detail["response"])
    status, cmp = call("GET", f"{BRIDGE}/api/v1/runs/{run_id}/results/1/compare", AGENT)
    check("compare answers", status == 200 and "responseChangesSinceLastPass" in cmp)
    status, hist = call("GET", f"{BRIDGE}/api/v1/tests/item:88132/history", AGENT)
    check("test history", status == 200 and hist["count"] >= 1)
    status, _ = call("POST", f"{BRIDGE}/api/v1/runs/{run_id}/results/1/feedback", AGENT, {"helpful": True})
    check("feedback saved", status == 200)
    status, audit = call("GET", f"{BRIDGE}/api/v1/audit?target=item:88132", AGENT)
    check("audit has the result upload and the comment", status == 200 and
          {"post result FAILED", "add AI comment"} <= {e["action"] for e in audit["entries"]}, str(audit)[:300])

    # what reached codebeamer
    _, comments = call("GET", f"{CB}/_comments")
    check("comment posted to codebeamer", any(c["itemId"] == "88132" for c in comments))

    # events
    time.sleep(2)
    _, events = call("GET", f"{SINK}/_events")
    names = [e["event"] for e in events]
    check("events: run.started, test.failed, run.finished, analysis.done",
          {"run.started", "test.failed", "run.finished", "analysis.done"} <= set(names), str(names))
    check("event signatures valid", all(e["signatureValid"] in (True, None) for e in events) and any(e["signatureValid"] for e in events))
    check("chat message for failed run", any(e["event"] == "run.finished" and (e["body"] or {}).get("text", "").startswith("❌") for e in events))

    # MCP with the official SDK (optional dependency)
    try:
        import asyncio
        import httpx2
        from mcp import Client
        from mcp.client.streamable_http import streamable_http_client

        async def mcp_check():
            http = httpx2.AsyncClient(headers={"Authorization": "Bearer dev-agent-key"}, timeout=60)
            async with Client(streamable_http_client(f"{BRIDGE}/mcp", http_client=http)) as client:
                tools = {t.name for t in (await client.list_tools()).tools}
                found = json.loads((await client.call_tool("find_new_failures", {"runs": 20})).content[0].text)
                run_view = json.loads((await client.call_tool("get_run", {"run_id": run_id})).content[0].text)
                return tools, found, run_view

        tools, found, run_view = asyncio.run(mcp_check())
        check("MCP tools listed", {"list_runs", "get_run", "compare_with_last_pass", "start_run"} <= tools, str(tools))
        check("MCP get_run", run_view["id"] == run_id)
        check("MCP find_new_failures answers", "newFailures" in found)
    except ImportError:
        print("  skip  MCP (pip install mcp)")

    # bench side: sentences in codebeamer steps -> functions on the (fake) bench
    status, steps = call("GET", "http://localhost:5001/steps")
    check("framework serves its step catalogue", status == 200 and len(steps["steps"]) >= 10, str(steps)[:200])
    for item, expected in (("88134", "PASSED"), ("88135", "FAILED")):
        status, started = call("POST", f"{BRIDGE}/api/ci/runs", {**JENKINS, **BASIC}, {"testRun": item, "timeLimitSec": 30})
        job = wait_job(started["id"]) if status == 202 else None
        verdict = job and job["results"][0]["verdict"]
        check(f"bench sentences #{item} → {expected}", verdict == expected, str(job)[:300])
        if job:
            _, detail = call("GET", f"{BRIDGE}/api/v1/runs/{job['runId']}/results/1", AGENT)
            response = detail.get("response") if isinstance(detail, dict) else None
            if isinstance(response, str):
                response = json.loads(response)
            check(f"bench #{item}: measurements and safe state in the answer",
                  bool(response and response.get("measurements")) and response["bench"]["safeState"] == "reached",
                  str(response)[:300])

    print(f"\n{'all passed' if not failures else f'{len(failures)} failed: ' + ', '.join(failures)}")


if __name__ == "__main__":
    main()
    sys.exit(1 if failures else 0)
