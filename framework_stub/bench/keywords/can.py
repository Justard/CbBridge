"""CAN steps. Frame IDs are numbers ({frame:hex} accepts 0x1A0 or 416)."""
from bench.context import StepFailed


def expect_frame(ctx, frame, timeout):
    can = ctx.device("can")
    seen_at = ctx.wait_until(lambda: can.first_seen(frame), timeout / 1000)
    if seen_at is None:
        raise StepFailed(f"no frame 0x{frame:X} within {timeout:g} ms")
    since = ctx.values.get("ignition_on_at")
    if since is not None:
        ms = ctx.measure("wake-up time", (seen_at - since) * 1000, "ms", f"< {timeout:g} ms")
        return f"0x{frame:X} after {ms:.0f} ms"
    return f"0x{frame:X} seen"


def expect_wakeup_below(ctx, limit):
    ms = ctx.values.get("wake-up time")
    if ms is None:
        raise StepFailed("no wake-up time measured yet — put 'CAN frame … is received within …' before this step")
    if ms >= limit:
        raise StepFailed(f"wake-up {ms:.0f} ms, limit < {limit:g} ms")
    return f"wake-up {ms:.0f} ms"


def expect_silence(ctx, frame, duration):
    can = ctx.device("can")
    seen = ctx.wait_until(lambda: can.first_seen(frame), duration / 1000)
    if seen is not None:
        raise StepFailed(f"frame 0x{frame:X} seen, bus should be silent")
    return f"no 0x{frame:X} for {duration:g} ms"
