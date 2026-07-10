using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace EmsScout.Desktop.Services;

public sealed class WindowHandleProvider
{
    private Window? _window;

    public void Attach(Window window)
    {
        _window = window;
    }

    public IntPtr GetWindowHandle()
    {
        return _window is null
            ? throw new InvalidOperationException("Main window is not attached.")
            : WindowNative.GetWindowHandle(_window);
    }
}
