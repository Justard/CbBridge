"""
bench.yaml: which device OBJECTS this bench has, and how to bring it into a safe state.

    devices:
      psu: { driver: fake.PowerSupply, settings: { max_voltage: 30 } }
    safe_state:
      - psu.output_off

A driver is "module.Class" under bench/devices/ (or a full dotted path to your own module).
It is built as Class(settings, ctx) — ctx is None when the safe state runs after a crashed test.
Methods named in safe_state take no arguments.
"""
import importlib

import yaml


class BenchConfigError(ValueError):
    pass


class BenchConfig:
    def __init__(self, devices, safe_state, name="bench"):
        self.name = name
        self.devices = devices          # name -> (class, settings)
        self.safe_state = safe_state    # [(device, method)]

    @classmethod
    def load(cls, path):
        with open(path, encoding="utf-8") as f:
            doc = yaml.safe_load(f) or {}
        errors, devices, safe = [], {}, []

        for name, spec in (doc.get("devices") or {}).items():
            if not isinstance(spec, dict) or not spec.get("driver"):
                errors.append(f"device '{name}': needs 'driver: module.Class'")
                continue
            try:
                devices[name] = (_resolve_class(spec["driver"]), spec.get("settings") or {})
            except (ImportError, AttributeError) as ex:
                errors.append(f"device '{name}': driver '{spec['driver']}' not found ({ex})")

        for entry in doc.get("safe_state") or []:
            dev, _, method = str(entry).partition(".")
            if dev not in devices:
                if dev not in (doc.get("devices") or {}):
                    errors.append(f"safe_state '{entry}': no device '{dev}'")
                continue
            if not callable(getattr(devices[dev][0], method, None)):
                errors.append(f"safe_state '{entry}': {devices[dev][0].__name__} has no method '{method}'")
            safe.append((dev, method))

        if errors:
            raise BenchConfigError(f"{path}:\n  " + "\n  ".join(errors))
        return cls(devices, safe, doc.get("name") or "bench")

    def open_device(self, name, ctx):
        if name not in self.devices:
            raise KeyError(f"no device '{name}' in bench.yaml (have: {', '.join(self.devices)})")
        klass, settings = self.devices[name]
        return klass(dict(settings), ctx)

    def apply_safe_state(self, get_device, log):
        """Normal end of a test: the safe-state methods of the devices this test opened. Returns error texts."""
        errors = []
        for dev, method in self.safe_state:
            obj = get_device(dev)
            if obj is None:
                continue          # this test never touched that device
            try:
                getattr(obj, method)()
                log(f"safe state: {dev}.{method}")
            except Exception as ex:
                errors.append(f"safe state {dev}.{method}: {type(ex).__name__}: {ex}")
        return errors

    def emergency_safe_state(self, log=print):
        """After the test process was killed: open EVERY device fresh (from the framework process) and make it safe.
        The test process is gone, so this must not rely on anything it held in memory."""
        opened, errors = {}, []
        for dev, method in self.safe_state:
            try:
                if dev not in opened:
                    opened[dev] = self.open_device(dev, None)
                getattr(opened[dev], method)()
                log(f"emergency safe state: {dev}.{method}")
            except Exception as ex:
                errors.append(f"emergency safe state {dev}.{method}: {type(ex).__name__}: {ex}")
        for obj in opened.values():
            try:
                getattr(obj, "close", lambda: None)()
            except Exception:
                pass
        return errors


def _resolve_class(path):
    module, _, attr = path.rpartition(".")
    try:
        mod = importlib.import_module(f"bench.devices.{module}")
    except ImportError:
        mod = importlib.import_module(module)
    return getattr(mod, attr)
