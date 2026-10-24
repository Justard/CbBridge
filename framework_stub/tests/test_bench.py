"""
Tests for the bench side: sentences, context, safe shutdown. No hardware — the fake bench only.

    cd framework_stub
    python -m unittest discover -s tests -v
"""
import json
import os
import sys
import tempfile
import textwrap
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
STUB = os.path.dirname(HERE)
sys.path.insert(0, STUB)

_STATE = tempfile.NamedTemporaryFile(prefix="fake-bench-", suffix=".json", delete=False).name
os.environ["FAKE_BENCH_STATE"] = _STATE

import bench                                   # noqa: E402
import executor                                # noqa: E402
import runner                                  # noqa: E402
from bench.devices.fake import read_state      # noqa: E402
from bench.registry import Catalogue, CatalogueError, Undefined  # noqa: E402
from bench.units import ParamError, TypeTable  # noqa: E402

CATALOGUE, BENCH = bench.load()


def example(name):
    with open(os.path.join(STUB, "examples", name), encoding="utf-8") as f:
        return json.load(f)


def testrun(steps, params=None, limit=30, item="1"):
    return {"itemId": item, "name": "t", "timeLimitSec": limit, "parameters": params or {}, "testRunSteps": steps}


def run(tr):
    return bench.run_test(tr, CATALOGUE, BENCH)


def reset_bench():
    if os.path.exists(_STATE):
        os.remove(_STATE)


class Units(unittest.TestCase):
    def setUp(self):
        self.t = TypeTable({"direction": ["forward", "reverse"]})

    def test_time_in_ms(self):
        self.assertEqual(self.t.convert("ms", "500 ms"), 500)
        self.assertEqual(self.t.convert("ms", "0.5 s"), 500)
        self.assertEqual(self.t.convert("ms", "0,5s"), 500)
        self.assertEqual(self.t.convert("ms", "2 min"), 120000)
        self.assertEqual(self.t.convert("ms", "250"), 250)

    def test_electrical(self):
        self.assertAlmostEqual(self.t.convert("V", "800 mV"), 0.8)
        self.assertAlmostEqual(self.t.convert("A", "150 mA"), 0.15)
        self.assertAlmostEqual(self.t.convert("A", "90 µA"), 0.00009)

    def test_wrong_unit_is_clear(self):
        with self.assertRaises(ParamError) as e:
            self.t.convert("V", "13 A")
        self.assertIn("unit 'A' not allowed", str(e.exception))

    def test_other_types(self):
        self.assertEqual(self.t.convert("hex", "0x1A0"), 416)
        self.assertIs(self.t.convert("onoff", "ON"), True)
        self.assertIs(self.t.convert("onoff", "aus"), False)
        self.assertEqual(self.t.convert("direction", "Reverse"), "reverse")
        self.assertEqual(self.t.convert("int", "3"), 3)
        with self.assertRaises(ParamError):
            self.t.convert("int", "3.5")
        with self.assertRaises(ParamError):
            self.t.convert("direction", "sideways")


class CatalogueLoading(unittest.TestCase):
    def load(self, text):
        with tempfile.NamedTemporaryFile("w", suffix=".yaml", delete=False, encoding="utf-8") as f:
            f.write(textwrap.dedent(text))
        try:
            return Catalogue.load(f.name)
        finally:
            os.remove(f.name)

    def test_shipped_catalogue_loads(self):
        self.assertGreaterEqual(len(CATALOGUE.steps), 10)

    def test_all_problems_reported_at_once(self):
        with self.assertRaises(CatalogueError) as e:
            self.load("""
                steps:
                  - sentence: "the supply is set to {voltage:volt}"
                    function: power.set_voltage
                  - sentence: "x is done"
                    function: power.does_not_exist
                  - sentence: "y is set to {value:V}"
                    function: power.set_voltage
                  - sentence: "the ignition is switched {state:onoff}"
                    function: ignition.switch
                  - sentence: "THE ignition is switched {s:onoff}"
                    function: ignition.switch
                  - sentence: "z"
                    function: nomodule.f
            """)
        text = "\n".join(e.exception.problems)
        self.assertIn("unknown type 'volt'", text)
        self.assertIn("no function 'does_not_exist'", text)
        self.assertIn("has no parameter 'value'", text)          # sentence and function disagree
        self.assertIn("ambiguous", text)                          # same sentence twice
        self.assertIn("module 'nomodule' not found", text)
        self.assertGreaterEqual(len(e.exception.problems), 5)

    def test_function_needing_more_than_sentence_gives(self):
        with self.assertRaises(CatalogueError) as e:
            self.load("""
                steps:
                  - sentence: "the speed is checked"
                    function: motor.expect_speed
            """)
        self.assertIn("needs 'speed'", str(e.exception))

    def test_duplicate_keyword(self):
        with self.assertRaises(CatalogueError) as e:
            self.load("""
                steps:
                  - { sentence: "a is done", function: motor.stop, keyword: STOP }
                  - { sentence: "b is done", function: motor.stop, keyword: STOP }
            """)
        self.assertIn("already used", str(e.exception))


class Resolving(unittest.TestCase):
    def test_sentence_and_keyword_reach_same_function(self):
        a = CATALOGUE.resolve("the supply is set to 13.5 V")
        b = CATALOGUE.resolve("_PSU_SET(13.5 V)")
        c = CATALOGUE.resolve("_PSU_SET(voltage=13500 mV)")
        self.assertEqual({a.step_def.function_name, b.step_def.function_name, c.step_def.function_name},
                         {"power.set_voltage"})
        self.assertEqual(a.kwargs, b.kwargs)
        self.assertAlmostEqual(c.kwargs["voltage"], 13.5)

    def test_case_and_spaces_dont_matter(self):
        call = CATALOGUE.resolve("The  motor is commanded to 1500 rpm REVERSE.")
        self.assertEqual(call.kwargs, {"speed": 1500.0, "direction": "reverse"})

    def test_german_wording(self):
        self.assertEqual(CATALOGUE.resolve("die Zündung wird eingeschaltet").kwargs, {"state": True})

    def test_parameter_substitution(self):
        call = CATALOGUE.resolve("the supply is set to <supply>", {"supply": "9 V"})
        self.assertEqual(call.kwargs, {"voltage": 9.0})
        with self.assertRaises(ParamError):
            CATALOGUE.resolve("the supply is set to <supply>", {})

    def test_unknown_sentence_suggests(self):
        with self.assertRaises(Undefined) as e:
            CATALOGUE.resolve("the ignition is turned on")
        self.assertIn("the ignition is switched <state>", e.exception.suggestions)
        with self.assertRaises(Undefined) as e:
            CATALOGUE.resolve("_PSU_SETT(1 V)")
        self.assertIn("_PSU_SET(...)", e.exception.suggestions)


class Engine(unittest.TestCase):
    def setUp(self):
        reset_bench()

    def test_wakeup_example_passes_with_measurements(self):
        r = run(example("bench-wakeup.json"))
        self.assertEqual(r["verdict"], "PASSED", r)
        names = [m["name"] for m in r["measurements"]]
        self.assertIn("wake-up time", names)
        self.assertEqual(r["bench"]["safeState"], "reached")
        self.assertFalse(read_state()["psu_output"])

    def test_slow_ecu_fails_on_the_check_not_the_action(self):
        r = run(example("bench-wakeup-slow-ecu.json"))
        self.assertEqual(r["verdict"], "FAILED")
        self.assertEqual([s["verdict"] for s in r["steps"]], ["PASSED", "FAILED"])
        self.assertIn("limit < 300 ms", r["steps"][1]["actual"])
        self.assertEqual(r["attachments"][0]["contentType"], "text/plain")

    def test_non_critical_failure_continues_critical_stops(self):
        r = run(example("bench-motor.json"))       # step 3 (non-critical) fails, step 4 still runs
        self.assertEqual([s["verdict"] for s in r["steps"]], ["PASSED", "PASSED", "FAILED", "PASSED"])
        tr = example("bench-motor.json")
        tr["testRunSteps"][2][2] = True              # now critical -> step 4 not run
        r = run(tr)
        self.assertEqual(len(r["steps"]), 3)
        self.assertEqual(read_state()["motor_target_rpm"], 0.0)   # cleanup + safe state stopped the motor

    def test_undefined_sentence_blocks_before_any_device(self):
        r = run(example("bench-undefined.json"))
        self.assertEqual(r["verdict"], "BLOCKED")
        self.assertEqual(r["steps"], [])
        self.assertEqual(len(r["problems"]), 2)
        self.assertEqual(read_state(), {})            # nothing was switched

    def test_device_error_is_error_and_bench_still_made_safe(self):
        r = run(testrun([["Given the supply is set to 12 V\nAnd the supply output is switched on", "", False],
                         ["When the ignition is switched on", "", False]], {"fake.psuFault": True}))
        self.assertEqual(r["verdict"], "ERROR")
        self.assertEqual(len(r["steps"]), 1)          # ERROR stops the test, even on a non-critical step
        self.assertIn("did not confirm", r["steps"][0]["actual"])
        self.assertEqual(r["bench"]["safeState"], "reached")

    def test_free_text_expected_is_not_executed(self):
        r = run(testrun([["the bench waits 10 ms", "Nothing special happens", False]]))
        self.assertEqual(r["verdict"], "PASSED")

    def test_check_keyword_in_action_column_is_fine_but_when_in_expected_is_not(self):
        r = run(testrun([["the bench waits 10 ms", "When the ignition is switched on", False]]))
        self.assertEqual(r["verdict"], "BLOCKED")
        self.assertIn("use Then/And", r["message"])

    def test_cleanups_run_newest_first_and_errors_dont_stop_others(self):
        from bench.context import Context
        order = []
        ctx = Context(testrun([]), BENCH)
        ctx.on_cleanup("a", lambda: order.append("a"))
        ctx.on_cleanup("broken", lambda: 1 / 0)
        ctx.on_cleanup("c", lambda: order.append("c"))
        report = ctx.close()
        self.assertEqual(order, ["c", "a"])
        self.assertEqual(len(report["errors"]), 1)
        self.assertEqual(ctx.close(), report)          # second close does nothing

    def test_each_test_gets_a_fresh_context(self):
        r1 = run(example("bench-wakeup.json"))
        r2 = run(testrun([["CAN frame 0x1A0 is received within 50 ms", "", False]]))
        self.assertEqual(r1["verdict"], "PASSED")
        # the ignition was switched off by the safe state of test 1, so no frame — and no value leaks over
        self.assertEqual(r2["verdict"], "FAILED")
        self.assertEqual(r2["measurements"], [])

    def test_bench_ready_notices_unsafe_leftovers(self):
        with open(_STATE, "w") as f:
            json.dump({"psu_output": True, "psu_voltage": 12}, f)
        r = run(testrun([["Given the bench is ready", "", True]]))
        self.assertEqual(r["verdict"], "FAILED")
        self.assertIn("previous test", r["steps"][0]["actual"])


class Dispatch(unittest.TestCase):
    def test_plain_steps_still_use_the_simulation(self):
        tr = testrun([["Switch ignition ON", "CAN traffic starts", True]], {"stepDelaySec": 0})
        self.assertFalse(bench.is_bench_test(tr))
        self.assertEqual(executor.execute(tr)["verdict"], "PASSED")

    def test_sentences_go_to_the_bench(self):
        self.assertTrue(bench.is_bench_test(example("bench-wakeup.json")))
        self.assertTrue(bench.is_bench_test(testrun([["_WAIT(1 ms)", "", False]])))


class Stopping(unittest.TestCase):
    """Through the real child process, like /execute."""

    def setUp(self):
        reset_bench()
        self._soft = runner.SOFT_STOP_SEC
        runner.SOFT_STOP_SEC = 2

    def tearDown(self):
        runner.SOFT_STOP_SEC = self._soft

    def test_soft_stop_cleans_up_inside_the_test(self):
        tr = example("bench-hang.json")
        tr["timeLimitSec"] = 1.5
        t0 = time.monotonic()
        r = runner.run_with_limit(tr)
        self.assertEqual(r["verdict"], "TIMEOUT")
        self.assertIn("soft stop", r["message"])
        self.assertEqual(r["bench"]["safeState"], "reached")
        self.assertEqual([s["verdict"] for s in r["steps"]], ["PASSED", "ABORTED"])
        self.assertLess(time.monotonic() - t0, 3.4)          # did not need the kill
        self.assertFalse(read_state()["psu_output"])

    def test_kill_then_emergency_safe_state(self):
        tr = example("bench-block.json")
        tr["timeLimitSec"] = 1.5
        r = runner.run_with_limit(tr)
        self.assertEqual(r["verdict"], "TIMEOUT")
        self.assertIn("killed", r["message"])
        self.assertIn("emergency", r["bench"]["safeState"])
        state = read_state()
        self.assertFalse(state["psu_output"])
        self.assertFalse(state["ignition"])

    def test_cancel_is_aborted(self):
        import threading
        tr = example("bench-hang.json")
        tr["itemId"], tr["timeLimitSec"] = "cancel-me", 30
        threading.Timer(1.0, lambda: runner.cancel("cancel-me")).start()
        r = runner.run_with_limit(tr)
        self.assertEqual(r["verdict"], "ABORTED")
        self.assertEqual(r["bench"]["safeState"], "reached")


if __name__ == "__main__":
    unittest.main()
