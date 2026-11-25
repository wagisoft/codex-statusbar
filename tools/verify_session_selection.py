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
    legacy_records = fixture.with_name("fixture-legacy.jsonl").read_text(encoding="utf-8").splitlines()
    with tempfile.TemporaryDirectory() as home:
        root = Path(home)
        sessions = root / "sessions"
        sessions.mkdir()
        database = root / "state_5.sqlite"
        with sqlite3.connect(database) as db:
            db.execute("""CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, rollout_path TEXT,
                tokens_used INTEGER, recency_at_ms INTEGER, archived INTEGER DEFAULT 0,
                cwd TEXT, git_origin_url TEXT, updated_at INTEGER, updated_at_ms INTEGER,
                source TEXT DEFAULT 'vscode', thread_source TEXT DEFAULT 'user',
                originator TEXT DEFAULT 'Codex Desktop', agent_role TEXT, agent_path TEXT,
                agent_nickname TEXT, attached_thread_parent_id TEXT)""")

            def add(large=False, legacy=False, **metadata):
                ident = str(uuid4())
                path = sessions / f"rollout-{ident}.jsonl"
                with path.open("wb") as out:
                    out.write((records[0].replace(original_id, ident) + "\n").encode())
                    if large:
                        out.seek(212592398)
                        out.write(b"\n")
                    usage = legacy_records[1:] if legacy else records[1:]
                    out.write(("\n".join(usage).replace(original_id, ident) + "\n").encode())
                db.execute("INSERT INTO threads(id,title,rollout_path,tokens_used,recency_at_ms) "
                           "VALUES(?,?,?,?,?)", (ident, "Identical titles", str(path), 999, 1))
                for column, value in metadata.items():
                    db.execute(f"UPDATE threads SET {column}=? WHERE id=?", (value, ident))
                return ident

            a = add(large=True, title="Economy Core", cwd=r"E:\workspaces\OilEmpire\economy-core",
                    git_origin_url="https://example.test/project/_git/OilEmpire", updated_at_ms=1700000001000)
            b = add(legacy=True, title="Statusbar", cwd=r"E:\workspaces\codex-statusbar", updated_at=1700000002)
            # Codex on Windows stores this spelling even when the configured root is a DOS path.
            db.execute("UPDATE threads SET rollout_path=? || rollout_path WHERE id=?",
                       (chr(92) * 2 + "?" + chr(92), a))
            archived = add(archived=1)
            missing = add(title="Missing")
            (sessions / f"rollout-{missing}.jsonl").unlink()
            rejected = [add(source='{"subagent":{"other":"guardian"}}', recency_at_ms=999),
                        add(agent_role="reviewer"), add(agent_path="/root/worker"),
                        add(thread_source="internal"), archived,
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
            assert field(snapshot, "ThreadId") == ident and field(snapshot, "TotalTokens") == (221700 if ident == a else 5699084)
        for ident in rejected:
            result = field(probe("--thread-switch-probe", a, ident), "SecondSnapshot")
            assert field(result, "ThreadId") == "" and field(result, "TotalTokens") == 0
        catalog_probe = probe("--chat-selection-probe", a, b, missing)
        catalog = field(catalog_probe, "Catalog")
        catalog_ids = {field(row, "Id") for row in catalog}
        assert catalog_ids == {a, b, archived, missing}
        assert field(catalog[0], "Id") == b and field(catalog[1], "Id") == a
        assert field(catalog[1], "Project") == "OilEmpire"
        assert field(catalog[0], "Project") == "codex-statusbar"
        assert field(next(row for row in catalog if field(row, "Id") == archived), "Archived")
        for side, ident in [("First", a), ("Second", b)]:
            selected = field(catalog_probe, side)
            assert field(selected, "Source") == "manual" and field(selected, "ThreadId") == ident
            assert field(selected, "TotalTokens") == (221700 if ident == a else 5699084)
        missing_snapshot = field(catalog_probe, "Missing")
        assert field(missing_snapshot, "ThreadId") == "" and field(missing_snapshot, "TotalTokens") == 0
        assert field(missing_snapshot, "Source") == "manual-session-unavailable"
        assert "rollout-missing-or-inaccessible" in field(missing_snapshot, "Warnings")
        # Exercise the same persisted binding on fresh processes, without --thread.
        settings = root / "settings.json"
        def saved_run(ident):
            settings.write_text(json.dumps({"chatMode": "Manual", "manualChatId": ident}), encoding="utf-8")
            return subprocess.check_output(["dotnet", str(dll), "--no-overlay", "--duration", "1",
                                            "--sessions", str(sessions), "--settings", str(settings)],
                                           env=env, timeout=30)
        for ident in (a, a, b):
            report = saved_run(ident)
            assert ident.encode() in report and b"rollout-tail" in report
        assert b"thread-excluded-by-metadata" in saved_run(rejected[0])
        with sqlite3.connect(database) as db:
            db.execute("UPDATE threads SET rollout_path=? WHERE id=?",
                       (str(root / "sessions-backup" / f"rollout-{missing}.jsonl"), missing))
        db.close()
        assert b"rollout-outside-session-root" in saved_run(missing)
        # Older schemas use title. A renamed chat on the current schema carries its name by ID.
        with sqlite3.connect(database) as db:
            db.execute("ALTER TABLE threads ADD COLUMN name TEXT")
            db.execute("UPDATE threads SET name=? WHERE id=?", ("Renamed chat", a))
        db.close()
        renamed = probe("--chat-selection-probe", a, b, missing)
        assert field(field(renamed, "First"), "Title") == "Renamed chat"
        assert field(field(renamed, "Second"), "Title") == "Statusbar"
        assert field(field(renamed, "First"), "Project") == "OilEmpire"
        manual = subprocess.run(["dotnet", str(dll), "--no-overlay", "--duration", "1",
                                 "--sessions", str(sessions), "--thread", a],
                                env=env, check=True, capture_output=True, timeout=30)
        assert a.encode() in manual.stdout and b"forced" in manual.stdout
    print("Session selection passed: idle switch, ambiguous recency, six internal/archived cases,"
          " manual catalog and switching, missing-file placeholders, explicit pin and 212 MB rollout.")


if __name__ == "__main__":
    main()
