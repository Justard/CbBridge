#!/usr/bin/env python3
"""
Start a test run on the CB Test Bridge from CI (Jenkins, GitLab, …), follow it, and write a JUnit report.
Python 3.8+, standard library only — runs on Windows and Linux agents.

Credentials come from the environment (Jenkins: withCredentials), never from the command line:
    BRIDGE_API_KEY            the bridge's Ci:ApiKey
    CB_USER + CB_PASSWORD     the CI agent's codebeamer account   (or CB_TOKEN for an API token)

Examples
    python bridge_ci.py --bridge https://bridge.example.com --category 4480 --release R5.0 --versions "FW 5.0.1,HW D1"
    python bridge_ci.py --bridge https://bridge.example.com --testablauf 88201 --framework-url http://bench-a:5001
    python bridge_ci.py --bridge https://bridge.example.com --test-case 10232          # a single test
    python bridge_ci.py --bridge https://bridge.example.com --test-run 88131           # an existing test run

Exit codes: 0 = all passed · 1 = some tests failed / timed out / not run · 2 = could not run (bad request,
credentials, bridge unreachable, job failed or aborted).
"""
import argparse
import base64
import json
import os
import signal
import sys
import time
import urllib.error
import urllib.request

EXIT_OK, EXIT_TESTS_FAILED, EXIT_ERROR = 0, 1, 2


def die(message, code=EXIT_ERROR):
    print(f"[bridge-ci] ERROR: {message}", file=sys.stderr)
    sys.exit(code)


class Bridge:
    def __init__(self, base_url, api_key, auth_header, insecure=False):
        self.base = base_url.rstrip("/")
        self.headers = {"X-Api-Key": api_key, "Authorization": auth_header, "Accept": "application/json"}
        self.context = None
        if insecure:  # only for self-signed test servers
            import ssl
            self.context = ssl._create_unverified_context()

    def call(self, method, path, body=None, accept_json=True):
        data = None if body is None else json.dumps(body).encode("utf-8")
        headers = dict(self.headers)
        if data is not None:
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(self.base + path, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=60, context=self.context) as response:
                text = response.read().decode("utf-8")
                return response.status, (json.loads(text) if accept_json and text else text)
        except urllib.error.HTTPError as e:
            text = e.read().decode("utf-8", "replace")
            try:
                return e.code, json.loads(text)
            except ValueError:
                return e.code, {"error": text or e.reason}
        except urllib.error.URLError as e:
            die(f"cannot reach the bridge at {self.base}: {e.reason}")


def auth_header_from_env():
    token = os.environ.get("CB_TOKEN")
    if token:
        return "Bearer " + token
    user, password = os.environ.get("CB_USER"), os.environ.get("CB_PASSWORD")
    if not user or password is None:
        die("set CB_USER and CB_PASSWORD (or CB_TOKEN) to the CI agent's codebeamer account")
    return "Basic " + base64.b64encode(f"{user}:{password}".encode("utf-8")).decode("ascii")


def split_list(value):
    return [v.strip() for v in value.split(",") if v.strip()] if value else None


def main():
    ap = argparse.ArgumentParser(description="Run codebeamer tests through the CB Test Bridge and write JUnit XML.")
    ap.add_argument("--bridge", default=os.environ.get("BRIDGE_URL"), help="bridge base URL (or BRIDGE_URL)")
    what = ap.add_mutually_exclusive_group(required=True)
    what.add_argument("--category", help="category with Testabläufe: run all their test cases")
    what.add_argument("--testablauf", help="one Testablauf (test run / test set): run its test cases")
    what.add_argument("--test-run", help="one existing test run")
    what.add_argument("--test-case", help="one test case (a single test)")
    ap.add_argument("--only", help="comma-separated test case IDs to keep (with --category / --testablauf)")
    ap.add_argument("--release", help="release name for this execution, e.g. R5.0")
    ap.add_argument("--versions", help="comma-separated version names, e.g. 'FW 5.0.1,HW D1'")
    ap.add_argument("--framework-url", help="test system address, e.g. http://bench-a:5001 (default: bridge setting)")
    ap.add_argument("--codebeamer-url", help="codebeamer address (default: bridge setting)")
    ap.add_argument("--no-new-runs", action="store_true", help="do not create a new codebeamer test run per test case")
    ap.add_argument("--no-post-back", action="store_true", help="do not write results to codebeamer")
    ap.add_argument("--time-limit", type=int, help="seconds per test")
    ap.add_argument("--overall-limit", type=int, help="seconds for the whole run")
    ap.add_argument("--label", default=os.environ.get("BUILD_TAG"), help="shown in the bridge log (default: Jenkins BUILD_TAG)")
    ap.add_argument("--junit", default="junit.xml", help="where to write the JUnit report")
    ap.add_argument("--poll", type=float, default=3.0, help="seconds between status checks")
    ap.add_argument("--max-wait", type=int, help="give up (and abort the job) after this many seconds")
    ap.add_argument("--insecure", action="store_true", help="accept a self-signed HTTPS certificate on the bridge")
    args = ap.parse_args()

    if not args.bridge:
        die("give --bridge or set BRIDGE_URL")
    api_key = os.environ.get("BRIDGE_API_KEY")
    if not api_key:
        die("set BRIDGE_API_KEY to the bridge's Ci:ApiKey")

    bridge = Bridge(args.bridge, api_key, auth_header_from_env(), args.insecure)
    request = {
        "category": args.category, "testablauf": args.testablauf, "testRun": args.test_run, "testCase": args.test_case,
        "onlyTestCases": split_list(args.only), "release": args.release, "versions": split_list(args.versions),
        "frameworkUrl": args.framework_url, "codebeamerUrl": args.codebeamer_url,
        "createNewRuns": not args.no_new_runs, "postBack": not args.no_post_back,
        "timeLimitSec": args.time_limit, "overallLimitSec": args.overall_limit, "label": args.label,
    }
    request = {k: v for k, v in request.items() if v is not None}

    status, started = bridge.call("POST", "/api/ci/runs", request)
    if status != 202:
        die(f"bridge refused the run (HTTP {status}): {started.get('error', started)}")
    job_id = started["id"]
    print(f"[bridge-ci] job {job_id} started · status: {started['statusUrl']}")

    # Jenkins "Abort" sends SIGTERM: stop the job on the bridge too, so the test bench is freed
    def stop(*_):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, stop)

    deadline = time.time() + args.max_wait if args.max_wait else None
    log_from, job = 0, None
    try:
        while True:
            status, job = bridge.call("GET", f"/api/ci/runs/{job_id}?logFrom={log_from}")
            if status != 200:
                die(f"status check failed (HTTP {status}): {job}")
            for line in job["log"]:
                print("  " + line, flush=True)
            log_from += len(job["log"])
            if job["state"] in ("Finished", "Failed", "Aborted"):
                break
            if deadline and time.time() > deadline:
                print(f"[bridge-ci] --max-wait {args.max_wait}s reached, aborting job", file=sys.stderr)
                raise KeyboardInterrupt
            time.sleep(args.poll)
    except KeyboardInterrupt:
        bridge.call("POST", f"/api/ci/runs/{job_id}/abort")
        print("[bridge-ci] abort sent to the bridge", file=sys.stderr)
        time.sleep(1)
        _, job = bridge.call("GET", f"/api/ci/runs/{job_id}")

    status, xml = bridge.call("GET", f"/api/ci/runs/{job_id}/junit", accept_json=False)
    if status == 200:
        with open(args.junit, "w", encoding="utf-8") as f:
            f.write(xml)
        print(f"[bridge-ci] JUnit report: {args.junit}")

    s = job["summary"]
    print(f"[bridge-ci] {job['state']}: {s['passed']}/{s['total']} passed · failed {s['failed']} · timeout {s['timeout']}"
          f" · error {s['error']} · not run {s['notRun']}")
    for r in job["results"]:
        if r["verdict"] != "PASSED":
            print(f"  {r['verdict']:8} #{r['itemId']} {r['name']}  {r['message']}")

    if job["state"] != "Finished" or s["total"] == 0 or s["error"] > 0:
        sys.exit(EXIT_ERROR if job["state"] != "Finished" or s["total"] == 0 else EXIT_TESTS_FAILED)
    sys.exit(EXIT_OK if s["allPassed"] else EXIT_TESTS_FAILED)


if __name__ == "__main__":
    main()
