"""
Command line for developers: run ONE test run JSON without the bridge, codebeamer or Flask.
The input is the same JSON the bridge sends ("Input JSON" button in the bridge UI).

    python cli.py --input testrun-88131.json
    python cli.py --input testrun-88131.json --output result.json --time-limit 20
    python cli.py --input testrun-88131.json --in-process     # breakpoints in executor.py hit directly

Exit code: 0 = PASSED, 1 = anything else.
"""
import argparse
import json
import sys

import executor
import runner


def main():
    ap = argparse.ArgumentParser(description="Run one test run JSON locally.")
    ap.add_argument("--input", "-i", required=True, help="test run JSON (same as POST /execute body)")
    ap.add_argument("--output", "-o", help="write the result JSON here")
    ap.add_argument("--time-limit", type=float, help="override timeLimitSec")
    ap.add_argument("--in-process", action="store_true",
                    help="no child process and no time limit — easiest for debugging")
    args = ap.parse_args()

    with open(args.input, encoding="utf-8") as f:
        testrun = json.load(f)
    if args.time_limit:
        testrun["timeLimitSec"] = args.time_limit

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
