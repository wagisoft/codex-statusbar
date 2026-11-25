using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Text;

namespace CodexStatusbar;

internal static class ActiveChatProbe
{
    public static object Read(IntPtr window, string sessionRoot, string? captureDirectory = null)
    {
        var foreground = window == IntPtr.Zero;
        if (foreground)
        {
            if (!CodexWindowLocator.TryGetForegroundCodexTarget(out var target))
                return new { Status = "no-verified-codex-foreground" };
            window = target.HostWindow.Handle;
        }
        GetWindowThreadProcessId(window, out var process);
        if (process == 0 || !CodexWindowLocator.IsCodexDesktopProcess(process))
            return new { Status = "not-a-codex-window" };

        var database = StateDatabase.ResolvePathForSessionsRoot(sessionRoot);
        var header = new DesktopChatHeader().Read(window);
        var headerResult = header.Title is null ? (null, header.Reason) : TokenLogMonitor.ResolveHeader(database, header.Title);
        using var monitor = new TokenLogMonitor(sessionRoot);
        monitor.ApplyHeaderObservation(headerResult.Item1, headerResult.Item2);
        var verified = monitor.Poll();
        var caption = new StringBuilder(1024);
        GetWindowText(window, caption, caption.Capacity);
        var nativeTitle = caption.ToString();
        var catalog = StateDatabase.ReadChatCatalog(database);
        var candidates = new List<object>();
        var properties = new Dictionary<string, int>();
        var knownNames = catalog.GroupBy(chat => chat.Title, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        try
        {
            var root = AutomationElement.FromHandle(window);
            var bounds = root.Current.BoundingRectangle;
            string? documentName = null;
            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<(AutomationElement Element, int Depth)>();
            queue.Enqueue((root, 0));
            var inspected = 0;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (queue.Count > 0 && inspected < 900 && elapsed.ElapsedMilliseconds < 1700)
            {
                var (element, depth) = queue.Dequeue();
                inspected++;
                var current = element.Current;
                if (current.IsOffscreen && depth > 0) continue;
                var control = current.ControlType;
                var type = control.ProgrammaticName;
                properties[type] = properties.GetValueOrDefault(type) + 1;
                if (control == ControlType.Document && current.AutomationId == "RootWebArea") documentName = current.Name;
                var selectionItem = element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var item);
                var selected = item is SelectionItemPattern selection && selection.Current.IsSelected;
                var selectionContainer = element.TryGetCurrentPattern(SelectionPattern.Pattern, out _);
                var matches = knownNames.GetValueOrDefault(current.Name ?? "");
                string? route = null;
                if ((control == ControlType.Document || control == ControlType.Hyperlink)
                    && element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && value is ValuePattern document)
                    route = DesktopDocumentRoute.Parse(document.Current.Value);
                if (selected || selectionContainer || matches > 0 || control == ControlType.Document || route is not null)
                    candidates.Add(new { type, depth, selectionItem, selected, selectionContainer, matches, route,
                        automationId = !string.IsNullOrEmpty(current.AutomationId),
                        helpText = !string.IsNullOrEmpty(current.HelpText),
                        itemStatus = !string.IsNullOrEmpty(current.ItemStatus),
                        ancestors = control == ControlType.Text && current.Name == documentName
                            ? Ancestors(walker, element, bounds.Y).ToArray() : null,
                        documentNameMatches = current.Name == documentName,
                        x = (int)(current.BoundingRectangle.X - bounds.X),
                        y = (int)(current.BoundingRectangle.Y - bounds.Y),
                        nativeTitleMatches = current.Name == nativeTitle,
                        documentMatch = control == ControlType.Document
                            ? catalog.Where(chat => chat.Title == current.Name).Select(chat => new { chat.Id, chat.Archived }).ToArray()
                            : null,
                        isRootWebArea = current.AutomationId == "RootWebArea",
                        expectedOilEmpireName = current.Name == "Implementierung" });
                if (depth >= 23) continue;
                for (var child = walker.GetFirstChild(element); child is not null && queue.Count < 900;
                    child = walker.GetNextSibling(child))
                    queue.Enqueue((child, depth + 1));
            }
            if (captureDirectory is not null && verified is { HasUsage: true })
                RenderProbe.CaptureSavedChat(captureDirectory, OverlaySettings.Load(),
                    new ChatBinding(ChatBindingMode.Automatic, null, false), verified);
            return new { Status = "sampled", foreground, inspected, elapsedMs = elapsed.ElapsedMilliseconds,
                truncated = queue.Count > 0, headerVerifiedId = headerResult.Item1, headerDecision = headerResult.Item2,
                rolloutVerification = verified?.Source, metricsAvailable = verified?.HasUsage == true, properties, candidates };
        }
        catch (Exception error) when (error is COMException or ElementNotAvailableException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            return new { Status = "uia-unavailable", Error = error.GetType().Name };
        }
    }

    private static IEnumerable<object> Ancestors(TreeWalker walker, AutomationElement element, double top)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            var parent = walker.GetParent(element);
            if (parent is null) yield break;
            var p = parent.Current;
            yield return new { role = p.ControlType.ProgrammaticName, y = (int)(p.BoundingRectangle.Y - top),
                height = (int)p.BoundingRectangle.Height, hasName = !string.IsNullOrEmpty(p.Name),
                hasId = !string.IsNullOrEmpty(p.AutomationId) };
            element = parent;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
