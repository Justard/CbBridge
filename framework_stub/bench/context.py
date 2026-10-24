"""
Context = everything ONE test run owns while it runs. A new one is built for every test and thrown away after.

    ctx.params      the test run's parameters (codebeamer parameter set, e.g. {"supply": "9 V", "temperature": "-40C"})
    ctx.config      test configuration / firmware the bridge sent ("R4.3 / FW 4.3.1")
    ctx.device(n)   a device object (power supply, CAN, ...) — opened on first use, closed after the test
    ctx.values      scratch space between steps: one step measures, a later step checks
    ctx.measure()   a named measurement that goes into the result JSON (and so into history / codebeamer)
    ctx.log()       a line for the bench log (attached to the codebeamer test run when the test fails)
    ctx.wait()      sleep that a stop request can interrupt — use it instead of time.sleep()
    ctx.on_cleanup  undo something at the end, also when the test fails or is stopped

Nothing is shared between tests: two tests never see each other's values, and a test that crashes
can't leave a device "half open" for the next one, because close() always runs.
"""
import time


class StepFailed(AssertionError):
    """A check did not hold — the step is FAILED (the device works, the value is wrong)."""


class StopRequested(Exception):
    """The bridge asked to stop (time limit or /cancel). Raised inside ctx.wait() / ctx.check_stop()."""


class Context:
    def __init__(self, testrun, bench, stop_event=None):
        self.testrun = testrun
        self.item_id = str(testrun.get("itemId", ""))
        self.name = testrun.get("name")
        self.params = _params_as_dict(testrun.get("parameters"))
        self.config = testrun.get("testConfiguration")
        self.bench = bench                    # BenchConfig: device definitions + safe state
        self.values = {}
        self.measurements = []
        self.log_lines = []
        self.cleanup_report = {"errors": [], "safeState": "not needed"}
        self._stop_event = stop_event
        self._devices = {}                    # name -> opened object, in open order
        self._cleanup = []                    # (description, fn), run in reverse
        self._started = time.monotonic()
        self._closed = False

    # ---- devices -------------------------------------------------------------------------------------------
    def device(self, name):
        """The device object called `name` in bench.yaml. Opened once per test, closed in close()."""
        if name not in self._devices:
            self._devices[name] = self.bench.open_device(name, self)
            self.log(f"opened device '{name}'")
        return self._devices[name]

    # ---- time and stopping ---------------------------------------------------------------------------------
    @property
    def stop_requested(self):
        return bool(self._stop_event is not None and self._stop_event.is_set())

    def check_stop(self):
        if self.stop_requested:
            raise StopRequested("stop requested by the bridge")

    def wait(self, seconds):
        """Sleep, but wake up at once when the bridge asks to stop."""
        end = time.monotonic() + max(0.0, seconds)
        while True:
            self.check_stop()
            left = end - time.monotonic()
            if left <= 0:
                return
            if self._stop_event is not None:
                self._stop_event.wait(min(left, 0.05))
            else:
                time.sleep(min(left, 0.05))

    def wait_until(self, condition, timeout_s, poll_s=0.01):
        """Poll condition() until it returns something truthy; returns it, or None after timeout_s."""
        end = time.monotonic() + timeout_s
        while True:
            value = condition()
            if value:
                return value
            if time.monotonic() >= end:
                return None
            self.wait(poll_s)

    # ---- results -------------------------------------------------------------------------------------------
    def measure(self, name, value, unit="", limit=None):
        self.values[name] = value
        self.measurements.append({"name": name, "value": round(value, 6) if isinstance(value, float) else value,
                                  "unit": unit, "limit": limit})
        return value

    def log(self, line):
        self.log_lines.append(f"{time.monotonic() - self._started:8.3f}s  {line}")

    def on_cleanup(self, description, fn):
        """fn() runs after the test, newest first — e.g. a step that heats the chamber registers 'back to 25 °C'."""
        self._cleanup.append((description, fn))

    # ---- end of test ---------------------------------------------------------------------------------------
    def close(self):
        """Always runs (finally). Order: cleanups of the steps → bench safe state → close devices.
        Errors are collected, never raised: one broken cleanup must not skip the others."""
        if self._closed:
            return self.cleanup_report
        self._closed = True
        errors = self.cleanup_report["errors"]

        for description, fn in reversed(self._cleanup):
            try:
                fn()
                self.log(f"cleanup: {description}")
            except Exception as ex:
                errors.append(f"cleanup '{description}': {type(ex).__name__}: {ex}")

        if self._devices:
            safe_errors = self.bench.apply_safe_state(self._devices.get, self.log)
            errors.extend(safe_errors)
            self.cleanup_report["safeState"] = "reached" if not safe_errors else "FAILED"

        for name, dev in reversed(list(self._devices.items())):
            try:
                close = getattr(dev, "close", None)
                if close:
                    close()
            except Exception as ex:
                errors.append(f"close '{name}': {type(ex).__name__}: {ex}")
        self._devices.clear()
        for e in errors:
            self.log("ERROR " + e)
        return self.cleanup_report


def _params_as_dict(params):
    if isinstance(params, dict):
        return dict(params)
    if isinstance(params, list):  # [{"name": .., "value": ..}, ..]
        return {p.get("name"): p.get("value") for p in params if isinstance(p, dict)}
    return {}
