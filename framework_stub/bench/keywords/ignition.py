"""Ignition relay steps."""


def switch(ctx, state):
    ign = ctx.device("ignition")
    if state:
        ign.on()
        ctx.values["ignition_on_at"] = ign.switched_on_at   # later steps measure from here
    else:
        ign.off()
    return f"ignition {'ON' if state else 'OFF'}"
