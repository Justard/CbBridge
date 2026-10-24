"""
Command line for developers: run ONE test run JSON without the bridge, codebeamer or Flask.
The input is the same JSON the bridge sends ("Input JSON" button in the bridge UI).

    python cli.py --input testrun-88131.json
    python cli.py --input testrun-88131.json --output result.json --time-limit 20
    python cli.py --input testrun-88131.json --in-process     # breakpoints in executor.py hit directly

    python cli.py --input testrun-bench.json --check       # only check the sentences, touch no device
    python cli.py --steps                                   # list the sentence catalogue

Exit code: 0 = PASSED (or check OK), 1 = anything else.
"""
import argparse
import json
import sys

import bench
import executor
import runner


def main():
    ap = argparse.ArgumentParser(description="Run one test run JSON locally.")
    ap.add_argument("--input", "-i", help="test run JSON (same as POST /execute body)")
    ap.add_argument("--check", action="store_true", help="resolve every sentence, run nothing")
    ap.add_argument("--steps", action="store_true", help="print the sentence catalogue")
    ap.add_argument("--output", "-o", help="write the result JSON here")
    ap.add_argument("--time-limit", type=float, help="override timeLimitSec")
    ap.add_argument("--in-process", action="store_true",
                    help="no child process and no time limit — easiest for debugging")
    args = ap.parse_args()

    catalogue, _ = bench.load()          # also validates steps.yaml / bench.yaml
    if args.steps:
        for s in catalogue.describe():
            print(f"  {s['sentence']}" + (f"    | {s['keyword']}" if s["keyword"] else ""))
            for a in s["also"]:
                print(f"    also: {a}")
        sys.exit(0)
    if not args.input:
        ap.error("--input is required")

    with open(args.input, encoding="utf-8") as f:
        testrun = json.load(f)
    if args.time_limit:
        testrun["timeLimitSec"] = args.time_limit

    if args.check:
        from bench.context import _params_as_dict
        from bench.engine import plan
        _, problems = plan(testrun, catalogue, _params_as_dict(testrun.get("parameters")))
        print("\n".join(problems) or "all sentences known")
        sys.exit(1 if problems else 0)

    if args.in_process:
        result = executor.execute(testrun, lambda s: print(f"  step {s['index']}: {s['verdict']}", file=sys.stderr))
    else:
        result = runner.run_with_limit(testrun)

    text = json.dumps(result, indent=2, ensure_ascii=False)
    print(text)
    if args.output:
        with open(args.output, "w", encoding="utf-8") as f:
            f.write(text)
    sys.exit(0 if result.get("verdict") == "PASSED" else 1)


if __name__ == "__main__":
    main()
