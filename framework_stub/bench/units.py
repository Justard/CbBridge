"""
Parameter types for step sentences: "{name:type}".

Each type has
  - a regex, so a sentence like "the motor runs at {speed:rpm} {direction:direction}" splits unambiguously
  - a converter, so the function receives a number in a fixed base unit, never the raw text

Number types accept the units people actually write and convert to ONE base unit:
    {t:ms}   "500 ms", "0.5 s", "2 min"     -> 500.0, 500.0, 120000.0   (milliseconds)
    {u:V}    "13.5 V", "13,5V", "800 mV"    -> 13.5, 13.5, 0.8          (volts)
    {i:A}    "2 A", "150 mA", "90 µA"       -> 2.0, 0.15, 0.00009       (amperes)
A number without a unit is taken as the base unit ("500" for {t:ms} = 500 ms).

Add a type: put an entry in NUMBER_TYPES (number + units) or SIMPLE_TYPES (anything else),
or declare an enum in steps.yaml under "enums:".
"""
import re

_NUM = r"[-+]?\d+(?:[.,]\d+)?"


class ParamError(ValueError):
    """A value in a sentence can't be converted (wrong unit, out of range...)."""


# type -> (base unit, {unit spelling (lower case): factor to base})
NUMBER_TYPES = {
    "ms":   ("ms",  {"ms": 1, "s": 1000, "sec": 1000, "min": 60000, "us": 0.001, "µs": 0.001}),
    "s":    ("s",   {"s": 1, "sec": 1, "ms": 0.001, "min": 60, "h": 3600}),
    "V":    ("V",   {"v": 1, "mv": 0.001, "kv": 1000}),
    "A":    ("A",   {"a": 1, "ma": 0.001, "µa": 1e-6, "ua": 1e-6}),
    "rpm":  ("rpm", {"rpm": 1, "1/min": 1, "u/min": 1}),
    "degC": ("°C",  {"°c": 1, "c": 1, "degc": 1}),
    "pct":  ("%",   {"%": 1}),
    "int":  ("",    {}),
    "float": ("",   {}),
}


def _bool(text):
    t = text.strip().lower()
    if t in ("on", "ein", "true", "yes", "an", "enabled"):
        return True
    if t in ("off", "aus", "false", "no", "disabled"):
        return False
    raise ParamError(f"'{text}' is not on/off")


def _hex(text):
    t = text.strip()
    try:
        return int(t, 16) if t.lower().startswith("0x") else int(t)
    except ValueError:
        raise ParamError(f"'{text}' is not a number like 0x1A0") from None


# type -> (regex, converter)
SIMPLE_TYPES = {
    "word":  (r"\S+", str),
    "text":  (r'"[^"]*"', lambda t: t[1:-1]),        # quoted: Then the display shows "READY"
    "onoff": (r"on|off|ein|aus|true|false|an", _bool),
    "hex":   (r"0[xX][0-9A-Fa-f]+|\d+", _hex),
}


def _unit_regex(units):
    # number + ANY unit word: a wrong unit then gives "unit 'A' not allowed here" instead of "no step definition"
    return rf"{_NUM}(?:\s*[^\s\d.,][^\s]*)?"


def number_converter(type_name):
    base, units = NUMBER_TYPES[type_name]
    pattern = re.compile(rf"^\s*({_NUM})\s*(\S*)\s*$")

    def convert(text):
        m = pattern.match(text)
        if not m:
            raise ParamError(f"'{text}' is not a number")
        value = float(m.group(1).replace(",", "."))
        unit = m.group(2).lower()
        if type_name == "int":
            if unit or value != int(value):
                raise ParamError(f"'{text}' is not a whole number")
            return int(value)
        if not unit:
            return value
        if unit not in units:
            raise ParamError(f"'{text}': unit '{m.group(2)}' not allowed here, use {', '.join(units)}")
        return value * units[unit]

    return convert


class TypeTable:
    """All types one step catalogue can use: built-in + enums from steps.yaml."""

    def __init__(self, enums=None):
        self.types = {}
        for name, (_, units) in NUMBER_TYPES.items():
            self.types[name] = (_unit_regex(units), number_converter(name))
        self.types.update(SIMPLE_TYPES)
        for name, values in (enums or {}).items():
            self.add_enum(name, values)

    def add_enum(self, name, values):
        if not values or not all(isinstance(v, str) for v in values):
            raise ValueError(f"enum '{name}' needs a list of words")
        lookup = {v.lower(): v for v in values}
        alt = "|".join(re.escape(v) for v in sorted(values, key=len, reverse=True))

        def convert(text, _lookup=lookup, _name=name):
            v = _lookup.get(text.strip().lower())
            if v is None:
                raise ParamError(f"'{text}' is not one of {', '.join(_lookup.values())}")
            return v

        self.types[name] = (alt, convert)

    def __contains__(self, name):
        return name in self.types

    def regex(self, name):
        return self.types[name][0]

    def convert(self, name, text):
        return self.types[name][1](text)

    def names(self):
        return sorted(self.types)
