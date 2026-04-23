using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FocusFence.Helpers;

public static class NativeThumbnailProvider
{
    private const string IID_IShellItemImageFactory = "bcc18b79-ba16-442f-80c4-8a59c30c463b";

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [ComImport]
    [Guid(IID_IShellItemImageFactory)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(
            [In] SIZE size,
            [In] SIIGBF flags,
            [Out] out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [In][MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In][MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out][MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Gets a thumbnail for a file using the Windows Shell API.
    /// Works for images, videos, PDFs, and everything Explorer supports.
    /// </summary>
    public static BitmapSource? GetThumbnail(string fileName, int width, int height)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            Guid iid = new Guid(IID_IShellItemImageFactory);
            SHCreateItemFromParsingName(fileName, IntPtr.Zero, iid, out IShellItemImageFactory factory);
            
            if (factory == null) return null;

            SIZE size = new SIZE(width, height);
            
            // SIIGBF_THUMBNAILONLY ensures we don't accidentally pull generic file icons if thumbnail isn't available
            // If you want default icons when thumbnail fails, use SIIGBF_RESIZETOFIT
            factory.GetImage(size, SIIGBF.SIIGBF_RESIZETOFIT, out hbitmap);

            if (hbitmap != IntPtr.Zero)
            {
                var options = BitmapSizeOptions.FromEmptyOptions();
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, options);
                source.Freeze();
                return source;
            }
        }
        catch (Exception)
        {
            // Silently ignore errors (e.g. no thumbnail available or permission denied)
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
            {
                DeleteObject(hbitmap);
            }
        }

        return null;
    }
}
