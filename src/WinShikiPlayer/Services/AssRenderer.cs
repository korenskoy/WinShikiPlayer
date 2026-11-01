using System.Runtime.InteropServices;
using System.Text;

namespace WinShikiPlayer.Services;

// libass owns all native memory; callers receive a managed, premultiplied BGRA frame.
internal sealed class AssRenderer : IDisposable
{
    private IntPtr _library;
    private IntPtr _renderer;
    private IntPtr _track;
    public AssRenderer()
    {
        _library = ass_library_init();
        if (_library == IntPtr.Zero) throw new InvalidOperationException("libass: не удалось создать библиотеку.");
        _renderer = ass_renderer_init(_library);
        if (_renderer == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("libass: не удалось создать рендерер."); }
        ass_set_fonts(_renderer, null, "Arial", 1, null, 1);
    }
    public void Load(byte[] content)
    {
        if (_track != IntPtr.Zero) ass_free_track(_track);
        _track = ass_read_memory(_library, content, (nuint)content.Length, "UTF-8");
        if (_track == IntPtr.Zero) throw new InvalidOperationException("Не удалось прочитать ASS-субтитры.");
    }
    public byte[]? Render(int width, int height, double seconds)
    {
        if (_track == IntPtr.Zero || width < 1 || height < 1 || width > 3840 || height > 2160) return null;
        ass_set_frame_size(_renderer, width, height);
        var current = ass_render_frame(_renderer, _track, (long)(Math.Max(0, seconds) * 1000), out var changed);
        if (changed == 0) return null;
        var output = new byte[checked(width * height * 4)];
        var images = 0;
        while (current != IntPtr.Zero && images++ < 10000)
        {
            var item = Marshal.PtrToStructure<AssImage>(current); current = item.Next;
            if (item.Width < 1 || item.Height < 1 || item.Stride < item.Width || item.Width > width || item.Height > height) continue;
            var red = (byte)(item.Color >> 24); var green = (byte)(item.Color >> 16); var blue = (byte)(item.Color >> 8);
            var opacity = 255 - (int)(item.Color & 255);
            var row = new byte[item.Width];
            for (var y = 0; y < item.Height; y++)
            {
                var destinationY = item.Y + y; if (destinationY < 0 || destinationY >= height) continue;
                Marshal.Copy(item.Bitmap + y * item.Stride, row, 0, item.Width);
                for (var x = 0; x < item.Width; x++)
                {
                    var destinationX = item.X + x; if (destinationX < 0 || destinationX >= width) continue;
                    var alpha = row[x] * opacity / 255; if (alpha == 0) continue;
                    var index = (destinationY * width + destinationX) * 4;
                    output[index] = (byte)((blue * alpha + output[index] * (255 - alpha)) / 255);
                    output[index + 1] = (byte)((green * alpha + output[index + 1] * (255 - alpha)) / 255);
                    output[index + 2] = (byte)((red * alpha + output[index + 2] * (255 - alpha)) / 255);
                    output[index + 3] = (byte)(alpha + output[index + 3] * (255 - alpha) / 255);
                }
            }
        }
        return output;
    }
    public void Dispose()
    {
        if (_track != IntPtr.Zero) ass_free_track(_track);
        if (_renderer != IntPtr.Zero) ass_renderer_done(_renderer);
        if (_library != IntPtr.Zero) ass_library_done(_library);
        _track = _renderer = _library = IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AssImage
    {
        public int Width, Height, Stride; public IntPtr Bitmap; public uint Color; public int X, Y; public IntPtr Next; public int Type;
    }
    [DllImport("libass.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ass_library_init();
    [DllImport("libass.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void ass_library_done(IntPtr library);
    [DllImport("libass.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ass_renderer_init(IntPtr library);
    [DllImport("libass.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void ass_renderer_done(IntPtr renderer);
    [DllImport("libass.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void ass_free_track(IntPtr track);
    [DllImport("libass.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void ass_set_frame_size(IntPtr renderer, int width, int height);
    [DllImport("libass.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ass_render_frame(IntPtr renderer, IntPtr track, long now, out int changed);
    [DllImport("libass.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void ass_set_fonts(IntPtr renderer, string? defaultFont, string family, int provider, string? config, int update);
    [DllImport("libass.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr ass_read_memory(IntPtr library, byte[] buffer, nuint size, string codepage);
}
