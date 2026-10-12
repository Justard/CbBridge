"""
STAND-IN for your real Python framework.

Replace the body of execute() with a call into your framework. Keep the signature:
    execute(testrun: dict, report_step: callable) -> dict (the result JSON)

`testrun` is exactly what the bridge sends: itemId, name, parent, description,
testRunSteps, parameters, modifier, testConfiguration, timeLimitSec.

Demo switches (put them in "parameters"):
    stepDelaySec        seconds per step (default 1)
    simulateFailAtStep  step number that fails
    simulateHangSec     hang this long in step 1 (to see the time limit work)
"""
import time


def _params_as_dict(params):
    if isinstance(params, dict):
        return params
    if isinstance(params, list):  # [{"name": .., "value": ..}, ..]
        return {p.get("name"): p.get("value") for p in params if isinstance(p, dict)}
    return {}


def _read_step(step):
    """codebeamer step = positional array [Action, Expected result, Critical?]; dicts accepted too."""
    if isinstance(step, list):
        cells = step + [None] * (3 - len(step))
        return cells[0], cells[1], bool(cells[2])
    if isinstance(step, dict):
        return (step.get("action") or step.get("Action"),
                step.get("expected") or step.get("Expected result"),
                bool(step.get("critical") or step.get("Critical?")))
    return str(step), None, False


def execute(testrun, report_step=lambda step: None):
    params = _params_as_dict(testrun.get("parameters"))
    steps = testrun.get("testRunSteps") or [["(no steps given)", "", False]]
    if not isinstance(steps, list):
        steps = [steps]

    delay = float(params.get("stepDelaySec", 1))
    hang = float(params.get("simulateHangSec", 0))
    fail_at = int(params.get("simulateFailAtStep", 0))

    results, verdict = [], "PASSED"
    for i, step in enumerate(steps, start=1):
        action, expected, critical = _read_step(step)
        time.sleep(delay)
        if hang and i == 1:
            time.sleep(hang)
        ok = i != fail_at
        result = {
            "index": i,
            "action": action,
            "expected": expected,
            "critical": critical,
            "actual": "as expected" if ok else "deviation (simulated)",
            "verdict": "PASSED" if ok else "FAILED",
        }
        results.append(result)
        report_step(result)
        if not ok:
            verdict = "FAILED"
            if critical:  # a failed critical step stops the test; others continue
                break

    return {
        "itemId": testrun.get("itemId"),
        "name": testrun.get("name"),
        # echo what the test ran against (interface / firmware / release), so results are traceable
        "testConfiguration": testrun.get("testConfiguration"),
        "releases": testrun.get("releases"),
        "versions": testrun.get("versions"),
        "verdict": verdict,
        "message": "" if verdict == "PASSED" else "failed step(s): " + ", ".join(
            str(r["index"]) for r in results if r["verdict"] == "FAILED"),
        "steps": results,
    }
