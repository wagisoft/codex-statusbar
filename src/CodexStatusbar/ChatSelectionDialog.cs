namespace CodexStatusbar;

internal sealed class ChatSelectionDialog : Form
{
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Search titles, projects or folders" };
    private readonly CheckBox _archived = new() { Text = "Show archived chats", AutoSize = true };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        ShowItemToolTips = true
    };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _select = new() { Text = "Use this chat", AutoSize = true, Enabled = false };
    private IReadOnlyList<ChatEntry> _chats = [];
    private readonly IReadOnlyList<string> _recent;
    private readonly string? _initial;
    private bool _loading = true;
    public ChatEntry? Selection { get; private set; }

    public ChatSelectionDialog(string databasePath, IReadOnlyList<string> recent, string? initial)
    {
        _recent = recent;
        _initial = initial;
        Text = "Codex Statusbar · Choose chat";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(830, 535);
        MinimumSize = new Size(580, 360);
        ShowInTaskbar = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = "Choose the chat whose tokens Statusbar shows. This leaves the open Codex chat unchanged.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);
        var filters = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.Controls.Add(_search, 0, 0);
        filters.Controls.Add(_archived, 1, 0);
        layout.Controls.Add(filters, 0, 1);
        _search.AccessibleName = "Search chats";
        _list.AccessibleName = "Available chats";
        _list.Columns.Add("Chat", 365);
        _list.Columns.Add("Project", 193);
        _list.Columns.Add("Last activity", 193);
        layout.Controls.Add(_list, 0, 2);

        var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(_select, 2, 0);
        layout.Controls.Add(footer, 0, 3);
        AcceptButton = _select;
        CancelButton = cancel;
        _search.TextChanged += (_, _) => RefreshList();
        _archived.CheckedChanged += (_, _) => RefreshList();
        _list.SelectedIndexChanged += (_, _) => _select.Enabled = _list.SelectedItems.Count == 1;
        _list.ItemActivate += (_, _) => AcceptSelection();
        _select.Click += (_, _) => AcceptSelection();
        Shown += async (_, _) =>
        {
            try { _chats = await Task.Run(() => StateDatabase.ReadChatCatalog(databasePath)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (IsDisposed) return;
            _loading = false;
            RefreshList();
        };
        RefreshList();
    }

    private void RefreshList()
    {
        var selectedId = _list.SelectedItems.Count == 1
            ? ((ChatEntry)_list.SelectedItems[0].Tag!).Id : _initial;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _list.Groups.Clear();
            var search = _search.Text.Trim();
            var visible = _chats.Where(chat => (_archived.Checked || !chat.Archived) && chat.Matches(search)).ToArray();
            var recent = _recent.Select(id => visible.FirstOrDefault(chat => chat.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .OfType<ChatEntry>().ToArray();
            AddGroup("Recently selected", recent, selectedId);
            var recentIds = recent.Select(chat => chat.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var group in visible.Where(chat => !recentIds.Contains(chat.Id))
                .GroupBy(chat => chat.Project, StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
                AddGroup(group.Key, group, selectedId);
            _status.Text = _loading ? "Reading local chat index…" : visible.Length == 0
                ? "No matching chats in the local index." : $"{visible.Length:N0} chats";
            _select.Enabled = _list.SelectedItems.Count == 1;
        }
        finally { _list.EndUpdate(); }
    }

    private void AddGroup(string title, IEnumerable<ChatEntry> chats, string? selectedId)
    {
        var group = new ListViewGroup(title);
        foreach (var chat in chats)
        {
            if (group.ListView is null) _list.Groups.Add(group);
            var item = new ListViewItem([chat.Archived ? chat.Title + " [archived]" : chat.Title,
                chat.Project, FormatActivity(chat.UpdatedMilliseconds)], group)
            {
                Tag = chat,
                ToolTipText = chat.WorkingDirectory ?? string.Empty
            };
            _list.Items.Add(item);
            if (chat.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
        }
    }

    private static string FormatActivity(long milliseconds) =>
        milliseconds is > 0 and <= 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime().ToString("g") : "—";

    private void AcceptSelection()
    {
        if (_list.SelectedItems.Count != 1) return;
        Selection = (ChatEntry)_list.SelectedItems[0].Tag!;
        DialogResult = DialogResult.OK;
    }
}
