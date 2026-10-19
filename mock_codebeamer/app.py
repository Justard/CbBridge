"""
FAKE codebeamer for local testing — NOT the real API shapes.
The JSON below is invented. Once you capture real responses from your server,
paste them in here so the bridge is tested against reality.

Base URL for the bridge:  http://localhost:5002/cb
    GET /cb/rest/projects
    GET /cb/rest/project/<id>
    GET /cb/rest/project/<id>/categories
    GET /cb/rest/category/<id>/outline
    GET /cb/rest/item/<id>
    GET /cb/rest/item/<id>/references
    PUT /cb/api/v3/testruns/<id>        (result upload)
    GET /cb/_uploads                    (see what the bridge uploaded)
    POST /cb/api/v3/items/<id>/comments (AI analysis comment, multipart)
    GET /cb/_comments                   (see the comments the bridge added)
    POST /cb/api/v3/items/<id>/attachments (framework files, multipart "attachments")
    GET /cb/_attachments                (see the files the bridge uploaded)
Any non-empty username/password or token is accepted.
"""
from flask import Flask, abort, jsonify, request

app = Flask(__name__)
UPLOADS = []
COMMENTS = []
ATTACHMENTS = []
CREATED = []          # test runs created through the v3 "create test run" call
NEXT_RUN_ID = [90001]

MODIFIER = {"uri": "/user/7", "name": "jdoe"}
PARENT = {"uri": "/item/88130", "name": "Release gate 4.3"}
TRACKER = {"uri": "/category/4471", "name": "Test cases"}


R43 = [{"uri": "/item/7001", "name": "R4.3"}]
FW431 = [{"uri": "/item/7101", "name": "FW 4.3.1"}, {"uri": "/item/7102", "name": "HW C2"}]
FW430RC2 = [{"uri": "/item/7103", "name": "FW 4.3.0-rc2"}]


def make_item(iid, name, description, steps, parameters, parent=PARENT, versions=FW431):
    """Shaped like the codebeamer test case schema: no "id" (only "uri"), enums as {id,name},
    testSteps as positional arrays [Action, Expected result, Critical?].
    "parameters" is NOT in that schema — demo switches for the framework stand-in only.
    "releases"/"versions" hold the references the bridge builds Test Configuration from."""
    return {
        "releases": R43,
        "versions": versions,
        "uri": f"/item/{iid}", "version": 3, "parent": parent, "tracker": TRACKER,
        "priority": {"id": 2, "name": "High"}, "name": name,
        "status": {"id": 4, "name": "Accepted"}, "type": {"id": 3, "name": "Functional"},
        "submittedAt": "2026-09-30T08:12:00+02:00", "submitter": MODIFIER,
        "modifiedAt": "2026-10-09T16:40:00+02:00", "modifier": MODIFIER,
        "verifies": [{"uri": "/item/2210", "name": "REQ wake-up timing"}],
        "preAction": "Bench powered, ECU flashed with release build.",
        "testSteps": steps,
        "postAction": "Ignition OFF, bench idle.",
        "description": description, "descFormat": "Wiki",
        "parameters": parameters,
    }


ITEMS = {
    "88130": make_item("88130", "Release gate 4.3", "Folder for the release gate test cases.", [], {}, parent=None),
    "88131": make_item("88131", "CAN bus wake-up on ignition", "ECU must wake within 500 ms after ignition ON.",
                  [["Switch ignition ON", "CAN traffic starts", True],
                   ["Measure wake-up time", "< 500 ms", True]],
                  {"stepDelaySec": 1}),
    "88132": make_item("88132", "Bluetooth pairing with 5 devices", "Pair five phones in sequence.",
                  [["Enable pairing", "Head unit visible", True],
                   ["Pair 5 devices", "5 paired", False],
                   ["Reboot head unit", "5 still paired", True]],
                  {"stepDelaySec": 1, "simulateFailAtStep": 2}, versions=FW430RC2),
    "88133": make_item("88133", "Navigation reroute (hangs — shows time limit)",
                  "Step 1 hangs for 120 s so the time limit has to stop it.",
                  [["Block current route", "Reroute in < 3 s", True]],
                  {"simulateHangSec": 120}),
}


# A test case whose parameter set generated 14 test runs (references of /item/10231)
TC_10231 = {"uri": "/item/10231", "name": "TC CAN bus wake-up"}
TEMPS = ["-40C", "-20C", "0C", "25C", "60C", "85C", "105C"]
GENERATED_RUNS = []
for n in range(14):
    rid = str(88140 + n)
    temp, volt = TEMPS[n % 7], ("9V" if n < 7 else "16V")
    ITEMS[rid] = make_item(rid, f"CAN wake-up · temp={temp} · supply={volt}",
                           f"Generated run {n + 1} of 14 from the test case parameter set.",
                           [["Set climate chamber to " + temp, "Chamber stable", True],
                            ["Set supply to " + volt, "Supply stable", True],
                            ["Switch ignition ON", "Wake-up < 500 ms", True]],
                           {"temperature": temp, "supply": volt, "stepDelaySec": 0.3}, parent=TC_10231)
    GENERATED_RUNS.append({"uri": f"/item/{rid}", "name": ITEMS[rid]["name"], "type": "Test Run"})
ITEMS["10231"] = make_item("10231", TC_10231["name"], "Test case; its parameter set generates 14 runs.",
                           [["Switch ignition ON", "Wake-up < 500 ms", True]], {}, parent=None)


# Run multiple: category 4480 holds Testabläufe (test sets / test runs) that link to test cases
TEST_CASES = {
    "10232": ("Power-on self test", [["Apply 12 V", "Current < 2 A", True], ["Read self-test DTCs", "No DTC set", True]]),
    "10233": ("Ignition cycle 50x", [["Cycle ignition 50 times", "No reset", True]]),
    "10234": ("Sleep current", [["Enter sleep mode", "Sleep within 60 s", True], ["Measure current", "< 100 µA", True]]),
    "10235": ("Bluetooth pairing", [["Enable pairing", "Head unit visible", True], ["Pair phone", "Paired", True]]),
    "10236": ("Wi-Fi hotspot", [["Enable hotspot", "SSID visible", True], ["Connect client", "Internet reachable", False]]),
    "10237": ("CAN wake-up on ignition", [["Switch ignition ON", "CAN traffic starts", True]]),
}
for tc_id, (tc_name, tc_steps) in TEST_CASES.items():
    ITEMS[tc_id] = make_item(tc_id, tc_name, f"Test case {tc_name}.", tc_steps, {"stepDelaySec": 0.3}, parent=None)

TESTABLAEUFE = {   # Testablauf → (name, type, linked test cases)
    "88200": ("Smoke · Infotainment", "Test Set", ["10232", "10237"]),
    "88201": ("Regression · Power", "Test Run", ["10232", "10233", "10234"]),
    "88202": ("Night · Connectivity", "Test Set", ["10235", "10236"]),
}
for ta_id, (ta_name, ta_type, _) in TESTABLAEUFE.items():
    ITEMS[ta_id] = make_item(ta_id, ta_name, f"{ta_type} grouping test cases.", [], {}, parent=None)


@app.before_request
def require_auth():
    if request.path.startswith("/cb/_"):
        return None
    auth = request.headers.get("Authorization", "")
    if not auth:
        return jsonify(message="Unauthorized"), 401
    # any login works, except the password "wrong" (to test rejected credentials)
    if auth.startswith("Basic "):
        import base64
        try:
            if base64.b64decode(auth[6:]).decode().endswith(":wrong"):
                return jsonify(message="Bad credentials"), 401
        except Exception:
            return jsonify(message="Bad Authorization header"), 401


@app.get("/cb/rest/projects")
def projects():
    return jsonify([{"id": 29, "uri": "/project/29", "name": "Infotainment ECU", "keyName": "INFO"},
                    {"id": 31, "uri": "/project/31", "name": "Telematics unit", "keyName": "TCU"}])


@app.get("/cb/rest/project/<pid>")
def project(pid):
    return jsonify({"id": int(pid), "uri": f"/project/{pid}", "name": "Infotainment ECU", "keyName": "INFO"})


@app.get("/cb/rest/project/<pid>/categories")
def categories(pid):
    return jsonify([{"id": 4480, "uri": "/category/4480", "name": "Testabläufe", "type": "Test Set"},
                    {"id": 4471, "uri": "/category/4471", "name": "Test runs", "type": "Test Run"},
                    {"id": 4470, "uri": "/category/4470", "name": "Test cases", "type": "Test Case"}])


@app.get("/cb/rest/category/<cid>/outline")
def outline(cid):
    if cid == "4480":   # a folder with the Testabläufe inside (the folder itself is not a Testablauf)
        groups = [{"uri": f"/item/{i}", "name": n, "type": t} for i, (n, t, _) in TESTABLAEUFE.items()]
        return jsonify([{"uri": "/item/88199", "name": "Release 4.3", "type": "Folder", "children": groups}])
    children = [{"uri": f"/item/{i}", "name": ITEMS[i]["name"], "status": "Ready"} for i in ("88131", "88132", "88133")]
    return jsonify([{"uri": "/item/88130", "name": ITEMS["88130"]["name"], "status": "Ready", "children": children}])


@app.get("/cb/rest/item/<iid>")
def item(iid):
    if iid not in ITEMS:
        abort(404)
    return jsonify(ITEMS[iid])


@app.get("/cb/rest/item/<iid>/references")
def references(iid):
    if iid in TESTABLAEUFE:
        cases = [{"uri": f"/item/{c}", "name": TEST_CASES[c][0], "type": "Test Case"} for c in TESTABLAEUFE[iid][2]]
        return jsonify(cases + [{"uri": "/item/2210", "name": "REQ wake-up timing", "type": "Requirement"}])
    if iid == "88130":
        return jsonify([{"uri": f"/item/{i}", "name": ITEMS[i]["name"], "type": "Test Run"} for i in ("88131", "88132", "88133")])
    if iid == "10231":
        # like codebeamer usually does: newest first
        return jsonify(list(reversed(GENERATED_RUNS)) + [{"uri": "/item/2210", "name": "REQ wake-up timing", "type": "Requirement"}])
    return jsonify([{"uri": "/item/10231", "name": "TC CAN bus wake-up", "type": "Test Case"}])


@app.post("/cb/api/v3/trackers/<tracker_id>/testruns")
def create_test_run(tracker_id):
    body = request.get_json(silent=True) or {}
    new_id = str(NEXT_RUN_ID[0]); NEXT_RUN_ID[0] += 1
    CREATED.append({"trackerId": tracker_id, "newId": new_id, "body": body})
    ITEMS[new_id] = make_item(new_id, body.get("name", "New test run"), "Created by the bridge.", [], {}, parent=None)
    return jsonify(id=int(new_id), uri=f"/item/{new_id}", name=body.get("name")), 201


@app.get("/cb/_created")
def created():
    return jsonify(CREATED)


@app.put("/cb/api/v3/testruns/<tid>")
def upload(tid):
    UPLOADS.append({"testRunId": tid, "body": request.get_json(silent=True)})
    return jsonify(id=tid, updated=True)


@app.post("/cb/api/v3/items/<iid>/comments")
def add_comment(iid):
    COMMENTS.append({"itemId": iid, "comment": request.form.get("comment"), "commentFormat": request.form.get("commentFormat")})
    return jsonify(id=len(COMMENTS), itemId=iid), 201


@app.post("/cb/api/v3/items/<iid>/attachments")
def add_attachment(iid):
    for f in request.files.getlist("attachments"):
        ATTACHMENTS.append({"itemId": iid, "name": f.filename, "contentType": f.mimetype, "size": len(f.read())})
    return jsonify(ATTACHMENTS[-1:]), 201


@app.get("/cb/_attachments")
def attachments():
    return jsonify(ATTACHMENTS)


@app.get("/cb/_comments")
def comments():
    return jsonify(COMMENTS)


@app.get("/cb/_uploads")
def uploads():
    return jsonify(UPLOADS)


if __name__ == "__main__":
    app.run(host="127.0.0.1", port=5002)
