"""
Checks the bridge's MCP server with the official MCP Python SDK (pip install mcp) — the same client library
many agents use. Lists the tools and calls the read-only ones.

    python tools/mcp/mcp_smoke.py --url http://localhost:5000/mcp --key dev-agent-key [--mode auto|legacy] [--start 88132]
"""
import argparse
import asyncio
import json

import httpx2
from mcp import Client
from mcp.client.streamable_http import streamable_http_client


def text(result):
    return result.content[0].text if result.content else ""


async def main(a):
    http = httpx2.AsyncClient(headers={"Authorization": f"Bearer {a.key}"}, timeout=60)
    async with Client(streamable_http_client(a.url, http_client=http), mode=a.mode) as client:
        info = client.server_info
        print(f"connected: {info.name if info else '?'} (mode={a.mode})")
        tools = await client.list_tools()
        print("tools:", ", ".join(t.name for t in tools.tools))

        runs = await client.call_tool("list_runs", {"limit": 3})
        data = json.loads(text(runs))
        print(f"list_runs: {data['total']} runs; newest {[r['id'] for r in data['runs']]}")

        new = await client.call_tool("find_new_failures", {"runs": 20})
        nf = json.loads(text(new))["newFailures"]
        print(f"find_new_failures: {len(nf)}", [f"{x['name']} ({x['previousVerdict']}→{x['verdict']})" for x in nf[:3]])

        if data["runs"]:
            run = await client.call_tool("get_run", {"run_id": data["runs"][0]["id"]})
            r = json.loads(text(run))
            print(f"get_run: {r['selection']} → {[(x['position'], x['verdict'], x.get('trend')) for x in r['results']]}")
            failing = next((x for x in r["results"] if x["verdict"] != "PASSED"), None)
            if failing:
                cmp = json.loads(text(await client.call_tool("compare_with_last_pass", {"run_id": r["id"], "position": failing["position"]})))
                print("compare_with_last_pass:", (cmp["lastPassing"] or {}).get("runId"), cmp["responseChangesSinceLastPass"][:2])
                hist = json.loads(text(await client.call_tool("test_history", {"test_key": failing["testKey"]})))
                print(f"test_history: {hist['count']} runs, passRate {hist['passRate']}, flips {hist['flips']}")

        bad = await client.call_tool("get_run", {"run_id": "nope"})
        print("error handling:", bad.is_error if hasattr(bad, "is_error") else bad.isError, text(bad)[:60])

        if a.start:
            started = json.loads(text(await client.call_tool("start_run", {"test_run": a.start, "analyze": True})))
            print("start_run:", started)
            for _ in range(30):
                await asyncio.sleep(2)
                job = json.loads(text(await client.call_tool("get_job", {"job_id": started["jobId"]})))
                if job["done"]:
                    print("get_job:", job["state"], job["summary"], "runId", job["runId"])
                    break


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://localhost:5000/mcp")
    ap.add_argument("--key", required=True)
    ap.add_argument("--mode", default="auto", choices=["auto", "legacy"])
    ap.add_argument("--start", help="also start this test run via start_run")
    asyncio.run(main(ap.parse_args()))
