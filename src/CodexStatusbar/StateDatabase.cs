using System.Runtime.InteropServices;

namespace CodexStatusbar;

/// <summary>One row of <c>~/.codex/state_5.sqlite</c>'s <c>threads</c> table.</summary>
internal sealed record StateThreadRecord(
    string Id,
    string? Title,
    string? RolloutPath,
    long TokensUsed, string Project);

/// <summary>
/// Read-only access to <c>state_5.sqlite</c> through the SQLite library that ships with Windows
/// (<c>winsqlite3.dll</c>), so the overlay needs no extra package and never writes to Codex state.
/// Used only for restart recovery / thread titles and as a fallback when the IPC route is down.
/// </summary>
internal static class StateDatabase
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadOnly = 0x00000001;

    private static string DisplayTitle(string databasePath) =>
        Query(databasePath, "SELECT 1 FROM pragma_table_info('threads') WHERE name='name'", null, _ => true).Count > 0
            ? "COALESCE(NULLIF(TRIM(name), ''), title)" : "title";

    public static string ResolvePath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return Path.Combine(codexHome, "state_5.sqlite");
    }

    /// <summary>
    /// Resolves the database that belongs to a given sessions root, so an overridden
    /// <c>--sessions</c> path (an isolated test home, a second Codex profile) never falls back to the
    /// default home's database and reports another profile's conversation.
    /// </summary>
    public static string ResolvePathForSessionsRoot(string? sessionsRoot)
    {
        if (!string.IsNullOrWhiteSpace(sessionsRoot))
        {
            var home = Path.GetDirectoryName(sessionsRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(home))
            {
                var candidate = Path.Combine(home, "state_5.sqlite");
                return candidate;
            }
        }

        return ResolvePath();
    }

    public static StateThreadRecord? TryReadThread(string databasePath, string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return QuerySingle(
            databasePath,
            $"SELECT id, {DisplayTitle(databasePath)}, rollout_path, tokens_used, cwd, git_origin_url FROM threads WHERE id = $id LIMIT 1",
            threadId);
    }

    // Unknown schemas fail closed. Only an independently observed route supplies the ID.
    public static StateThreadRecord? TryReadDesktopUserThread(string databasePath, string threadId) =>
        QuerySingle(databasePath,
            $"SELECT id, {DisplayTitle(databasePath)}, rollout_path, tokens_used, cwd, git_origin_url FROM threads WHERE id = $id AND archived = 0 " +
            "AND source = 'vscode' AND (thread_source IS NULL OR thread_source = 'user') " +
            "AND (originator IS NULL OR originator = 'Codex Desktop') " +
            "AND agent_role IS NULL AND agent_path IS NULL AND agent_nickname IS NULL " +
            "AND attached_thread_parent_id IS NULL LIMIT 1", threadId);

    private const string UserChat = "source = 'vscode' AND "
        + "(thread_source IS NULL OR thread_source IN ('user', 'chatgpt_handoff')) "
        + "AND agent_role IS NULL AND agent_path IS NULL AND agent_nickname IS NULL "
        + "AND attached_thread_parent_id IS NULL";

    public static IReadOnlyList<ChatEntry> ReadChatCatalog(string databasePath) =>
        Query(databasePath, $"SELECT id, {DisplayTitle(databasePath)}, cwd, git_origin_url, " +
            "COALESCE(updated_at_ms, updated_at * 1000, 0), archived FROM threads WHERE " +
            UserChat + " ORDER BY COALESCE(updated_at_ms, updated_at * 1000, 0) DESC, id", null,
            statement => new ChatEntry(
                ReadText(statement, 0) ?? string.Empty,
                ReadText(statement, 1) is { Length: > 0 } title ? title : "Untitled chat",
                ReadText(statement, 2), ReadText(statement, 3),
                sqlite3_column_int64(statement, 4), sqlite3_column_int64(statement, 5) != 0));

    public static StateThreadRecord? TryReadSelectableThread(string databasePath, string id) =>
        QuerySingle(databasePath, $"SELECT id, {DisplayTitle(databasePath)}, rollout_path, tokens_used, cwd, git_origin_url FROM threads WHERE id = $id AND {UserChat} LIMIT 1", id);

    private static StateThreadRecord? QuerySingle(string databasePath, string sql, string? parameter) =>
        Query(databasePath, sql, parameter, statement => new StateThreadRecord(
            ReadText(statement, 0) ?? string.Empty, ReadText(statement, 1), ReadText(statement, 2),
            sqlite3_column_int64(statement, 3), new ChatEntry(string.Empty, string.Empty,
                ReadText(statement, 4), ReadText(statement, 5), 0, false).Project)).FirstOrDefault();

    private static IReadOnlyList<T> Query<T>(string databasePath, string sql, string? parameter, Func<IntPtr, T> readRow)
    {
        if (!File.Exists(databasePath))
        {
            return [];
        }

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        var databasePathPointer = IntPtr.Zero;
        var sqlPointer = IntPtr.Zero;
        var parameterPointer = IntPtr.Zero;
        try
        {
            databasePathPointer = Marshal.StringToCoTaskMemUTF8(databasePath);
            if (sqlite3_open_v2(databasePathPointer, out database, SqliteOpenReadOnly, IntPtr.Zero) != SqliteOk)
            {
                return [];
            }

            sqlPointer = Marshal.StringToCoTaskMemUTF8(sql);
            if (sqlite3_prepare_v2(database, sqlPointer, -1, out statement, IntPtr.Zero) != SqliteOk)
            {
                return [];
            }

            if (parameter is not null)
            {
                parameterPointer = Marshal.StringToCoTaskMemUTF8(parameter);
                if (sqlite3_bind_text(statement, 1, parameterPointer, -1, IntPtr.Zero) != SqliteOk)
                {
                    return [];
                }
            }

            var results = new List<T>();
            int status;
            while ((status = sqlite3_step(statement)) == SqliteRow)
                results.Add(readRow(statement));
            return status == SqliteDone ? results : [];
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or AccessViolationException)
        {
            // No usable SQLite on this machine: the overlay simply runs without the state-db fallback.
            return [];
        }
        finally
        {
            if (statement != IntPtr.Zero)
            {
                _ = sqlite3_finalize(statement);
            }

            if (database != IntPtr.Zero)
            {
                _ = sqlite3_close_v2(database);
            }

            FreeString(databasePathPointer);
            FreeString(sqlPointer);
            FreeString(parameterPointer);
        }
    }

    private static void FreeString(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static string? ReadText(IntPtr statement, int column)
    {
        var pointer = sqlite3_column_text(statement, column);
        return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_open_v2(IntPtr filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_prepare_v2(IntPtr database, IntPtr sql, int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_bind_text(IntPtr statement, int index, IntPtr value, int byteCount, IntPtr destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern long sqlite3_column_int64(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_close_v2(IntPtr database);
}