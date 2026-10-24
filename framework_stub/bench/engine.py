"""
Runs ONE codebeamer test run on the bench, sentence by sentence.

How a codebeamer step [Action, Expected result, Critical?] is read:
  Action           every line is executed:   "When the ignition is switched on"
                   (keyword optional here; several lines = several sentences, run in order)
  Expected result  lines starting with Then/And/But/Dann/Und/Aber or _KEYWORD are executed as CHECKS;
                   any other text is a plain description and is only shown
  Critical?        a FAILED critical step ends the test; others continue

Order of a test:
  1. plan     resolve EVERY line first. One unknown sentence or bad value -> BLOCKED, no device touched.
  2. run      step by step; each step reports PASSED / FAILED / ERROR with what was actually seen
  3. close    always: cleanups of the steps, safe state of the bench, close devices (see context.py)

Verdicts: FAILED = a check did not hold. ERROR = the bench itself had a problem (device error, crash),
the test stops there. ABORTED = the bridge asked to stop; the result comes back with the steps done so far.
"""
import base64
import re
import time
import traceback

from bench.context import Context, StepFailed, StopRequested
from bench.registry import Undefined
from bench.units import ParamError

GHERKIN = re.compile(r"^(given|when|then|and|but|angenommen|gegeben sei|wenn|dann|und|aber)\b\s*", re.I)
CHECK_WORDS = {"then", "and", "but", "dann", "und", "aber"}


def split_keyword(line):
    """'When the x' -> ('when', 'the x'); 'the x' -> (None, 'the x')."""
    m = GHERKIN.match(line.strip())
    if not m:
        return None, line.strip()
    return m.group(1).lower(), line.strip()[m.end():]


def _lines(cell):
    if cell is None:
        return []
    return [l.strip() for l in str(cell).splitlines() if l.strip() and not l.strip().startswith("#")]


def _is_executable(line):
    kw, _ = split_keyword(line)
    return kw is not None or line.startswith("_")


def read_step(step):
    """codebeamer step = positional array [Action, Expected result, Critical?]; dicts accepted too."""
    if isinstance(step, list):
        cells = step + [None] * (3 - len(step))
        return cells[0], cells[1], bool(cells[2])
    if isinstance(step, dict):
        return (step.get("action") or step.get("Action"),
                step.get("expected") or step.get("Expected result"),
                bool(step.get("critical") or step.get("Critical?")))
    return str(step), None, False


def is_bench_test(testrun):
    """True when any step is written as a sentence (Given/When/Then...) or as _KEYWORD(...)."""
    steps = testrun.get("testRunSteps")
    if not isinstance(steps, list):
        return False
    for step in steps:
        action, expected, _ = read_step(step)
        if any(_is_executable(l) for l in _lines(action) + _lines(expected)):
            return True
    return False


def plan(testrun, catalogue, params):
    """Resolve every line. Returns (planned steps, problems). Touches no device."""
    planned, problems = [], []
    for i, step in enumerate(testrun.get("testRunSteps") or [], start=1):
        action, expected, critical = read_step(step)
        calls, checks = [], []
        for line in _lines(action):
            _resolve(catalogue, line, params, calls, problems, i)
        for line in _lines(expected):
            kw, _ = split_keyword(line)
            if kw in CHECK_WORDS or line.startswith("_"):
                _resolve(catalogue, line, params, checks, problems, i)
            elif kw is not None:
                problems.append(f"step {i}: expected result '{line}' starts with '{kw}' — use Then/And there")
        planned.append({"index": i, "action": action, "expected": expected, "critical": critical,
                        "calls": calls, "checks": checks})
    return planned, problems


def _resolve(catalogue, line, params, into, problems, index):
    _, text = split_keyword(line)
    try:
        into.append(catalogue.resolve(text, params))
    except (Undefined, ParamError) as ex:
        problems.append(f"step {index}: {ex}")


def run_test(testrun, catalogue, bench, report_step=lambda s: None, stop_event=None):
    ctx = Context(testrun, bench, stop_event)
    started = time.monotonic()
    planned, problems = plan(testrun, catalogue, ctx.params)

    if problems:
        return _result(testrun, ctx, "BLOCKED", "not executable, no device was touched: " + "; ".join(problems),
                       [], started, problems=problems)

    results, verdict, message = [], "PASSED", ""
    try:
        for p in planned:
            try:
                res = _run_step(ctx, p)
            except StopRequested:
                res = {"index": p["index"], "action": p["action"], "expected": p["expected"],
                       "critical": p["critical"], "actual": "stopped during this step", "verdict": "ABORTED"}
                results.append(res)
                report_step(res)
                raise
            results.append(res)
            report_step(res)
            if res["verdict"] == "ERROR":
                verdict, message = "ERROR", f"step {p['index']}: {res['actual']}"
                break
            if res["verdict"] == "FAILED":
                verdict = "FAILED"
                if p["critical"]:
                    break
    except StopRequested:
        verdict, message = "ABORTED", "stopped by the bridge (time limit or cancel)"
    finally:
        report = ctx.close()

    if verdict == "FAILED":
        message = "failed step(s): " + ", ".join(str(r["index"]) for r in results if r["verdict"] == "FAILED")
    if report["errors"]:
        message = (message + "; " if message else "") + "clean-up problems: " + "; ".join(report["errors"])
        if verdict == "PASSED":
            verdict = "ERROR"      # the test passed, but the bench may not be safe for the next one
    return _result(testrun, ctx, verdict, message, results, started)


def _run_step(ctx, p):
    t0 = time.monotonic()
    actual, verdict = [], "PASSED"
    try:
        for call in p["calls"] + p["checks"]:
            ctx.check_stop()
            ctx.log(f"step {p['index']}: {call.text}")
            out = call.run(ctx)
            if out:
                actual.append(str(out))
    except StopRequested:
        ctx.log(f"step {p['index']}: stopped")
        raise
    except (StepFailed, AssertionError) as ex:
        verdict = "FAILED"
        actual.append(str(ex) or "check failed")
        ctx.log(f"step {p['index']}: FAILED {ex}")
    except Exception as ex:
        verdict = "ERROR"
        actual.append(f"{type(ex).__name__}: {ex}")
        ctx.log(f"step {p['index']}: ERROR {type(ex).__name__}: {ex}\n{traceback.format_exc()}")
    return {"index": p["index"], "action": p["action"], "expected": p["expected"], "critical": p["critical"],
            "actual": "; ".join(actual) or "done", "verdict": verdict,
            "durationMs": round((time.monotonic() - t0) * 1000)}


def _result(testrun, ctx, verdict, message, steps, started, problems=None):
    attachments = []
    if verdict != "PASSED":
        log = "\n".join(ctx.log_lines) or "(no log lines)"
        if problems:
            log = "\n".join(problems) + "\n\n" + log
        attachments.append({"name": f"bench-log-{testrun.get('itemId')}.txt", "contentType": "text/plain",
                            "contentBase64": base64.b64encode(log.encode()).decode()})
    out = {
        "itemId": testrun.get("itemId"),
        "name": testrun.get("name"),
        "testConfiguration": testrun.get("testConfiguration"),
        "releases": testrun.get("releases"),
        "versions": testrun.get("versions"),
        "verdict": verdict,
        "message": message,
        "steps": steps,
        "measurements": ctx.measurements,
        "bench": {"name": ctx.bench.name, "safeState": ctx.cleanup_report["safeState"],
                  "cleanupErrors": ctx.cleanup_report["errors"]},
        "attachments": attachments,
        "benchDurationSec": round(time.monotonic() - started, 3),
    }
    if problems:
        out["problems"] = problems
    return out
