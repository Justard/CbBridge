"""
Runs ONE test run in a child process and enforces timeLimitSec.
Shared by the Flask app and the CLI, so both behave the same.

Why a child process: a hung test (or one waiting on hardware) can always be killed;
a thread cannot be forced to stop.
"""
import multiprocessing as mp
import queue
import threading
import time

from executor import execute

_lock = threading.Lock()
_active = {}        # itemId -> Process   (tests currently running)
_cancelled = set()  # itemIds stopped via /cancel


def _child(testrun, out_q):
    """Runs inside the child process."""
    try:
        result = execute(testrun, lambda step: out_q.put(("step", step)))
        out_q.put(("done", result))
    except Exception as ex:  # report, never crash silently
        out_q.put(("error", f"{type(ex).__name__}: {ex}"))


def is_running(item_id):
    with _lock:
        return str(item_id) in _active


def cancel(item_id):
    item_id = str(item_id)
    with _lock:
        proc = _active.get(item_id)
        if proc is None:
            return False
        _cancelled.add(item_id)
    _stop(proc)
    return True


def _stop(proc):
    # NOTE: if your tests start their own sub-processes (tools, simulators), kill the whole
    # tree here, e.g. with psutil: for c in psutil.Process(proc.pid).children(recursive=True): c.kill()
    if proc.is_alive():
        proc.terminate()
        proc.join(timeout=5)
    if proc.is_alive():
        proc.kill()
        proc.join(timeout=5)


def run_with_limit(testrun):
    item_id = str(testrun.get("itemId", ""))
    limit = float(testrun.get("timeLimitSec") or 60)

    out_q = mp.Queue()
    proc = mp.Process(target=_child, args=(testrun, out_q), daemon=True)
    started = time.monotonic()
    deadline = started + limit
    proc.start()
    with _lock:
        _active[item_id] = proc
        _cancelled.discard(item_id)

    steps, final, error = [], None, None
    try:
        while True:
            try:
                kind, data = out_q.get(timeout=0.2)
            except queue.Empty:
                if not proc.is_alive() or time.monotonic() >= deadline:
                    break
                continue
            if kind == "step":
                steps.append(data)
            elif kind == "done":
                final = data
                break
            else:
                error = data
                break
    finally:
        with _lock:
            _active.pop(item_id, None)
            was_cancelled = item_id in _cancelled
            _cancelled.discard(item_id)

    duration = round(time.monotonic() - started, 2)
    if final is not None:
        proc.join(timeout=2)
        final["durationSec"] = duration
        return final

    _stop(proc)
    if was_cancelled:
        verdict, message = "ABORTED", "stopped via /cancel"
    elif error:
        verdict, message = "ERROR", error
    elif time.monotonic() >= deadline:
        verdict, message = "TIMEOUT", f"stopped after timeLimitSec={limit:g}"
    else:
        verdict, message = "ERROR", "test process exited without a result"

    return {
        "itemId": testrun.get("itemId"),
        "name": testrun.get("name"),
        "verdict": verdict,
        "message": message,
        "durationSec": duration,
        "steps": steps,  # steps that finished before the stop are kept
    }
