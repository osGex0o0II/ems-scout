using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using Windows.Graphics;
using EmsScout.Application.Settings;

namespace EmsScout.Desktop.Services;

public static class WindowSizeConstraint
{
    public const int CurrentPlacementVersion = 2;
    public const int MinimumClientWidth = 1040;
    public const int MinimumClientHeight = 680;
    public const int InitialClientWidth = MinimumClientWidth;
    public const int InitialClientHeight = MinimumClientHeight;
    private const double InitialWidthWorkAreaRatio = 0.82;
    private const double InitialHeightWorkAreaRatio = 0.86;

    public static SizeInt32 ScaleSizeForWindow(Window window, SizeInt32 desiredSize)
    {
        var handle = WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96u : dpi) / 96d;
        var scaled = new SizeInt32(
            (int)Math.Round(desiredSize.Width * scale),
            (int)Math.Round(desiredSize.Height * scale));
        return ConstrainPhysicalSizeForWindow(window, scaled);
    }

    public static SizeInt32 ConstrainPhysicalSizeForWindow(Window window, SizeInt32 physicalSize)
    {
        var handle = WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96u : dpi) / 96d;
        var workArea = DisplayArea.GetFromWindowId(
            window.AppWindow.Id,
            DisplayAreaFallback.Nearest).WorkArea;
        var minimumWidth = (int)Math.Round(MinimumClientWidth * scale);
        var minimumHeight = (int)Math.Round(MinimumClientHeight * scale);
        var maxWidth = Math.Max(minimumWidth, (int)Math.Round(workArea.Width * InitialWidthWorkAreaRatio));
        var maxHeight = Math.Max(minimumHeight, (int)Math.Round(workArea.Height * InitialHeightWorkAreaRatio));
        return new SizeInt32(
            Math.Clamp(physicalSize.Width, minimumWidth, maxWidth),
            Math.Clamp(physicalSize.Height, minimumHeight, maxHeight));
    }

    public static WindowPlacementState Capture(Window window)
    {
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        return new WindowPlacementState
        {
            PlacementVersion = CurrentPlacementVersion,
            Left = position.X,
            Top = position.Y,
            Width = size.Width,
            Height = size.Height,
        };
    }


    public static void Restore(Window window, WindowPlacementState placement)
    {
        var size = ConstrainPhysicalSizeForWindow(window, new SizeInt32(placement.Width, placement.Height));
        var workArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var maxLeft = workArea.X + Math.Max(0, workArea.Width - size.Width);
        var maxTop = workArea.Y + Math.Max(0, workArea.Height - size.Height);
        var position = new PointInt32(
            Math.Clamp(placement.Left, workArea.X, maxLeft),
            Math.Clamp(placement.Top, workArea.Y, maxTop));
        window.AppWindow.Move(position);
        window.AppWindow.Resize(size);
    }

    public static void Minimize(Window window)
    {
        ShowWindow(WindowNative.GetWindowHandle(window), 6);
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
