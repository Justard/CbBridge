"""
Receives the bridge's events (Events:Subscribers) and checks their signature — for local testing, and as a
reference for receivers. Port 5004.
    EVENT_SECRET=dev-event-secret python tools/event_sink/event_sink.py
    GET http://localhost:5004/_events     what arrived (with signatureValid)
"""
import hashlib
import hmac
import os

from flask import Flask, jsonify, request

app = Flask(__name__)
SECRET = os.environ.get("EVENT_SECRET", "dev-event-secret").encode()
EVENTS = []


@app.post("/hook")
def hook():
    body = request.get_data()
    sent = request.headers.get("X-Bridge-Signature", "")
    expected = "sha256=" + hmac.new(SECRET, body, hashlib.sha256).hexdigest()
    EVENTS.append({"event": request.headers.get("X-Bridge-Event"),
                   "signatureValid": hmac.compare_digest(sent, expected) if sent else None,
                   "body": request.get_json(silent=True)})
    return "", 204


@app.get("/_events")
def events():
    return jsonify(EVENTS)


if __name__ == "__main__":
    app.run(host="127.0.0.1", port=int(os.environ.get("SINK_PORT", "5004")))
