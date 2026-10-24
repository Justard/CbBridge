"""Power supply steps."""
from bench.context import StepFailed


def set_voltage(ctx, voltage):
    ctx.device("psu").set_voltage(voltage)
    return f"supply set to {voltage:g} V"


def output(ctx, state):
    psu = ctx.device("psu")
    psu.output_on() if state else psu.output_off()
    return f"output {'ON' if state else 'OFF'}"


def expect_current_below(ctx, limit, settle=0.2):
    ctx.wait(settle)                                       # let the reading settle
    amps = ctx.measure("supply current", ctx.device("psu").measure_current(), "A", f"< {_fmt_a(limit)}")
    if amps >= limit:
        raise StepFailed(f"current {_fmt_a(amps)}, limit < {_fmt_a(limit)}")
    return f"current {_fmt_a(amps)}"


def expect_current_between(ctx, low, high):
    ctx.wait(0.2)
    amps = ctx.measure("supply current", ctx.device("psu").measure_current(), "A",
                       f"{_fmt_a(low)} … {_fmt_a(high)}")
    if not low <= amps <= high:
        raise StepFailed(f"current {_fmt_a(amps)}, expected {_fmt_a(low)} … {_fmt_a(high)}")
    return f"current {_fmt_a(amps)}"


def _fmt_a(amps):
    if amps < 0.001:
        return f"{amps * 1e6:.0f} µA"
    if amps < 1:
        return f"{amps * 1000:.0f} mA"
    return f"{amps:.2f} A"
