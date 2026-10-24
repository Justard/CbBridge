"""
FAKE bench: a power supply, an ignition relay, a CAN interface and a motor, around a simulated ECU.
Everything is invented. It behaves just enough like hardware to show the idea:

  - the ECU wakes up `wakeMs` after ignition ON (slower below 10.5 V, not at all below 9 V)
  - an awake ECU sends CAN heartbeat 0x1A0 and motor status 0x3B0
  - the motor follows a CAN speed command with a ramp; supply current rises with speed
  - outputs (supply on/off, ignition, motor command) are kept in a small state file, like real hardware keeps
    its state when the test process dies — so the "emergency safe state" after a kill can be checked

Fault injection for demos and tests, through the test run's parameters:
    fake.wakeMs        ECU wake-up time in ms (default 180)
    fake.motorMaxRpm   motor saturates here (default 4000)
    fake.sleepCurrentA current with ignition OFF (default 0.00008)
    fake.psuFault      true -> the supply refuses to switch on (device error, not a failed check)

A REAL driver has the same shape: a class built as Class(settings, ctx), with methods and close().
"""
import json
import os
import tempfile
import time

HEARTBEAT = 0x1A0
MOTOR_STATUS = 0x3B0
MOTOR_COMMAND = 0x2B0


class DeviceError(RuntimeError):
    """The device itself misbehaves (no answer, refused) — the step is ERROR, the bench needs a look."""


def _state_file(settings):
    return (settings.get("state_file") or os.environ.get("FAKE_BENCH_STATE")
            or os.path.join(tempfile.gettempdir(), "cbbridge-fake-bench.json"))


def read_state(path=None):
    path = path or _state_file({})
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


class _World:
    """The simulated physics, shared by all fake devices of ONE test (kept on the context)."""

    def __init__(self, params, state_file):
        p = params or {}
        self.wake_ms = float(p.get("fake.wakeMs", 180))
        self.max_rpm = float(p.get("fake.motorMaxRpm", 4000))
        self.sleep_current = float(p.get("fake.sleepCurrentA", 0.00008))
        self.psu_fault = str(p.get("fake.psuFault", "")).lower() in ("1", "true", "yes")
        self.state_file = state_file
        saved = read_state(state_file)
        self.voltage = float(saved.get("psu_voltage", 0.0))
        self.output = bool(saved.get("psu_output", False))
        self.ignition = bool(saved.get("ignition", False))
        self.ignition_on_at = time.monotonic() - 3600 if self.ignition else None
        self.rpm_target = float(saved.get("motor_target_rpm", 0.0))
        self.direction = saved.get("motor_direction", "forward")
        self._rpm = 0.0
        self._rpm_at = time.monotonic()

    def save(self):
        state = {"psu_voltage": self.voltage, "psu_output": self.output, "ignition": self.ignition,
                 "motor_target_rpm": self.rpm_target, "motor_direction": self.direction,
                 "updated": time.strftime("%Y-%m-%dT%H:%M:%S")}
        tmp = self.state_file + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(state, f)
        os.replace(tmp, self.state_file)

    # ECU model
    def awake_at(self):
        """monotonic time the ECU is/was awake from, or None while it can't wake."""
        if not (self.output and self.ignition and self.voltage >= 9.0 and self.ignition_on_at is not None):
            return None
        wake = self.wake_ms + (100 if self.voltage < 10.5 else 0)
        return self.ignition_on_at + wake / 1000

    def awake(self):
        t = self.awake_at()
        return t is not None and time.monotonic() >= t

    def rpm(self):
        now = time.monotonic()
        target = min(self.rpm_target, self.max_rpm) if self.awake() else 0.0
        step = 3000 * (now - self._rpm_at)        # ramp 3000 rpm/s
        self._rpm = min(target, self._rpm + step) if target > self._rpm else max(target, self._rpm - step)
        self._rpm_at = now
        return self._rpm

    def current(self):
        if not self.output:
            return 0.0
        if not self.awake():
            return self.sleep_current
        return 0.35 + self.rpm() * 0.0008


def _world(ctx, settings):
    if ctx is None:                                   # emergency safe state: no test context any more
        return _World({}, _state_file(settings))
    w = getattr(ctx, "_fake_world", None)
    if w is None:
        w = _World(ctx.params, _state_file(settings))
        ctx._fake_world = w
    return w


class PowerSupply:
    def __init__(self, settings, ctx):
        self.max_voltage = float(settings.get("max_voltage", 30))
        self.w = _world(ctx, settings)

    def set_voltage(self, volts):
        if not 0 <= volts <= self.max_voltage:
            raise DeviceError(f"{volts} V is outside 0…{self.max_voltage:g} V of this supply")
        self.w.voltage = volts
        self.w.save()

    def output_on(self):
        if self.w.psu_fault:
            raise DeviceError("supply did not confirm OUTPUT ON (simulated fault)")
        self.w.output = True
        self.w.save()

    def output_off(self):
        self.w.output = False
        self.w.save()

    def measure_current(self):
        return self.w.current()

    def measure_voltage(self):
        return self.w.voltage if self.w.output else 0.0

    def close(self):
        pass


class Ignition:
    def __init__(self, settings, ctx):
        self.w = _world(ctx, settings)

    def on(self):
        if not self.w.ignition:
            self.w.ignition = True
            self.w.ignition_on_at = time.monotonic()
            self.w.save()

    def off(self):
        self.w.ignition = False
        self.w.ignition_on_at = None
        self.w.save()

    @property
    def switched_on_at(self):
        return self.w.ignition_on_at

    def close(self):
        pass


class CanBus:
    def __init__(self, settings, ctx):
        self.channel = settings.get("channel", "can0")
        self.w = _world(ctx, settings)
        self.sent = []

    def first_seen(self, frame_id):
        """monotonic time a frame first appeared on the bus, or None. (A real driver: read the receive queue.)"""
        if frame_id in (HEARTBEAT, MOTOR_STATUS) and self.w.awake():
            return self.w.awake_at()
        return None

    def read_motor_status(self):
        if not self.w.awake():
            return None
        return {"rpm": round(self.w.rpm(), 1), "direction": self.w.direction}

    def send(self, frame_id, data):
        self.sent.append((frame_id, data))
        if frame_id == MOTOR_COMMAND and self.w.awake():
            self.w.rpm_target = float(data.get("rpm", 0))
            self.w.direction = data.get("direction", "forward")
            self.w.save()

    def close(self):
        pass


class Motor:
    """The motor is driven through the ECU over CAN — this object is the test's handle for it."""

    def __init__(self, settings, ctx):
        self.w = _world(ctx, settings)
        self.can = CanBus(settings, ctx)

    def command(self, rpm, direction="forward"):
        if not self.w.awake():
            raise DeviceError("ECU not awake — motor command has no receiver")
        self.can.send(MOTOR_COMMAND, {"rpm": rpm, "direction": direction})

    def speed(self):
        status = self.can.read_motor_status()
        return None if status is None else status["rpm"]

    def stop(self):
        # works from the safe state too: clear the command even if the ECU is asleep
        self.w.rpm_target = 0.0
        self.w.save()

    def close(self):
        pass
