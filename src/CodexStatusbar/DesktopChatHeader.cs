using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CodexStatusbar;

internal sealed class DesktopChatHeader
{
    private static readonly Condition DocumentCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea"));
    private static readonly Condition HeaderCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar),
        new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Chat-Symbolleiste"),
            new PropertyCondition(AutomationElement.NameProperty, "Chat toolbar")));

    private IntPtr _window;
    private AutomationElement? _document;
    private AutomationElement? _header;
    private long _retryAfter;

    public (string? Title, string Reason) Read(IntPtr window)
    {
        if (window != _window)
        {
            _window = window;
            Reset();
            _retryAfter = 0;
        }
        try
        {
            if (_document is null || _header is null)
            {
                if (Environment.TickCount64 < _retryAfter) return (null, "uia-header-unavailable");
                _retryAfter = Environment.TickCount64 + 1500;
                var root = AutomationElement.FromHandle(window);
                _document = root.FindFirst(TreeScope.Descendants, DocumentCondition);
                _header = _document?.FindFirst(TreeScope.Descendants, HeaderCondition);
                if (_header is null || _document is null) return (null, "uia-header-unavailable");
            }

            var title = _document.Current.Name;
            if (string.IsNullOrWhiteSpace(title) || _document.Current.IsOffscreen || _header.Current.IsOffscreen)
                return (null, "uia-header-unavailable");
            var labels = _header.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            var matches = 0;
            foreach (AutomationElement label in labels)
                if (!label.Current.IsOffscreen && label.Current.Name == title) matches++;
            if (_document.Current.Name != title) return (null, "uia-header-changing");
            return matches switch
            {
                1 => (title, "uia-chat-header"),
                > 1 => (null, "ambiguous-chat"),
                _ => (null, "uia-header-title-unconfirmed")
            };
        }
        catch (Exception error) when (error is COMException or ElementNotAvailableException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            Reset();
            _retryAfter = Environment.TickCount64 + 1500;
            return (null, "uia-header-unavailable");
        }
    }

    private void Reset()
    {
        _document = null;
        _header = null;
    }
}
