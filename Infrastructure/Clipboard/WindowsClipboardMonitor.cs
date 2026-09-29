using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CsAgent.Infrastructure.Clipboard;

public sealed record ClipboardImage(
    byte[] PngBytes,
    int Width,
    int Height,
    DateTimeOffset CapturedAt);

public sealed partial class WindowsClipboardMonitor : IDisposable
{
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint WM_APP_STOP = 0x8001;
    private const uint CS_HREDRAW = 0x0002;
    private const uint CS_VREDRAW = 0x0001;
    private const uint WS_OVERLAPPED = 0x00000000;
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private const int MAX_RETRIES = 5;

    private static readonly nint HWND_MESSAGE = (nint)(-3);

    private static readonly ConcurrentDictionary<nint, WindowsClipboardMonitor> s_instances = new();

    private static readonly object s_classSync = new();
    private static ushort s_windowClassAtom = 0;
    private const string s_className = "CsAgent.ClipboardMonitor";

    private readonly object _sync = new();
    private Thread? _thread;
    private uint _threadId;
    private nint _windowHandle;
    private int _lastError;
    private ClipboardImage? _latest;
    private volatile bool _started;
    private volatile bool _disposed;
    private readonly ManualResetEventSlim _startedEvent = new(false);

    // ── Public API ───────────────────────────────────────────────────────────

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsClipboardMonitor));
        if (_started) return;

        _thread = new Thread(MessageThread) { IsBackground = true, Name = "CSAgent Clipboard" };
        _thread.Start();
        _startedEvent.Wait();

        if (_windowHandle == 0)
            throw new InvalidOperationException(
                $"Failed to create the Windows clipboard monitor window. Win32 error: {_lastError}");

        _started = true;
    }

    public ClipboardImage? ConsumeLatest()
    {
        lock (_sync) { var img = _latest; _latest = null; return img; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_windowHandle != 0) PostMessageW(_windowHandle, WM_APP_STOP, 0, 0);
        else if (_threadId != 0) PostThreadMessageW(_threadId, WM_APP_STOP, 0, 0);
        try { _thread?.Join(TimeSpan.FromSeconds(2)); } catch { }
        _startedEvent.Dispose();
    }

    // ── Instance registry ────────────────────────────────────────────────────

    private void AttachInstance() => s_instances[_windowHandle] = this;
    private static WindowsClipboardMonitor? GetInstance(nint hwnd)
        => s_instances.GetValueOrDefault(hwnd);

    // ── Message thread ───────────────────────────────────────────────────────

    private void MessageThread()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            EnsureWindowClass();

            _windowHandle = CreateWindowExW(
                0, s_className, "CSAgent Clipboard Monitor", WS_OVERLAPPED,
                0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);

            if (_windowHandle == 0)
            {
                _lastError = Marshal.GetLastWin32Error();
                _startedEvent.Set();
                return;
            }

            if (!AddClipboardFormatListener(_windowHandle))
            {
                _lastError = Marshal.GetLastWin32Error();
                DestroyWindow(_windowHandle);
                _windowHandle = 0;
                _startedEvent.Set();
                return;
            }

            AttachInstance();
            _startedEvent.Set();

            MSG msg = default;
            while (!_disposed)
            {
                var r = GetMessageW(ref msg, 0, 0, 0);
                if (r == -1 || r == 0) break;
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            RemoveClipboardFormatListener(_windowHandle);
            s_instances.TryRemove(_windowHandle, out _);
            DestroyWindow(_windowHandle);
            _windowHandle = 0;
        }
        catch (Exception ex)
        {
            _lastError = Marshal.GetLastWin32Error();
            _windowHandle = 0;
            Console.Error.WriteLine($"Clipboard monitor: {ex.Message} (Win32: {_lastError})");
            _startedEvent.Set();
        }
    }

    private static void EnsureWindowClass()
    {
        lock (s_classSync)
        {
            if (s_windowClassAtom != 0) return;

            unsafe
            {
                fixed (char* classNamePtr = s_className)
                {
                    var wc = new WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(WNDCLASSEXW),
                        style = CS_HREDRAW | CS_VREDRAW,
                        lpfnWndProc = &WindowProc,
                        lpszClassName = classNamePtr
                    };
                    s_windowClassAtom = RegisterClassExW((nint)(&wc));
                }
            }

            if (s_windowClassAtom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 1410)
                    throw new InvalidOperationException($"RegisterClassExW failed. Win32: {err}");
                s_windowClassAtom = 1;
            }
        }
    }

    // ── Window procedure ─────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_CLIPBOARDUPDATE) { HandleClipboardUpdate(hwnd); return 0; }
            if (msg == WM_APP_STOP) { PostQuitMessage(0); return 0; }
            if (msg == 0x0002 /*WM_DESTROY*/) { PostQuitMessage(0); return 0; }
        }
        catch { }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void HandleClipboardUpdate(nint hwnd)
    {
        for (var i = 0; i < MAX_RETRIES; i++)
        {
            var image = TryReadClipboardImage(hwnd);
            if (image is not null)
            {
                var inst = GetInstance(hwnd);
                if (inst is not null) lock (inst._sync) inst._latest = image;
                return;
            }
            Thread.Sleep(30);
        }
    }

    private static ClipboardImage? TryReadClipboardImage(nint hwnd)
    {
        if (!OpenClipboard(hwnd)) return null;
        try
        {
            nint handle = 0;
            bool isDibV5 = false;
            if (IsClipboardFormatAvailable(CF_DIBV5)) { handle = GetClipboardData(CF_DIBV5); isDibV5 = true; }
            else if (IsClipboardFormatAvailable(CF_DIB)) { handle = GetClipboardData(CF_DIB); }
            if (handle == 0) return null;

            var size = GlobalSize(handle);
            if (size == 0 || size > int.MaxValue) return null;

            var locked = GlobalLock(handle);
            if (locked == 0) return null;
            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(locked, bytes, 0, bytes.Length);
                return DibImageEncoder.Encode(bytes, isDibV5);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    // ── Win32 structs ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd, lParam;
        public uint message, time, lPrivate;
        public nuint wParam;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    // ── P/Invoke (LibraryImport — source-generated, NativeAOT-safe) ────────────

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(nint lpwcx);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint dwExStyle, string lpClassName,
        string lpWindowName, uint dwStyle, int X, int Y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint fmt);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint fmt);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint GlobalSize(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(ref MSG lpMsg, nint hWnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint tid, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}

/// <summary>
/// Converts Windows DIB/DIBV5 clipboard data to PNG without System.Drawing.
/// Uses ZLibStream for correct zlib-wrapped IDAT data per PNG spec.
/// </summary>
internal static class DibImageEncoder
{
    private const int BITMAPINFOHEADER_SIZE = 40;
    private const uint BI_RGB = 0;
    private const uint BI_BITFIELDS = 3;

    public static ClipboardImage? Encode(byte[] dib, bool isDibV5)
    {
        if (dib.Length < BITMAPINFOHEADER_SIZE) return null;

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0, 4));
        if (headerSize < BITMAPINFOHEADER_SIZE) return null;

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4, 4));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8, 4));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(12, 2));
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16, 4));

        if (width <= 0 || rawHeight == 0 || planes != 1) return null;
        if (bitsPerPixel != 24 && bitsPerPixel != 32) return null;
        if (compression != BI_RGB && compression != BI_BITFIELDS) return null;

        var topDown = rawHeight < 0;
        var height = Math.Abs(rawHeight);
        var pixelOffset = headerSize;

        uint redMask = 0x00FF0000, greenMask = 0x0000FF00,
             blueMask = 0x000000FF, alphaMask = 0xFF000000;

        if (compression == BI_BITFIELDS)
        {
            if (pixelOffset + 12 > dib.Length) return null;
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(pixelOffset, 4));
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(pixelOffset + 4, 4));
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(pixelOffset + 8, 4));
            pixelOffset += 12;
            if (isDibV5 && pixelOffset + 4 <= dib.Length)
            {
                alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(pixelOffset, 4));
                pixelOffset += 4;
            }
        }

        if (isDibV5 && headerSize >= 56)
        {
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(40, 4));
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(44, 4));
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(48, 4));
            alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(52, 4));
            pixelOffset = headerSize;
        }

        var bytesPerPixel = bitsPerPixel / 8;
        var sourceStride = ((width * bytesPerPixel) + 3) & ~3;
        if ((long)pixelOffset + (long)sourceStride * height > dib.Length) return null;

        var rgba = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var srcY = topDown ? y : height - 1 - y;
            var srcRow = pixelOffset + srcY * sourceStride;
            var dstRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var src = srcRow + x * bytesPerPixel;
                var dst = dstRow + x * 4;
                uint pixel = bitsPerPixel == 32
                    ? BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(src, 4))
                    : (uint)(dib[src] | (dib[src + 1] << 8) | (dib[src + 2] << 16));

                byte fallback = bitsPerPixel == 24 ? (byte)255 : (byte)0;
                rgba[dst] = Extract(pixel, blueMask, fallback);
                rgba[dst + 1] = Extract(pixel, greenMask, fallback);
                rgba[dst + 2] = Extract(pixel, redMask, fallback);
                rgba[dst + 3] = bitsPerPixel == 24 ? (byte)255 : Extract(pixel, alphaMask, 255);
            }
        }

        return new ClipboardImage(EncodePng(width, height, rgba), width, height, DateTimeOffset.UtcNow);
    }

    private static byte Extract(uint pixel, uint mask, byte fallback)
    {
        if (mask == 0) return fallback;
        var shift = TrailingZeros(mask);
        var value = (pixel & mask) >> shift;
        var max = mask >> shift;
        return max == 0 ? fallback : (byte)((value * 255 + max / 2) / max);
    }

    private static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        using var out_ = new MemoryStream();
        out_.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8; ihdr[9] = 6;
        WriteChunk(out_, "IHDR", ihdr);

        var stride = width * 4 + 1;
        var raw = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            raw[y * stride] = 0;
            Buffer.BlockCopy(rgba, y * width * 4, raw, y * stride + 1, width * 4);
        }

        using (var buf = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buf, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(raw);
            WriteChunk(out_, "IDAT", buf.ToArray());
        }

        WriteChunk(out_, "IEND", []);
        return out_.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        Span<byte> t = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        s.Write(t);
        s.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(t, data));
        s.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFF;
        foreach (var v in a) c = Upd(c, v);
        foreach (var v in b) c = Upd(c, v);
        return ~c;
    }

    private static uint Upd(uint c, byte v)
    {
        c ^= v;
        for (var i = 0; i < 8; i++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
        return c;
    }

    private static int TrailingZeros(uint v)
    {
        if (v == 0) return 32;
        var n = 0;
        while ((v & 1) == 0) { v >>= 1; n++; }
        return n;
    }
}