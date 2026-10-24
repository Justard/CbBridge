"""
The step catalogue: sentences -> functions, loaded from steps.yaml and checked at START-UP.

A mistake in steps.yaml (unknown function, typo in a placeholder, two sentences that collide) stops the framework
from starting, with every problem listed — instead of a test failing on the bench at 3 a.m.

Two ways to call the same function from a codebeamer step:
    When the supply is set to 13.5 V          sentence (Given/When/Then/And/But, or German Angenommen/Wenn/Dann/Und/Aber)
    _PSU_SET(13.5 V)                          keyword with arguments (positional in sentence order, or name=value)
"""
import difflib
import importlib
import inspect
import re

import yaml

from bench.units import ParamError, TypeTable

PLACEHOLDER = re.compile(r"\{(\w+)(?::(\w+))?\}")
KEYWORD_CALL = re.compile(r"^_([A-Z][A-Z0-9_]*)\s*(?:\((.*)\))?\s*$", re.S)
PARAM_REF = re.compile(r"<([\w.\-]+)>")          # <supply> -> value from the test run's parameters


class CatalogueError(ValueError):
    """steps.yaml has problems; .problems lists all of them."""

    def __init__(self, path, problems):
        self.problems = problems
        super().__init__(f"{path}: {len(problems)} problem(s)\n  " + "\n  ".join(problems))


class Undefined(LookupError):
    def __init__(self, text, suggestions):
        self.text, self.suggestions = text, suggestions
        hint = f" — did you mean: {' / '.join(suggestions)}" if suggestions else ""
        super().__init__(f"no step definition for: {text}{hint}")


class StepDef:
    def __init__(self, sentence, function_name, fn, placeholders, regex, keyword=None, note=None):
        self.sentence = sentence
        self.function_name = function_name
        self.fn = fn
        self.placeholders = placeholders     # [(name, type)] in sentence order
        self.regex = regex
        self.keyword = keyword
        self.note = note
        self.aliases = []                    # further sentences for the same function (other language, wording)


class Call:
    """One resolved line: function + converted arguments, ready to run."""

    def __init__(self, text, step_def, kwargs):
        self.text, self.step_def, self.kwargs = text, step_def, kwargs

    def run(self, ctx):
        return self.step_def.fn(ctx, **self.kwargs)


class Catalogue:
    def __init__(self, steps, types, path):
        self.steps, self.types, self.path = steps, types, path
        self.by_keyword = {s.keyword: s for s in steps if s.keyword}
        self._sentences = [(s, s.regex) for s in steps] + [(s, rx) for s in steps for _, rx in s.aliases]

    # ---- loading ---------------------------------------------------------------------------------------------
    @classmethod
    def load(cls, path, package="bench.keywords"):
        with open(path, encoding="utf-8") as f:
            doc = yaml.safe_load(f) or {}
        problems = []
        try:
            types = TypeTable(doc.get("enums"))
        except ValueError as ex:
            types, problems = TypeTable(), [str(ex)]

        steps, seen_shapes, seen_keywords = [], {}, {}
        for n, entry in enumerate(doc.get("steps") or [], start=1):
            where = f"step {n}"
            if not isinstance(entry, dict) or not entry.get("sentence") or not entry.get("function"):
                problems.append(f"{where}: needs 'sentence' and 'function'")
                continue
            where = f"step {n} '{entry['sentence']}'"
            fn = _resolve(package, entry["function"], problems, where)
            sentences = [entry["sentence"]] + list(entry.get("also") or [])
            compiled = []
            for sentence in sentences:
                result = _compile(sentence, types, problems, where)
                if result is None:
                    continue
                placeholders, regex, shape = result
                if shape in seen_shapes:
                    problems.append(f"{where}: same sentence as {seen_shapes[shape]} — the match would be ambiguous")
                seen_shapes[shape] = where
                compiled.append((sentence, placeholders, regex))
            if not compiled:
                continue
            names = [p for p, _ in compiled[0][1]]
            for sentence, placeholders, _ in compiled[1:]:
                if sorted(p for p, _ in placeholders) != sorted(names):
                    problems.append(f"{where}: 'also' sentence '{sentence}' must use the same placeholders {names}")
            if fn is not None:
                _check_signature(fn, entry["function"], names, problems, where)
            keyword = entry.get("keyword")
            if keyword:
                if not re.fullmatch(r"[A-Z][A-Z0-9_]*", keyword):
                    problems.append(f"{where}: keyword '{keyword}' must be UPPER_CASE (called as _{keyword}(...))")
                elif keyword in seen_keywords:
                    problems.append(f"{where}: keyword {keyword} already used by {seen_keywords[keyword]}")
                seen_keywords[keyword] = where
            sd = StepDef(compiled[0][0], entry["function"], fn, compiled[0][1], compiled[0][2], keyword, entry.get("note"))
            sd.aliases = [(s, rx) for s, _, rx in compiled[1:]]
            steps.append(sd)

        if not steps and not problems:
            problems.append("no steps defined")
        if problems:
            raise CatalogueError(path, problems)
        return cls(steps, types, path)

    # ---- resolving one line ----------------------------------------------------------------------------------
    def resolve(self, text, params=None):
        """'the supply is set to 13.5 V' or '_PSU_SET(13.5 V)' -> Call. Raises Undefined or ParamError."""
        text = substitute(text, params or {})
        m = KEYWORD_CALL.match(text)
        if m:
            return self._resolve_keyword(text, m.group(1), m.group(2) or "")
        clean = _normalise(text)
        hits = [(sd, rx.fullmatch(clean)) for sd, rx in self._sentences]
        hits = [(sd, mm) for sd, mm in hits if mm]
        if not hits:
            raise Undefined(text, self.suggest(clean))
        sd, mm = hits[0]
        if len({id(s) for s, _ in hits}) > 1:
            raise ParamError(f"'{text}' matches several sentences: {', '.join(s.sentence for s, _ in hits)}")
        kwargs = {}
        for name, type_name in sd.placeholders:
            kwargs[name] = self.types.convert(type_name, mm.group(name))
        return Call(text, sd, kwargs)

    def _resolve_keyword(self, text, keyword, arg_text):
        sd = self.by_keyword.get(keyword)
        if sd is None:
            close = difflib.get_close_matches(keyword, list(self.by_keyword), n=3)
            raise Undefined(text, [f"_{k}(...)" for k in close])
        args = _split_args(arg_text)
        kwargs, order = {}, [p for p, _ in sd.placeholders]
        types = dict(sd.placeholders)
        for i, arg in enumerate(args):
            name, eq, value = arg.partition("=")
            if eq and name.strip() in types:
                name, value = name.strip(), value
            elif i < len(order):
                name, value = order[i], arg
            else:
                raise ParamError(f"_{keyword}: too many arguments (takes {', '.join(order) or 'none'})")
            kwargs[name] = self.types.convert(types[name], value.strip())
        missing = [p for p in order if p not in kwargs]
        if missing:
            raise ParamError(f"_{keyword}: missing {', '.join(missing)}")
        return Call(text, sd, kwargs)

    def suggest(self, clean, n=2):
        shapes = {PLACEHOLDER.sub(lambda m: f"<{m.group(1)}>", sd.sentence): sd for sd in self.steps}
        return difflib.get_close_matches(clean, list(shapes), n=n, cutoff=0.6)

    def describe(self):
        """For GET /steps: what test authors may write."""
        return [{"sentence": sd.sentence, "also": [s for s, _ in sd.aliases],
                 "keyword": f"_{sd.keyword}({', '.join(p for p, _ in sd.placeholders)})" if sd.keyword else None,
                 "parameters": [{"name": p, "type": t} for p, t in sd.placeholders],
                 "function": sd.function_name, "note": sd.note} for sd in self.steps]


# ---- helpers ---------------------------------------------------------------------------------------------------
def substitute(text, params):
    def repl(m):
        key = m.group(1)
        if key not in params:
            raise ParamError(f"parameter <{key}> is not set for this test run (have: {', '.join(params) or 'none'})")
        return str(params[key])
    return PARAM_REF.sub(repl, text)


def _normalise(text):
    return re.sub(r"\s+", " ", text).strip().rstrip(".").strip()


def _compile(sentence, types, problems, where):
    names, parts, pos = [], [], 0
    sentence = _normalise(sentence)
    shape = PLACEHOLDER.sub("{}", sentence).lower()
    for m in PLACEHOLDER.finditer(sentence):
        name, type_name = m.group(1), m.group(2) or "word"
        if type_name not in types:
            problems.append(f"{where}: unknown type '{type_name}' in {{{m.group(0)[1:-1]}}} "
                            f"(known: {', '.join(types.names())})")
            return None
        if name in [n for n, _ in names]:
            problems.append(f"{where}: placeholder '{name}' used twice")
            return None
        names.append((name, type_name))
        parts.append(_literal(sentence[pos:m.start()]))
        parts.append(f"(?P<{name}>{types.regex(type_name)})")
        pos = m.end()
    parts.append(_literal(sentence[pos:]))
    regex = re.compile("".join(parts), re.IGNORECASE)
    return names, regex, shape


def _literal(text):
    return re.escape(re.sub(r"\s+", " ", text)).replace(r"\ ", r"\s+").replace(" ", r"\s+")


def _resolve(package, dotted, problems, where):
    module, _, attr = dotted.rpartition(".")
    if not module:
        problems.append(f"{where}: function '{dotted}' must be module.function (e.g. power.set_voltage)")
        return None
    try:
        mod = importlib.import_module(f"{package}.{module}")
    except ImportError as ex:
        problems.append(f"{where}: module '{module}' not found in {package} ({ex})")
        return None
    fn = getattr(mod, attr, None)
    if not callable(fn):
        problems.append(f"{where}: no function '{attr}' in {package}.{module}")
        return None
    return fn


def _check_signature(fn, dotted, names, problems, where):
    sig = inspect.signature(fn)
    params = list(sig.parameters.values())
    if not params or params[0].kind not in (params[0].POSITIONAL_ONLY, params[0].POSITIONAL_OR_KEYWORD):
        problems.append(f"{where}: {dotted} must take ctx as its first parameter")
        return
    rest = {p.name: p for p in params[1:]}
    takes_any = any(p.kind == p.VAR_KEYWORD for p in params)
    for name in names:
        if name not in rest and not takes_any:
            problems.append(f"{where}: {dotted}() has no parameter '{name}' (has: {', '.join(rest) or 'only ctx'})")
    for p in rest.values():
        if p.default is p.empty and p.kind in (p.POSITIONAL_OR_KEYWORD, p.KEYWORD_ONLY) and p.name not in names:
            problems.append(f"{where}: {dotted}() needs '{p.name}', but the sentence has no {{{p.name}}}")


def _split_args(text):
    """'13.5 V, "hello, world", direction=reverse' -> ['13.5 V', '"hello, world"', 'direction=reverse']"""
    args, cur, quoted = [], "", False
    for ch in text:
        if ch == '"':
            quoted = not quoted
        if ch == "," and not quoted:
            args.append(cur.strip()); cur = ""
        else:
            cur += ch
    if cur.strip():
        args.append(cur.strip())
    return args
