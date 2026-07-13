using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using PsGameTranslator.Core.Models;

namespace PsGameTranslator.Capture;

public sealed class WindowCaptureService : IWindowCaptureService
{
    private readonly ILogger<WindowCaptureService> _logger;

    // EnumWindows returns every visible top-level window, which drowns out the
    // game the user actually wants under desktop/shell/IDE/browser clutter.
    // Instead of an allowlist (which only matched a couple of hardcoded titles
    // and missed every real PC game, e.g. Outlast), block known non-game
    // processes/titles by name and keep everything else — this naturally
    // includes any PC game window plus PS Remote Play.
    private static readonly string[] BlockedProcessNames =
    [
        "explorer", "dwm", "ApplicationFrameHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "TextInputHost", "SystemSettings", "Taskmgr", "ScreenClippingHost", "SnippingTool",
        "devenv", "Code", "notepad", "notepad++", "cmd", "powershell", "pwsh", "WindowsTerminal",
        "Discord", "Spotify", "OneDrive", "steam", "steamwebhelper",
        // AI assistants / launcher-style utilities that float above games.
        "claude", "Microsoft.CmdPal.UI", "ChatGPT",
        // GPU vendor overlays and audio/utility control panels — never the game itself.
        "NVIDIA Overlay", "NVIDIA Share", "nvsphelper64", "RtkUWP", "RadeonSoftware",
        "RivaTuner Statistics Server", "MSIAfterburner", "EpicGamesLauncher", "GalaxyClient",
        "PsGameTranslator.App", // never capture ourselves
    ];

    private static readonly string[] BlockedTitleExact =
    [
        "Program Manager", "Settings", "Windows Input Experience", "Microsoft Text Input Application",
    ];

    // Browsers are excluded by default (a browser window is almost never the
    // "game"), except a YouTube tab — kept as a lightweight test target when
    // no real game/Remote Play session is running.
    private static readonly string[] BrowserProcessNames = ["chrome", "msedge", "firefox", "brave", "opera"];

    public WindowCaptureService(ILogger<WindowCaptureService> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<CapturedWindow>> GetAvailableWindowsAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => EnumerateWindows(cancellationToken), cancellationToken);

    public Task<byte[]> CaptureAsync(
        CapturedWindow window,
        CaptureRegion? region = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => CaptureWindowToPng(window), cancellationToken);

    private byte[] CaptureWindowToPng(CapturedWindow window)
    {
        var bytes = TryCaptureWithWgc(window) ?? CaptureWithPrintWindow(window);

        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "latest_capture_full.png"), bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save debug full capture image");
        }

        return bytes;
    }

    /// <summary>Windows.Graphics.Capture reads the real DWM-composited surface for
    /// the window, so it renders GPU-accelerated fullscreen/borderless games
    /// correctly — unlike PrintWindow, which many such games answer with a stale
    /// or blurred cached frame. Returns null (never throws) so the caller can
    /// silently fall back to PrintWindow on unsupported OS builds or any capture
    /// failure (closed window, no frame in time, etc.).</summary>
    private byte[]? TryCaptureWithWgc(CapturedWindow window)
    {
        if (!WgcFrameCapture.IsSupported()) return null;
        try
        {
            var bytes = WgcFrameCapture.CaptureWindowPng(window.Handle);
            _logger.LogInformation("Captured window '{Title}' via Windows.Graphics.Capture", window.Title);
            return bytes;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "WGC capture failed for handle {Handle}, falling back to PrintWindow", window.Handle);
            return null;
        }
    }

    private byte[] CaptureWithPrintWindow(CapturedWindow window)
    {
        if (!GetWindowRect(window.Handle, out var rect))
            throw new InvalidOperationException(
                $"Could not get bounds for window handle {window.Handle}.");

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException(
                $"Window {window.Handle} has invalid dimensions: {width}×{height}.");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            nint hdc = graphics.GetHdc();
            try
            {
                // PW_RENDERFULLCONTENT captures layered/DWM-composed content correctly.
                bool success = PrintWindow(window.Handle, hdc, PW_RENDERFULLCONTENT);
                if (!success)
                    _logger.LogWarning(
                        "PrintWindow returned false for handle {Handle}", window.Handle);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        _logger.LogInformation(
            "Captured window '{Title}' ({Width}×{Height}) via PrintWindow", window.Title, width, height);
        return stream.ToArray();
    }

    private IReadOnlyList<CapturedWindow> EnumerateWindows(CancellationToken cancellationToken)
    {
        var windows = new List<CapturedWindow>();

        EnumWindows((windowHandle, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            if (!IsWindowVisible(windowHandle))
                return true;

            var title = GetWindowTitle(windowHandle);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            GetWindowThreadProcessId(windowHandle, out var processId);
            GetWindowRect(windowHandle, out var rect);

            windows.Add(new CapturedWindow
            {
                Handle = windowHandle,
                Title = title,
                ProcessId = unchecked((int)processId),
                ProcessName = GetProcessName(processId),
                Left = rect.Left,
                Top = rect.Top,
                Width = Math.Max(0, rect.Right - rect.Left),
                Height = Math.Max(0, rect.Bottom - rect.Top),
            });

            return true;
        }, 0);

        cancellationToken.ThrowIfCancellationRequested();

        var result = windows
            .Where(window => !BlockedProcessNames.Contains(window.ProcessName, StringComparer.OrdinalIgnoreCase))
            .Where(window => !BlockedTitleExact.Contains(window.Title, StringComparer.OrdinalIgnoreCase))
            .Where(window => !BrowserProcessNames.Contains(window.ProcessName, StringComparer.OrdinalIgnoreCase) ||
                              window.Title.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
            .Where(window => window.Width > 200 && window.Height > 150)
            .OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _logger.LogInformation(
            "Enumerated {WindowCount} matching windows (of {TotalCount} visible top-level windows)",
            result.Length, windows.Count);
        return result;
    }

    private static string GetWindowTitle(nint windowHandle)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length <= 0)
            return string.Empty;

        var buffer = new StringBuilder(length + 1);
        return GetWindowText(windowHandle, buffer, buffer.Capacity) > 0
            ? buffer.ToString().Trim()
            : string.Empty;
    }

    private string GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            return process.ProcessName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            _logger.LogDebug(exception, "Could not read process name for process {ProcessId}", processId);
            return string.Empty;
        }
    }

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint windowHandle, nint hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
