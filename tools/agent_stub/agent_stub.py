"""
STAND-IN for the AI analysis agent — for local testing of the bridge only.
The real agent (n8n workflow, Python service with an LLM, …) lives in its own repository and
implements the same contract: docs/ai-analysis.md ("cbbridge.analysis/v1").

This stub does no AI: it builds a comment from the failed steps so you can see the whole flow.

    python tools/agent_stub/agent_stub.py            # port 5003, answers directly (synchronous)
    AGENT_MODE=async python tools/agent_stub/agent_stub.py   # answers 202, calls back after AGENT_DELAY seconds

Bridge settings (appsettings.Development.json or environment):
    Analysis__Enabled=true
    Analysis__WebhookUrl=http://localhost:5003/analyze
    Analysis__CallbackBaseUrl=http://localhost:5000      # only for async mode
"""
import json
import os
import threading
import time
import urllib.request

from flask import Flask, jsonify, request

app = Flask(__name__)
MODE = os.environ.get("AGENT_MODE", "sync")
DELAY = float(os.environ.get("AGENT_DELAY", "3"))
RECEIVED = []


def comment_for(req):
    test, result = req.get("test", {}), req.get("result", {})
    framework = result.get("framework") or {}
    failed = [s for s in framework.get("steps", []) if isinstance(s, dict) and s.get("verdict") == "FAILED"]
    if failed:
        s = failed[0]
        where = f"step {s.get('index')} ({s.get('action', '?')})"
        hint = f"expected {s.get('expected', '?')}, got {s.get('actual', s.get('verdict'))}"
    else:
        where, hint = "no step detail in the framework answer", result.get("message") or "see framework log"
    if result.get("verdict") == "TIMEOUT":
        return f"[stub] Timed out after {result.get('durationSec')}s — check whether the bench hangs at {where}."
    return (f"[stub] {test.get('name')} failed at {where}: {hint}. "
            f"Config {test.get('testConfiguration') or '—'}. Start with the code behind that step.")


def call_back(req):
    time.sleep(DELAY)
    cb = req.get("callback") or {}
    body = json.dumps({"comment": comment_for(req), "likelyArea": "stub", "confidence": 0.1}).encode()
    r = urllib.request.Request(cb["url"], data=body, method="POST",
                               headers={"Content-Type": "application/json", cb.get("header", "X-Callback-Token"): cb["token"]})
    try:
        with urllib.request.urlopen(r, timeout=10) as resp:
            print("callback →", resp.status)
    except Exception as ex:  # noqa: BLE001 — a stub just reports
        print("callback failed:", ex)


@app.post("/analyze")
def analyze():
    req = request.get_json(force=True)
    RECEIVED.append(req)
    if MODE == "async" and req.get("callback"):
        threading.Thread(target=call_back, args=(req,), daemon=True).start()
        return jsonify(accepted=True), 202
    return jsonify(comment=comment_for(req), likelyArea="stub", confidence=0.1)


@app.get("/_received")
def received():
    return jsonify(RECEIVED)


if __name__ == "__main__":
    app.run(host="127.0.0.1", port=int(os.environ.get("AGENT_PORT", "5003")))
