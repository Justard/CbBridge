"""Motor steps. Speed in rpm, direction from the enum in steps.yaml."""
from bench.context import StepFailed


def command(ctx, speed, direction="forward"):
    motor = ctx.device("motor")
    motor.command(speed, direction)
    ctx.on_cleanup("motor command back to 0 rpm", motor.stop)   # undone even if a later step fails
    return f"commanded {speed:g} rpm {direction}"


def expect_speed(ctx, speed, tolerance, timeout):
    motor = ctx.device("motor")
    reached = ctx.wait_until(lambda: (lambda v: v is not None and abs(v - speed) <= tolerance)(motor.speed()),
                             timeout / 1000)
    rpm = motor.speed() or 0.0
    ctx.measure("motor speed", rpm, "rpm", f"{speed:g} ± {tolerance:g}")
    if not reached:
        raise StepFailed(f"{rpm:.0f} rpm after {timeout:g} ms, expected {speed:g} ± {tolerance:g}")
    return f"{rpm:.0f} rpm"


def stop(ctx):
    ctx.device("motor").stop()
    return "motor stopped"
