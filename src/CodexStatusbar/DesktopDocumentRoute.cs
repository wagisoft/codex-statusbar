using System.Runtime.InteropServices;
using Accessibility;

namespace CodexStatusbar;

internal static class DesktopDocumentRoute
{
    // Read the Chromium renderer's outer document only. Embedded pages and sidebar links
    // are separate accessible objects and cannot establish the main window's route.
    public static string? Read(out bool singleForegroundHost)
    {
        singleForegroundHost = false;
        if (!CodexWindowLocator.TryGetForegroundCodexTarget(out var target))
            return null;

        var window = target.HostWindow.Handle;
        if (window != GetForegroundWindow())
            return null;
        singleForegroundHost = target.IsOnlyChatWindow;
        var renderer = FindWindowEx(window, IntPtr.Zero, "Chrome_RenderWidgetHostHWND", null);
        if (renderer == IntPtr.Zero ||
            FindWindowEx(window, renderer, "Chrome_RenderWidgetHostHWND", null) != IntPtr.Zero)
            return null;

        var iid = typeof(IAccessible).GUID;
        try
        {
            if (AccessibleObjectFromWindow(renderer, unchecked((uint)-4), ref iid, out var element) != 0)
                return null;

            for (var depth = 0; depth < 6 && element is not null; depth++)
            {
                // ROLE_SYSTEM_DOCUMENT. Stop before descending into any document content.
                if (element.get_accRole(0) is int role && role == 0x0f)
                    return GetForegroundWindow() == window ? Parse(element.get_accValue(0)) : null;
                if (element.accChildCount != 1)
                    break;
                element = element.get_accChild(1) as IAccessible;
            }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException
            or NotImplementedException or UnauthorizedAccessException)
        {
            // The renderer may have closed or disabled accessibility during navigation.
        }
        return null;
    }

    internal static string? Parse(string? url)
    {
        foreach (var prefix in new[] { "app://-/local/", "app://-/index.html#/local/", "app://-/#/local/" })
        {
            if (url?.StartsWith(prefix, StringComparison.Ordinal) == true
                && Guid.TryParseExact(url.AsSpan(prefix.Length), "D", out var id)
                && url.Length == prefix.Length + 36)
                return id.ToString("D");
        }
        return null;
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
}
