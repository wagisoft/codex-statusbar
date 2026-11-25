"""Exercise the production session monitor using an isolated SQLite DB and synthetic rollouts."""
import json
import os
import sqlite3
import subprocess
import sys
import tempfile
from pathlib import Path
from uuid import uuid4


def main():
    dll = Path(sys.argv[1]).resolve()
    fixture = Path(__file__).resolve().parents[1] / "fixtures" / "fixture-modern.jsonl"
    records = fixture.read_text(encoding="utf-8").splitlines()
    original_id = json.loads(records[0])["payload"]["id"]
    with tempfile.TemporaryDirectory() as home:
        root = Path(home)
        sessions = root / "sessions"
        sessions.mkdir()
        database = root / "state_5.sqlite"
        with sqlite3.connect(database) as db:
            db.execute("""CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, rollout_path TEXT,
                tokens_used INTEGER, recency_at_ms INTEGER, archived INTEGER DEFAULT 0,
                source TEXT DEFAULT 'vscode', thread_source TEXT DEFAULT 'user',
                originator TEXT DEFAULT 'Codex Desktop', agent_role TEXT, agent_path TEXT,
                agent_nickname TEXT, attached_thread_parent_id TEXT)""")

            def add(large=False, **metadata):
                ident = str(uuid4())
                path = sessions / f"rollout-{ident}.jsonl"
                with path.open("wb") as out:
                    out.write((records[0].replace(original_id, ident) + "\n").encode())
                    if large:
                        out.seek(212592398)
                        out.write(b"\n")
                    out.write(("\n".join(records[1:]).replace(original_id, ident) + "\n").encode())
                db.execute("INSERT INTO threads(id,title,rollout_path,tokens_used,recency_at_ms) "
                           "VALUES(?,?,?,?,?)", (ident, "Identical titles", str(path), 999, 1))
                for column, value in metadata.items():
                    db.execute(f"UPDATE threads SET {column}=? WHERE id=?", (value, ident))
                return ident

            a, b = add(large=True), add()
            rejected = [add(source='{"subagent":{"other":"guardian"}}', recency_at_ms=999),
                        add(agent_role="reviewer"), add(agent_path="/root/worker"),
                        add(thread_source="internal"), add(archived=1),
                        add(attached_thread_parent_id=a)]
        db.close()
        output = root / "probe.json"
        env = dict(os.environ, CODEX_HOME=home)

        def probe(command, *ids):
            subprocess.run(["dotnet", str(dll), command, str(output), *ids,
                            "--sessions", str(sessions)], env=env, check=True, timeout=30)
            return json.loads(output.read_text(encoding="utf-8-sig"))

        def field(record, name):
            return {key.lower(): value for key, value in record.items()}[name.lower()]

        empty = probe("--probe")
        assert field(empty, "ThreadId") == "" and field(empty, "TotalTokens") == 0
        # A real writer keeps its reserved lock across the read-only monitor calls.
        with sqlite3.connect(database, timeout=0) as writer:
            writer.execute("BEGIN IMMEDIATE")
            pair = probe("--thread-switch-probe", a, b)
            writer.execute("UPDATE threads SET recency_at_ms=2 WHERE id=?", (a,))
        writer.close()
        for side, ident in [("FirstSnapshot", a), ("SecondSnapshot", b)]:
            snapshot = field(pair, side)
            assert field(snapshot, "ThreadId") == ident and field(snapshot, "TotalTokens") == 221700
        for ident in rejected:
            result = field(probe("--thread-switch-probe", a, ident), "SecondSnapshot")
            assert field(result, "ThreadId") == "" and field(result, "TotalTokens") == 0
        manual = subprocess.run(["dotnet", str(dll), "--no-overlay", "--duration", "1",
                                 "--sessions", str(sessions), "--thread", a],
                                env=env, check=True, capture_output=True, timeout=30)
        assert a.encode() in manual.stdout and b"forced" in manual.stdout
    print("Session selection passed: idle switch, ambiguous recency, six internal/archived cases,"
          " stale metrics, explicit pin and 212 MB rollout.")


if __name__ == "__main__":
    main()
