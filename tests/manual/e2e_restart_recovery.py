#!/usr/bin/env python3
"""
Drives steps 13-16 of the required end-to-end scenario against a running Daksh Gateway at localhost:5190:
restart the Gateway while a scheduled job is overdue, and confirm JobRunnerWorker's catch-up poll still
runs it once the Gateway comes back (not automated here, since it needs an external process restart - see
tests/manual/e2e_scenario.py's "Exact next steps" note in chatgpt_changes.md for why this is a separate script).

Usage (run against a Gateway you started yourself, same as e2e_scenario.py):

    python3 -I tests/manual/e2e_restart_recovery.py schedule
    # ... now kill and restart the Gateway process while this automation is still due ...
    python3 -I tests/manual/e2e_restart_recovery.py verify

State is handed between phases via /tmp/e2e_restart_state.json (this run's own file, independent of
e2e_scenario.py's /tmp/e2e_state.json).
"""
import json
import sys
import time
import urllib.request
import urllib.error

BASE = "http://localhost:5190"
STATE_FILE = "/tmp/e2e_restart_state.json"
RUN_ID = int(time.time())


def call(method, path, token=None, ws=None, body=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if ws:
        headers["X-Workspace-Id"] = ws
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.status, json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


def expect(step, status, body, ok=(200, 201, 202)):
    mark = "OK" if status in ok else "FAIL"
    print(f"[{mark}] {step}: HTTP {status}")
    if mark == "FAIL":
        print(json.dumps(body, indent=2))
        raise SystemExit(1)
    return body


def phase_schedule():
    s, b = call("POST", "/api/v1/auth/register", body={
        "email": f"restart{RUN_ID}@example.com", "password": "Sup3rSecret!",
        "displayName": "Restart Tester", "workspaceName": "Restart Co"})
    b = expect("register/login", s, b)
    token, ws = b["accessToken"], b["workspaceId"]

    s, b = call("POST", "/api/v1/providers", token, ws, {"name": "local", "kind": "scripted"})
    b = expect("configure provider", s, b)
    provider_id = b["id"]
    s, b = call("POST", f"/api/v1/providers/{provider_id}/models", token, ws, {
        "modelId": "research", "contextWindow": 16000, "maxOutputTokens": 2000,
        "tools": True, "structuredOutput": True, "embeddings": False,
        "inputPricePerMTok": 0, "outputPricePerMTok": 0})
    expect("add research model", s, b)

    s, b = call("POST", "/api/v1/agents", token, ws, {
        "name": "Restart Agent", "description": "Used to test scheduler restart recovery",
        "defaultModel": "local/research", "toolProfile": "coding",
        "systemInstructions": "You are a careful assistant."})
    b = expect("create agent", s, b)
    agent_id = b["id"]

    s, b = call("POST", "/api/v1/sessions", token, ws, {"agentId": agent_id, "title": "Restart recovery session"})
    b = expect("create session", s, b)
    session_id = b["id"]

    # Schedule the job 3 seconds out. The orchestrator restarts the Gateway right after this call returns and
    # keeps it down past that 3-second mark, so the job is overdue (missed) while nothing is polling for it.
    s, b = call("POST", f"/api/v1/sessions/{session_id}/messages", token, ws,
                {"text": "Schedule a reminder to check on this in 3 seconds."})
    b = expect("ask to schedule a task", s, b)
    for _ in range(60):
        s, b2 = call("GET", f"/api/v1/sessions/{session_id}/executions/{b['executionId']}", token, ws)
        if s == 200 and b2["status"] in ("Succeeded", "Failed", "Cancelled"):
            expect("scheduling execution finishes", s, b2, ok=(200,))
            if b2["status"] != "Succeeded":
                raise SystemExit(f"scheduling execution did not succeed: {b2}")
            break
        time.sleep(0.5)
    else:
        raise SystemExit("timed out waiting for the scheduling execution")

    s, b = call("GET", "/api/v1/automations", token, ws)
    automations = expect("list automations", s, b)
    assert len(automations) >= 1, f"expected an automation to have been created, got {automations}"
    automation = automations[0]
    assert automation["lastRunAt"] is None, f"expected the automation not to have run yet, got {automation}"
    print("    scheduled automation:", automation)

    with open(STATE_FILE, "w") as f:
        json.dump({"token": token, "ws": ws, "automation_id": automation["id"]}, f)
    print(f"\nScheduled. Now kill and restart the Gateway (it should stay down past "
          f"{automation['nextRunAt']}), then run this script again with 'verify'.")


def phase_verify():
    with open(STATE_FILE) as f:
        state = json.load(f)
    token, ws, automation_id = state["token"], state["ws"], state["automation_id"]

    for _ in range(40):
        s, b = call("GET", "/api/v1/automations", token, ws)
        automations = expect("list automations", s, b, ok=(200,))
        automation = next((a for a in automations if a["id"] == automation_id), None)
        assert automation is not None, f"automation {automation_id} disappeared: {automations}"
        if automation["lastRunAt"] is not None:
            print("    automation after restart:", automation)
            assert automation["consecutiveFailures"] == 0, f"expected the catch-up run to succeed, got {automation}"
            print("\nRestart recovery confirmed: the scheduled job ran via JobRunnerWorker's catch-up poll "
                  "after the Gateway was restarted past its due time.")
            return
        time.sleep(1)
    raise SystemExit(f"timed out waiting for the automation to run after restart; last seen: {automation}")


if __name__ == "__main__":
    if len(sys.argv) != 2 or sys.argv[1] not in ("schedule", "verify"):
        raise SystemExit(__doc__)
    {"schedule": phase_schedule, "verify": phase_verify}[sys.argv[1]]()
