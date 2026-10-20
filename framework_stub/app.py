"""
Flask entry point — mirrors your framework's REST API.
    POST /execute            one test run JSON in, result JSON out (always 200 once it ran)
    POST /cancel/<itemId>    stop a running test run
    GET  /progress/<itemId>  live step progress of a running test run (contract 1.1, optional)
    GET  /health             status + contract version + capabilities
"""
from flask import Flask, jsonify, request

import runner

app = Flask(__name__)
REQUIRED = ("itemId", "name")


CONTRACT = "1.1"


@app.get("/health")
def health():
    # contract: which version of docs/framework-contract.md this framework implements;
    # capabilities: optional parts it supports, so the bridge only uses what is there.
    return jsonify(status="ok", contract=CONTRACT, framework="framework_stub 1.1",
                   capabilities=["cancel", "progress", "attachments"])


@app.get("/progress/<item_id>")
def progress(item_id):
    p = runner.progress(item_id)
    return (jsonify(p), 200) if p else (jsonify(running=False), 404)


@app.post("/execute")
def execute():
    testrun = request.get_json(force=True, silent=True)
    if not isinstance(testrun, dict):
        return jsonify(verdict="ERROR", message="body must be a JSON object"), 400
    missing = [k for k in REQUIRED if not testrun.get(k)]
    if missing:
        return jsonify(verdict="ERROR", message=f"missing field(s): {', '.join(missing)}"), 400
    if runner.is_running(testrun["itemId"]):
        return jsonify(verdict="ERROR", message="this test run is already running"), 409
    app.logger.info("execute #%s %s (timeLimitSec=%s)", testrun["itemId"], testrun["name"], testrun.get("timeLimitSec"))
    return jsonify(runner.run_with_limit(testrun))


@app.post("/cancel/<item_id>")
def cancel(item_id):
    ok = runner.cancel(item_id)
    return jsonify(cancelled=ok), (200 if ok else 404)


if __name__ == "__main__":  # guard is required on Windows (child processes re-import this file)
    import argparse
    ap = argparse.ArgumentParser(description="Framework stand-in (Flask)")
    ap.add_argument("--port", type=int, default=5001, help="listening port (default 5001)")
    ap.add_argument("--host", default="127.0.0.1", help="use 0.0.0.0 to accept calls from other PCs")
    args = ap.parse_args()
    app.run(host=args.host, port=args.port, threaded=True)
