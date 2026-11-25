namespace CodexStatusbar;

internal sealed record ChatEntry(string Id, string Title, string? WorkingDirectory, [property: System.Text.Json.Serialization.JsonIgnore] string? Origin,
    long UpdatedMilliseconds, bool Archived)
{
    public string Project
    {
        get
        {
            if (Uri.TryCreate(Origin, UriKind.Absolute, out var remote) && remote.Scheme is "https" or "http" or "ssh" or "git")
            {
                var name = Uri.UnescapeDataString(remote.AbsolutePath.TrimEnd('/').Split('/')[^1]);
                if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }

            var folder = WorkingDirectory?.TrimEnd('\\', '/');
            return string.IsNullOrWhiteSpace(folder) ? "No project" : folder.Split('\\', '/')[^1];
        }
    }

    public bool Matches(string search) => Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Project.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || (WorkingDirectory?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);
}

internal enum ChatBindingMode { Automatic, Manual }

internal readonly record struct ChatBinding(ChatBindingMode Mode, string? ThreadId, bool FromCommandLine)
{
    public static ChatBinding Resolve(OverlaySettings settings, string? commandLineId) =>
        !string.IsNullOrWhiteSpace(commandLineId)
            ? new(ChatBindingMode.Manual, commandLineId, true)
            : new(settings.ChatMode, settings.ChatMode == ChatBindingMode.Manual ? settings.ManualChatId : null, false);
}
