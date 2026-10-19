"""
Contract check for a test framework: does it speak docs/framework-contract.md the way the bridge expects?
Python 3.8+, no packages. Run it against your framework before connecting it to the bridge.

    python check_framework.py --url http://localhost:5001
    python check_framework.py --url http://bench-a:5001 --test my-quick-test.json --slow my-slow-test.json

--test   a quick test your framework can run (default: a one-step test with a harmless action)
--slow   a test that runs for 20 s or more — used to check the time limit, /cancel and /progress.
         For framework_stub: slow-test.example.json

Exit code: 0 = no failures (warnings allowed), 1 = at least one failure.
"""
import argparse
import json
import threading
import time
import urllib.error
import urllib.request

VERDICTS = {"PASSED", "FAILED", "BLOCKED", "TIMEOUT", "ABORTED", "ERROR"}
results = []


def report(level, name, detail=""):
    results.append(level)
    mark = {"ok": "  ok   ", "warn": "  warn ", "fail": "  FAIL "}[level]
    print(f"{mark} {name}" + (f" — {detail}" if detail else ""))


def http(method, url, body=None, timeout=60):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={"Content-Type": "application/json"})
    start = time.monotonic()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read().decode()
            return r.status, raw, time.monotonic() - start
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(), time.monotonic() - start


def parse(raw):
    try:
        return json.loads(raw)
    except ValueError:
        return None


def check_result(name, status, raw, sent, expect=None):
    body = parse(raw)
    if status != 200 or not isinstance(body, dict):
        report("fail", name, f"expected 200 with a JSON object, got HTTP {status}: {raw[:120]}")
        return None
    verdict = str(body.get("verdict", body.get("result", body.get("status", "")))).upper()
    if verdict not in VERDICTS:
        report("fail", name, f"verdict '{verdict}' is not one of {sorted(VERDICTS)}")
        return body
    if expect and verdict not in expect:
        report("fail", name, f"expected {'/'.join(expect)}, got {verdict}: {body.get('message')}")
        return body
    report("ok", name, f"{verdict}" + (f" · {body.get('message')}" if body.get("message") else ""))
    if str(body.get("itemId")) != str(sent["itemId"]):
        report("warn", f"{name}: itemId echoed", f"answer has itemId {body.get('itemId')!r}, sent {sent['itemId']!r}")
    if not isinstance(body.get("durationSec"), (int, float)):
        report("warn", f"{name}: durationSec", "missing — the bridge then measures the HTTP call instead")
    for a in body.get("attachments") or []:
        ok = isinstance(a, dict) and a.get("name") and a.get("contentBase64")
        report("ok" if ok else "fail", f"{name}: attachment {a.get('name') if isinstance(a, dict) else a!r}",
               "" if ok else "needs name and contentBase64")
    return body


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", required=True)
    ap.add_argument("--test", help="JSON file: a quick test")
    ap.add_argument("--slow", help="JSON file: a test that runs 20 s or more")
    a = ap.parse_args()
    base = a.url.rstrip("/")
    print(f"framework contract check · {base}\n")

    # 1. health + contract version
    try:
        status, raw, _ = http("GET", base + "/health", timeout=5)
    except OSError as e:
        report("fail", "reachable", str(e))
        return
    caps = []
    if 200 <= status < 300:
        h = parse(raw) or {}
        caps = h.get("capabilities") or []
        report("ok", "GET /health", f"contract {h.get('contract', '1.0 (not stated)')}" + (f" · {', '.join(caps)}" if caps else ""))
        if "contract" not in h:
            report("warn", "contract version", 'add "contract": "1.1" and "capabilities" to /health so the bridge knows what you support')
    elif status == 404:
        report("warn", "GET /health", "no /health route — optional, but recommended")
    else:
        report("fail", "GET /health", f"HTTP {status}")

    # 2. a quick test
    quick = json.load(open(a.test, encoding="utf-8")) if a.test else {
        "itemId": "contract-check-1", "name": "Contract check (quick)", "description": "bridge contract check",
        "testRunSteps": [["_NOP()", "PASSED", False]], "parameters": {"stepDelaySec": 0.1},
        "testConfiguration": "contract-check", "timeLimitSec": 30}
    quick.setdefault("timeLimitSec", 30)
    status, raw, took = http("POST", base + "/execute", quick, timeout=quick["timeLimitSec"] + 15)
    check_result("POST /execute (quick test)", status, raw, quick)

    # 3. bad requests
    status, raw, _ = http("POST", base + "/execute", {"name": "no itemId"})
    report("ok" if status == 400 else "warn", "POST /execute without itemId → 400", f"got {status}")
    status, raw, _ = http("POST", base + "/cancel/contract-check-not-running")
    report("ok" if status == 404 else "warn", "POST /cancel for nothing running → 404", f"got {status}")

    if not a.slow:
        report("warn", "time limit, /cancel, /progress", "not checked — give --slow with a test that runs 20 s or more")
        return
    slow = json.load(open(a.slow, encoding="utf-8"))

    # 4. time limit: the framework itself must stop the test and answer TIMEOUT
    limited = {**slow, "itemId": "contract-check-timeout", "timeLimitSec": 3}
    status, raw, took = http("POST", base + "/execute", limited, timeout=30)
    check_result("time limit (timeLimitSec=3) → TIMEOUT", status, raw, limited, expect={"TIMEOUT"})
    report("ok" if took < 3 + 10 else "fail", "answered soon after the limit", f"{took:.1f}s for a 3 s limit")

    # 5. /cancel and /progress while a test runs
    running = {**slow, "itemId": "contract-check-cancel", "timeLimitSec": 60}
    answer = {}
    t = threading.Thread(target=lambda: answer.update(zip(("status", "raw", "took"), http("POST", base + "/execute", running, timeout=90))))
    t.start()
    time.sleep(2.5)
    if "progress" in caps:
        status, raw, _ = http("GET", base + "/progress/contract-check-cancel")
        p = parse(raw) or {}
        report("ok" if status == 200 and "stepsDone" in p else "fail", "GET /progress/{itemId} while running", f"HTTP {status}: {raw[:100]}")
    else:
        report("warn", "GET /progress", "not advertised in /health capabilities (optional, gives live step progress)")
    status, raw, _ = http("POST", base + "/cancel/contract-check-cancel")
    report("ok" if status == 200 else "fail", "POST /cancel/{itemId} while running → 200", f"got {status}")
    t.join(timeout=30)
    if "status" in answer:
        check_result("cancelled test answers ABORTED", answer["status"], answer["raw"], running, expect={"ABORTED"})
        report("ok" if answer["took"] < 15 else "fail", "stopped quickly after /cancel", f"{answer['took']:.1f}s")
    else:
        report("fail", "cancelled test answers", "the /execute call did not return within 30 s after /cancel")


if __name__ == "__main__":
    try:
        main()
    finally:
        fails, warns = results.count("fail"), results.count("warn")
        print(f"\n{results.count('ok')} ok · {warns} warning(s) · {fails} failure(s)")
    raise SystemExit(1 if results.count("fail") else 0)
