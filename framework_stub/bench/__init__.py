"""
Bench side of the test framework: sentences -> functions, a context per test, safe shutdown.

    from bench import load, run_test, is_bench_test
    catalogue, bench = load()                  # steps.yaml + bench.yaml, checked; raises on any problem
    result = run_test(testrun, catalogue, bench, report_step, stop_event)

Files (override with environment variables):
    STEPS_FILE    bench/steps.yaml   the sentence catalogue
    BENCH_CONFIG  bench/bench.yaml   the devices of this bench and its safe state
"""
import os

from bench.config import BenchConfig, BenchConfigError
from bench.engine import is_bench_test, run_test
from bench.registry import Catalogue, CatalogueError

HERE = os.path.dirname(os.path.abspath(__file__))
_loaded = None


def paths():
    return (os.environ.get("STEPS_FILE") or os.path.join(HERE, "steps.yaml"),
            os.environ.get("BENCH_CONFIG") or os.path.join(HERE, "bench.yaml"))


def load(reload=False):
    """(catalogue, bench). Cached per process; the child process of a test loads it once on first use."""
    global _loaded
    if _loaded is None or reload:
        steps_file, bench_file = paths()
        _loaded = (Catalogue.load(steps_file), BenchConfig.load(bench_file))
    return _loaded


__all__ = ["load", "run_test", "is_bench_test", "Catalogue", "CatalogueError", "BenchConfig", "BenchConfigError"]
