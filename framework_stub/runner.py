"""
Runs ONE test run in a child process and enforces timeLimitSec.
Shared by the Flask app and the CLI, so both behave the same.

Why a child process: a hung test (or one waiting on hardware) can always be killed;
a thread cannot be forced to stop.

Stopping (time limit or /cancel), in two stages:
  1. soft   set the stop event; ctx.wait() in the test raises, the test ends its step, runs its cleanups and
            the bench safe state itself, and returns the steps done so far       (BENCH_SOFT_STOP_SEC, 5 s)
  2. hard   still running after that -> kill the process, then apply the bench safe state from HERE,
            with freshly opened devices (bench.yaml "safe_state")
"""
import multiprocessing as mp
import os
import sys
import queue
import threading
import time

from executor import execute

# Soft stop: on time limit or /cancel the test first gets this long to stop by itself and bring the bench into its
# safe state. Only then is the process killed (and the safe state applied from here). Keep it well below the
# bridge's Framework:GraceSeconds (15 s), or the bridge stops waiting first.
SOFT_STOP_SEC = float(os.environ.get("BENCH_SOFT_STOP_SEC", "5"))

_lock = threading.Lock()
_active = {}        # itemId -> (Process, stop Event)   (tests currently running)
_progress = {}      # itemId -> {"steps": [...finished steps], "total": n, "startedAt": t}  (GET /progress)
_cancelled = set()  # itemIds stopped via /cancel


def _child(testrun, out_q, stop_event):
    """Runs inside the child process."""
    try:
        result = execute(testrun, lambda step: out_q.put(("step", step)), stop_event)
        out_q.put(("done", result))
    except Exception as ex:  # report, never crash silently
        out_q.put(("error", f"{type(ex).__name__}: {ex}"))


def progress(item_id):
    """Live state of a running test for GET /progress/<itemId>, or None."""
    with _lock:
        p = _progress.get(str(item_id))
        return None if p is None else {
            "itemId": str(item_id), "running": True, "stepsDone": len(p["steps"]), "stepsTotal": p["total"],
            "lastStep": p["steps"][-1] if p["steps"] else None,
            "elapsedSec": round(time.monotonic() - p["startedAt"], 1)}


def is_running(item_id):
    with _lock:
        return str(item_id) in _active


def cancel(item_id):
    """Asks the test to stop. run_with_limit() does the soft stop and, if needed, the kill."""
    item_id = str(item_id)
    with _lock:
        entry = _active.get(item_id)
        if entry is None:
            return False
        _cancelled.add(item_id)
    entry[1].set()
    return True


def _kill(proc):
    # NOTE: if your tests start their own sub-processes (tools, simulators), kill the whole
    # tree here, e.g. with psutil: for c in psutil.Process(proc.pid).children(recursive=True): c.kill()
    if proc.is_alive():
        proc.terminate()
        proc.join(timeout=3)
    if proc.is_alive():
        proc.kill()
        proc.join(timeout=3)


def _emergency_safe_state(testrun):
    """The test process was killed or crashed: it could not clean up. Make the bench safe from this process."""
    import bench
    if not bench.is_bench_test(testrun):
        return None
    try:
        _, bench_config = bench.load()
        name = bench_config.name
        errors = bench_config.emergency_safe_state(log=lambda line: print(line, file=sys.stderr, flush=True))
    except Exception as ex:
        name, errors = "?", [f"{type(ex).__name__}: {ex}"]
    return {"name": name, "cleanupErrors": errors,
            "safeState": "reached (emergency, after kill)" if not errors else "FAILED (emergency, after kill)"}


def run_with_limit(testrun):
    item_id = str(testrun.get("itemId", ""))
    limit = float(testrun.get("timeLimitSec") or 60)

    out_q = mp.Queue()
    stop_event = mp.Event()
    proc = mp.Process(target=_child, args=(testrun, out_q, stop_event), daemon=True)
    started = time.monotonic()
    deadline = started + limit
    proc.start()
    total = len(testrun.get("testRunSteps") or []) if isinstance(testrun.get("testRunSteps"), list) else None
    with _lock:
        _active[item_id] = (proc, stop_event)
        _cancelled.discard(item_id)
        _progress[item_id] = {"steps": [], "total": total, "startedAt": started}

    steps, final, error = [], None, None
    soft_deadline, timed_out = None, False
    try:
        while True:
            try:
                kind, data = out_q.get(timeout=0.2)
            except queue.Empty:
                kind, data = None, None
            if kind == "step":
                steps.append(data)
                with _lock:
                    if item_id in _progress:
                        _progress[item_id]["steps"].append(data)
            elif kind == "done":
                final = data
                break
            elif kind == "error":
                error = data
                break
            elif not proc.is_alive():
                break

            now = time.monotonic()
            if soft_deadline is None and (now >= deadline or stop_event.is_set()):
                timed_out = now >= deadline and not stop_event.is_set()
                stop_event.set()                       # 1. ask the test to stop (soft)
                soft_deadline = now + SOFT_STOP_SEC
            elif soft_deadline is not None and now >= soft_deadline:
                break                                  # 2. it did not stop in time -> kill below
    finally:
        with _lock:
            _active.pop(item_id, None)
            _progress.pop(item_id, None)
            was_cancelled = item_id in _cancelled
            _cancelled.discard(item_id)

    duration = round(time.monotonic() - started, 2)
    stopped = soft_deadline is not None

    if final is not None and not stopped:
        proc.join(timeout=2)
        final["durationSec"] = duration
        return final

    if final is not None:                              # stopped, but the test stopped itself and cleaned up
        proc.join(timeout=2)
        final["steps"] = final.get("steps") or steps
        how = "soft stop"
    else:
        _kill(proc)
        final = {"itemId": testrun.get("itemId"), "name": testrun.get("name"), "steps": steps}
        bench_report = _emergency_safe_state(testrun) if (stopped or error is None) else None
        if bench_report:
            final["bench"] = bench_report
        how = "killed" if stopped else None

    if was_cancelled:
        verdict, message = "ABORTED", "stopped via /cancel"
    elif error:
        verdict, message = "ERROR", error
    elif timed_out or (stopped and not was_cancelled):
        verdict, message = "TIMEOUT", f"stopped after timeLimitSec={limit:g}"
    else:
        verdict, message = "ERROR", "test process exited without a result"
    if how:
        safe = (final.get("bench") or {}).get("safeState")
        message += f" ({how}" + (f", safe state {safe}" if safe else "") + ")"

    final.update(verdict=verdict, message=message, durationSec=duration)
    return final
