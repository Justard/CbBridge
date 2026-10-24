"""Bench-wide steps."""
import time

from bench.context import StepFailed


def bench_ready(ctx):
    """Opens every device and checks nothing is left switched on from before."""
    for name in ctx.bench.devices:
        ctx.device(name)
    psu = ctx.device("psu")
    if psu.measure_voltage() > 0:
        raise StepFailed("supply output is already ON — previous test did not end in the safe state")
    return f"{len(ctx.bench.devices)} devices ready"


def wait(ctx, duration):
    ctx.wait(duration / 1000)
    return f"waited {duration:g} ms"


def nop(ctx):
    return "nothing done"


def note(ctx, text):
    ctx.log("note: " + text)
    return text


def hang(ctx, duration):
    """Demo: a long wait that DOES react to stop (ctx.wait) -> soft stop, safe state inside the test."""
    ctx.wait(duration)


def block(ctx, duration):
    """Demo: a call that does NOT react to stop (like a hung driver) -> the process is killed,
    and the framework applies the safe state from outside."""
    time.sleep(duration)
