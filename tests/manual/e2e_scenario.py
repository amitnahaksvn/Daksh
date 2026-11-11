#!/usr/bin/env python3
"""Drives the required 16-step end-to-end scenario against a running Daksh Gateway at localhost:5190.

Each run registers a fresh user (email includes the current timestamp), so it is safe to run repeatedly
against the same database.
"""
import json
import time
import urllib.request
import urllib.error

BASE = "http://localhost:5190"
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


# 1. A user logs into the platform (register = first login).
s, b = call("POST", "/api/v1/auth/register", body={
    "email": f"researcher{RUN_ID}@example.com", "password": "Sup3rSecret!",
    "displayName": "Researcher", "workspaceName": "Research Co"})
b = expect("1. register/login", s, b)
token, ws = b["accessToken"], b["workspaceId"]

# 2. The user creates an AI research agent.
s, b = call("POST", "/api/v1/providers", token, ws, {"name": "local", "kind": "scripted"})
b = expect("3a. configure provider", s, b)
provider_id = b["id"]

s, b = call("POST", f"/api/v1/providers/{provider_id}/models", token, ws, {
    "modelId": "research", "contextWindow": 16000, "maxOutputTokens": 2000,
    "tools": True, "structuredOutput": True, "embeddings": False,
    "inputPricePerMTok": 0, "outputPricePerMTok": 0})
expect("3b. add research model", s, b)

s, b = call("POST", f"/api/v1/providers/{provider_id}/models", token, ws, {
    "modelId": "hash-embedding", "contextWindow": 0, "maxOutputTokens": 0,
    "embeddings": True, "inputPricePerMTok": 0, "outputPricePerMTok": 0})
expect("3c. add embedding model", s, b)

s, b = call("POST", "/api/v1/agents", token, ws, {
    "name": "AI Research Agent", "description": "Researches topics and remembers findings",
    "defaultModel": "local/research", "toolProfile": "coding",
    "systemInstructions": "You are a careful research assistant."})
b = expect("2. create research agent", s, b)
agent_id = b["id"]

# 4. The user sends a request to research a technical topic. 5-7: agent searches, fetches, summarizes.
s, b = call("POST", "/api/v1/sessions", token, ws, {"agentId": agent_id, "title": "Research session"})
b = expect("4a. create session", s, b)
session_id = b["id"]

s, b = call("POST", f"/api/v1/sessions/{session_id}/messages", token, ws,
            {"text": "Please research the topic of pgvector and summarize how it works."})
b = expect("4b. send research request", s, b)
execution_id = b["executionId"]


def wait_for_execution(execution_id, label, timeout=30):
    for _ in range(timeout * 2):
        s, b = call("GET", f"/api/v1/sessions/{session_id}/executions/{execution_id}", token, ws)
        if s == 200 and b["status"] in ("Succeeded", "Failed", "Cancelled", "WaitingApproval"):
            print(f"[{'OK' if b['status'] == 'Succeeded' else 'FAIL'}] {label}: status={b['status']}")
            if b["status"] != "Succeeded":
                print(json.dumps(b, indent=2))
                raise SystemExit(1)
            return b
        time.sleep(0.5)
    raise SystemExit(f"{label}: timed out waiting for execution {execution_id}")


result1 = wait_for_execution(execution_id, "5-8. agent searches web, fetches, summarizes, saves memory")
print("    final text:", result1["finalText"][:200])

s, b = call("GET", f"/api/v1/sessions/{session_id}/messages", token, ws)
messages = expect("8b. read transcript", s, b)
tool_names = [m["toolName"] for m in messages if m.get("toolName")]
print("    tools called during research:", tool_names)
assert "web_search" in tool_names and "web_fetch" in tool_names and "memory_write" in tool_names, \
    f"expected web_search, web_fetch, memory_write in {tool_names}"

# 9-10. Follow-up question; agent retrieves the stored memory.
s, b = call("POST", f"/api/v1/sessions/{session_id}/messages", token, ws,
            {"text": "What did you find out about pgvector earlier?"})
b = expect("9. follow-up question", s, b)
result2 = wait_for_execution(b["executionId"], "10. agent recalls memory")
print("    follow-up answer:", result2["finalText"][:200])

s, b = call("GET", f"/api/v1/sessions/{session_id}/messages?after=0", token, ws)
messages2 = expect("10b. read transcript", s, b)
tool_names2 = [m["toolName"] for m in messages2 if m.get("toolName")]
assert "memory_search" in tool_names2, f"expected memory_search in {tool_names2}"

# 11-12. Schedule a future task; the scheduler persists the job.
s, b = call("POST", f"/api/v1/sessions/{session_id}/messages", token, ws,
            {"text": "Schedule a reminder to check on this research in 5 seconds."})
b = expect("11. ask to schedule a task", s, b)
result3 = wait_for_execution(b["executionId"], "12. scheduler persists the job")
print("    schedule answer:", result3["finalText"][:200])

s, b = call("GET", "/api/v1/automations", token, ws)
automations = expect("12b. list automations", s, b)
assert len(automations) >= 1, "expected at least one automation to have been created"
print("    automation:", automations[0])

print("\nAll steps through scheduling passed. (Restart recovery and channel delivery are tested separately.)")
with open("/tmp/e2e_state.json", "w") as f:
    json.dump({"token": token, "ws": ws, "agent_id": agent_id, "session_id": session_id,
               "automation_id": automations[0]["id"]}, f)
