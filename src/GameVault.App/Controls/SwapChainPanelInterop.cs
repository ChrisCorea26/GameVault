using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace GameVault.App.Controls;

/// <summary>Attaches a DXGI swap chain to a WinUI <see cref="SwapChainPanel"/> (ISwapChainPanelNative).</summary>
internal static unsafe class SwapChainPanelInterop
{
    private static readonly Guid SwapChainPanelNativeId = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    /// <summary>Must be called on the UI thread.</summary>
    public static void SetSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        var unknown = ((IWinRTObject)panel).NativeObject.ThisPtr;
        var iid = SwapChainPanelNativeId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var native));
        try
        {
            // ISwapChainPanelNative::SetSwapChain is the first method after IUnknown's three.
            var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(void***)native)[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }
}
