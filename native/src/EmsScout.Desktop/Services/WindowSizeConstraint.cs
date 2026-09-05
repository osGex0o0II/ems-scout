using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Windows.Graphics;

namespace EmsScout.Desktop.Services;

public static class WindowSizeConstraint
{
    public const int MinimumClientWidth = 1200;
    public const int MinimumClientHeight = 800;

    public static SizeInt32 ScaleSizeForWindow(Window window, SizeInt32 desiredSize)
    {
        var handle = WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96u : dpi) / 96d;
        return new SizeInt32(
            (int)Math.Round(desiredSize.Width * scale),
            (int)Math.Round(desiredSize.Height * scale));
    }

    private const int GwlWndProc = -4;
    private const int SwRestore = 9;
    private const uint WmGetMinMaxInfo = 0x0024;
    private static readonly Dictionary<nint, WindowHook> Hooks = [];

    public static void Attach(Window window)
    {
        var handle = WindowNative.GetWindowHandle(window);
        if (Hooks.ContainsKey(handle))
        {
            return;
        }

        var hook = new WindowHook(handle);
        hook.Attach();
        Hooks.Add(handle, hook);
    }

    public static void Restore(Window window)
    {
        ShowWindow(WindowNative.GetWindowHandle(window), SwRestore);
    }

    private sealed class WindowHook
    {
        private readonly nint _handle;
        private readonly WndProcDelegate _wndProc;
        private nint _previousWndProc;

        public WindowHook(nint handle)
        {
            _handle = handle;
            _wndProc = WndProc;
        }

        public void Attach()
        {
            _previousWndProc = SetWindowLongPtr(
                _handle,
                GwlWndProc,
                Marshal.GetFunctionPointerForDelegate(_wndProc));
            if (_previousWndProc == 0)
            {
                throw new InvalidOperationException("无法设置主窗口尺寸限制。");
            }
        }

        private nint WndProc(nint windowHandle, uint message, nint wParam, nint lParam)
        {
            if (message == WmGetMinMaxInfo && lParam != 0)
            {
                var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var dpi = GetDpiForWindow(_handle);
                var scale = (dpi == 0 ? 96u : dpi) / 96d;
                limits.MinTrackSize.X = (int)Math.Round(MinimumClientWidth * scale);
                limits.MinTrackSize.Y = (int)Math.Round(MinimumClientHeight * scale);
                Marshal.StructureToPtr(limits, lParam, false);
                return 0;
            }

            return CallWindowProc(_previousWndProc, windowHandle, message, wParam, lParam);
        }
    }

    private delegate nint WndProcDelegate(nint windowHandle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CallWindowProc(
        nint previousWndProc,
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);
}
