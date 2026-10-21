// NaiwaPet - a minimal desktop pet.
//
//   NaiwaPet.exe              run the pet (double-click the frog to play)
//   NaiwaPet.exe --selftest f render one frame onto a mock desktop, save PNG
//   NaiwaPet.exe --capture  f grab the screen after a short delay, save PNG
//
// The window is a layered window with per-pixel alpha, so only the frog is
// visible.  Playback is driven by the audio device position (winmm), not by a
// wall clock, so picture and sound cannot drift apart.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NaiwaPet
{
    static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;
        public const int WM_NCHITTEST = 0x0084;
        public const int HTTRANSPARENT = -1;
        public const uint BI_RGB = 0;
        public const uint DIB_RGB_COLORS = 0;
        public const int TIME_MS = 1;
        public const int TIME_SAMPLES = 2;
        public const int TIME_BYTES = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }
        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public int biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct MMTIME { public uint wType, sample, pad; }

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr hwo, int dev,
            ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] public static extern int waveOutGetPosition(IntPtr hwo, ref MMTIME t, int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATAW data);


    }

    /// <summary>Plays a PCM wave file and reports the exact sample position.</summary>
    sealed class AudioClock : IDisposable
    {
        IntPtr _wo = IntPtr.Zero;
        IntPtr _hdr = IntPtr.Zero;
        GCHandle _pin;
        static byte[] s_pcm;
        byte[] _buf;                 // buffer used by this run (shared or scaled copy)
        static int s_rate = 44100, s_channels = 2;
        public bool Available;

        public double Duration
        {
            get { return s_pcm == null ? 0 : s_pcm.Length / (double)(s_rate * s_channels * 2); }
        }

        public bool Load(string path)
        {
            try
            {
                byte[] all = File.ReadAllBytes(path);
                if (all.Length < 44 || all[0] != 'R' || all[1] != 'I' || all[2] != 'F' || all[3] != 'F')
                    return false;
                int p = 12;
                while (p + 8 <= all.Length)
                {
                    string id = "" + (char)all[p] + (char)all[p + 1] + (char)all[p + 2] + (char)all[p + 3];
                    int sz = BitConverter.ToInt32(all, p + 4);
                    if (id == "fmt ")
                    {
                        s_channels = BitConverter.ToInt16(all, p + 10);
                        s_rate = BitConverter.ToInt32(all, p + 12);
                    }
                    else if (id == "data")
                    {
                        s_pcm = new byte[sz];
                        Buffer.BlockCopy(all, p + 8, s_pcm, 0, Math.Min(sz, all.Length - p - 8));
                        return s_pcm.Length > 0;
                    }
                    p += 8 + sz + (sz & 1);
                }
                return false;
            }
            catch { return false; }
        }

        public int LastOpenRc, LastPrepRc, LastWriteRc;

        double _offset;

        public void Start() { Start(0.0, 1); }

        public void Start(double offsetSeconds) { Start(offsetSeconds, 1); }

        /// <summary>
        /// Start playback at a source offset.  When several frogs laugh together the
        /// OS mixer sums the same clip N times, so each instance is scaled down to
        /// keep that sum inside full scale - otherwise it clips and sounds crunchy.
        /// </summary>
        public void Start(double offsetSeconds, int concurrent)
        {
            Stop();
            if (s_pcm == null) return;
            _buf = s_pcm;            // no attenuation: several frogs simply add up
            Native.WAVEFORMATEX fmt = new Native.WAVEFORMATEX();
            fmt.wFormatTag = 1; fmt.nChannels = (ushort)s_channels;
            fmt.nSamplesPerSec = (uint)s_rate;
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = (ushort)(s_channels * 2);
            fmt.nAvgBytesPerSec = (uint)(s_rate * fmt.nBlockAlign);
            fmt.cbSize = 0;

            int r = Native.waveOutOpen(out _wo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            LastOpenRc = r;
            if (r != 0) { _wo = IntPtr.Zero; Available = false; return; }

            int blockAlign = s_channels * 2;
            int skip = (int)(Math.Max(0.0, offsetSeconds) * s_rate) * blockAlign;
            if (skip > _buf.Length) skip = _buf.Length;
            _offset = skip / (double)(s_rate * blockAlign);

            _pin = GCHandle.Alloc(_buf, GCHandleType.Pinned);
            // WAVEHDR: lpData, dwBufferLength, dwBytesRecorded, dwUser, dwFlags, dwLoops, lpNext, reserved
            int hdrSize = IntPtr.Size == 8 ? 48 : 32;
            _hdr = Marshal.AllocHGlobal(hdrSize);
            for (int i = 0; i < hdrSize; i++) Marshal.WriteByte(_hdr, i, 0);
            Marshal.WriteIntPtr(_hdr, 0, _pin.AddrOfPinnedObject() + skip);
            Marshal.WriteInt32(_hdr, IntPtr.Size, _buf.Length - skip);

            LastPrepRc = Native.waveOutPrepareHeader(_wo, _hdr, hdrSize);
            LastWriteRc = Native.waveOutWrite(_wo, _hdr, hdrSize);
            Available = true;
        }

        public double Position
        {
            get
            {
                if (!Available || _wo == IntPtr.Zero) return 0;
                Native.MMTIME t = new Native.MMTIME();
                t.wType = Native.TIME_SAMPLES;   // must be filled in before the call
                int sz = Marshal.SizeOf(typeof(Native.MMTIME));
                if (Native.waveOutGetPosition(_wo, ref t, sz) != 0) return 0;
                if (t.wType == Native.TIME_SAMPLES) return _offset + t.sample / (double)s_rate;
                if (t.wType == Native.TIME_MS) return _offset + t.sample / 1000.0;
                if (t.wType == Native.TIME_BYTES)
                    return _offset + t.sample / (double)(s_rate * s_channels * 2);
                return _offset;
            }
        }

        public void Stop()
        {
            if (_wo != IntPtr.Zero)
            {
                Native.waveOutReset(_wo);
                if (_hdr != IntPtr.Zero)
                {
                    Native.waveOutUnprepareHeader(_wo, _hdr, IntPtr.Size == 8 ? 48 : 32);
                    Marshal.FreeHGlobal(_hdr);
                    _hdr = IntPtr.Zero;
                }
                Native.waveOutClose(_wo);
                _wo = IntPtr.Zero;
            }
            if (_pin.IsAllocated) _pin.Free();
            Available = false;
        }

        public void Dispose() { Stop(); }
    }

    sealed class PetForm : Form
    {
        const int FPS = 30;
        const int CacheMax = 90;

        // Shared by every pet window: same folder, same canvas, same frames.
        static readonly Dictionary<int, Bitmap> s_cache = new Dictionary<int, Bitmap>();
        // frame index -> how many windows currently display it; never evict these
        static readonly Dictionary<int, int> s_use = new Dictionary<int, int>();
        static readonly Queue<int> s_order = new Queue<int>();
        static string s_frameDir;
        static int s_count;
        static int s_frameW, s_frameH;
        static readonly List<PetForm> s_pets = new List<PetForm>();
        public static int PetCount { get { return s_pets.Count; } }
        /// <summary>Fired when the last frog has closed, so the app can quit.</summary>
        public static Action AllClosed;
        public static Action QuitRequested;

        readonly AudioClock _audio = new AudioClock();

        const int HOVER_FRAME = 45;      // hover parks here
        const int HOVER_FPS = 60;        // hover animation speed
        enum PetState { Idle, HoverFwd, HoverHold, HoverBack, Playing }

        Bitmap _shown;
        int _shownIndex = -1;
        PetState _state = PetState.Idle;
        int _scale = 100;                 // 100 / 80 / 60 percent
        Bitmap _scaledBuf;                // reusable, avoids per-frame allocation

        // belly poke: a local displacement field with a damped spring-back
        const int POKE_MS = 420;
        const int DOUBLE_MS = 200;        // two presses closer than this count as a double click
        int _pokeStart = -1;              // ms when the deformation began, -1 = idle
        int _lastDownTick = int.MinValue; // for our own double-click window
        int _pokeX, _pokeY;               // centre, in window pixels
        float _pokeRadius;
        int _pokePendingAt = -1;          // ms of a single click waiting to see if it is a double
        Point _pokePendingPt;
        int _hoverFrom;
        int _hoverFrame;
        int _hoverStartMs;
        static int s_forcedHover = -1;   // -1 off, 0 force out, 1 force in (testing)
        bool _playing;
        bool _muted;                 // animates, but another pet owns the sound
        double _fallbackStart;
        System.Windows.Forms.Timer _timer;
        Point _dragOffset;
        bool _dragging;
        Point _downPoint;
        static string s_logPath;
        int _lastFrame = -1;
        DateTime _lastLog = DateTime.MinValue;

        public static void LoadFrames(string frameDir)
        {
            if (s_frameDir != null) return;
            s_frameDir = frameDir;
            s_count = Directory.GetFiles(frameDir, "*.png").Length;
            using (Image probe = Image.FromFile(Path.Combine(frameDir, "0000.png")))
            {
                s_frameW = probe.Width;
                s_frameH = probe.Height;
            }
        }

        public PetForm(string frameDir, string logPath, int autoplayMs)
        {
            LoadFrames(frameDir);
            if (logPath != null) s_logPath = logPath;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Empty;

            string[] fs = Directory.GetFiles(frameDir, "*.png");
            Array.Sort(fs, StringComparer.Ordinal);
            using (Image probe = Image.FromFile(fs[0]))
            {
                ClientSize = probe.Size;
            }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 40, wa.Bottom - Height - 20);

            // the menu is rebuilt each time it opens, because the ">2 pets" items
            // depend on how many frogs are currently on the desktop
            var menu = new ContextMenuStrip();
            menu.Opening += delegate { BuildMenu(menu); };
            ContextMenuStrip = menu;

            _audio.Load(Path.Combine(frameDir, "..\\naiwa.wav"));
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 10;
            _timer.Tick += delegate { OnTick(); };
            _timer.Start();

            s_pets.Add(this);
            ShowFrame(0);

            if (autoplayMs >= 0)
            {
                System.Windows.Forms.Timer auto = new System.Windows.Forms.Timer();
                auto.Interval = Math.Max(50, autoplayMs);
                auto.Tick += delegate { auto.Stop(); Play(true); };
                auto.Start();
            }
        }

        void BuildMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            int n = PetCount;

            menu.Items.Add("播放一遍 (或双击)", null, delegate { Play(true); });
            menu.Items.Add("再来一只", null, delegate { SpawnOne(_scale); });

            ToolStripMenuItem sizeItem = new ToolStripMenuItem("大小");
            int[] steps = new int[] { 100, 80, 60 };
            for (int k = 0; k < steps.Length; k++)
            {
                int pct = steps[k];
                ToolStripMenuItem it = new ToolStripMenuItem(pct + "%", null, delegate { SetScale(pct); });
                it.Checked = (_scale == pct);
                sizeItem.DropDownItems.Add(it);
            }
            menu.Items.Add(sizeItem);

            if (n > 1)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("删掉这一只", null, delegate { Close(); });
                menu.Items.Add("仅保留这一只", null, delegate { KeepOnlyMe(); });
                menu.Items.Add("全部大笑", null, delegate { LaughAll(); });
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(n > 1 ? "退出全部" : "退出", null, delegate
            {
                CloseAll();
                if (QuitRequested != null) QuitRequested();
            });
        }

        static int s_spawnSeq;

        /// <summary>
        /// Open another frog.  New frogs are laid out on a grid anchored to the
        /// bottom-right corner, so several of them never land on the same spot.
        /// </summary>
        public static PetForm SpawnOne() { return SpawnOne(100); }

        public static PetForm SpawnOne(int scalePct)
        {
            PetForm p = new PetForm(s_frameDir, null, -1);
            if (scalePct != 100) p.SetScale(scalePct);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int n = s_spawnSeq++;
            int perRow = Math.Max(1, (wa.Width - 40) / (p.Width + 16));
            int col = n % perRow;
            int row = (n / perRow) % 3;
            int x = wa.Right - 40 - p.Width - col * (p.Width + 16);
            int y = wa.Bottom - 20 - p.Height - row * (p.Height + 16);
            x = Math.Max(wa.Left + 8, x);
            y = Math.Max(wa.Top + 8, y);
            p.Location = new Point(x, y);
            p.Show();
            p.PushCurrent();
            return p;
        }

        /// <summary>Menu labels for the current pet count - used by --menudump.</summary>
        public string[] MenuLabels()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            BuildMenu(m);
            List<string> r = new List<string>();
            foreach (ToolStripItem it in m.Items) r.Add(it.Text);
            m.Dispose();
            return r.ToArray();
        }

        public void LaughAllPublic() { LaughAll(); }
        public void KeepOnlyPublic() { KeepOnlyMe(); }
        public static void CloseAllPublic() { CloseAll(); }
        public void ProbeAlpha()
        {
            try
            {
                Point c = new Point(Width / 2, Height / 2);
                AlphaAt(c);                       // this is GetPixel on _shown
                s_probes++;
            }
            catch (Exception ex)
            {
                s_probeFail++;
                Log(string.Format("PROBE EXCEPTION #{0}: {1}: {2}", s_probeFail,
                    ex.GetType().Name, ex.Message));
            }
        }

        public static int s_probes;
        public static int s_probeFail;

        public static void ProbeAll()
        {
            foreach (PetForm f in s_pets.ToArray()) f.ProbeAlpha();
        }

        public static void PlayOne(int i)
        {
            if (i >= 0 && i < s_pets.Count) s_pets[i].Play(true);
        }

        void KeepOnlyMe()
        {
            foreach (PetForm p in s_pets.ToArray())
            {
                if (!ReferenceEquals(p, this)) p.Close();
            }
        }

        public static void LaughAll()
        {
            PetForm[] all = s_pets.ToArray();
            for (int i = 0; i < all.Length; i++)
                all[i].PlayFrom(0, true, all.Length);   // only this one makes sound
        }

        public static void CloseAll()
        {
            foreach (PetForm p in s_pets.ToArray()) p.Close();
        }

        /// <summary>Resize this frog, keeping its bottom-right corner anchored.</summary>
        public void SetScale(int pct)
        {
            if (pct < 20) pct = 20;
            if (pct > 200) pct = 200;
            if (_scale == pct) return;

            int oldW = Width, oldH = Height;
            _scale = pct;
            int nw = Math.Max(8, s_frameW * pct / 100);
            int nh = Math.Max(8, s_frameH * pct / 100);
            ClientSize = new Size(nw, nh);
            Location = new Point(Left + (oldW - nw), Top + (oldH - nh));
            if (_scaledBuf != null) { _scaledBuf.Dispose(); _scaledBuf = null; }
            int f = _shownIndex >= 0 ? _shownIndex : 0;
            _shownIndex = -1;                      // force the repaint
            ShowFrame(f);
        }

        public int ScalePercent { get { return _scale; } }

        /// <summary>
        /// Draw the frozen frame with a radial displacement around (cx,cy).
        /// k &gt; 0 pulls the texture toward the centre (a finger pushing in),
        /// k &lt; 0 relaxes / overshoots outward.  Done in one pass, straight to the
        /// layered-window DIB, premultiplying as we go.
        /// </summary>
        unsafe void WarpPush(Bitmap src, int cx, int cy, float radius, float k)
        {
            int w = Math.Min(src.Width, Width), h = Math.Min(src.Height, Height);
            Native.BITMAPINFO bi = new Native.BITMAPINFO();
            bi.bmiHeader.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h;
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = (int)Native.BI_RGB;

            IntPtr bits;
            IntPtr hBmp = Native.CreateDIBSection(IntPtr.Zero, ref bi, Native.DIB_RGB_COLORS,
                out bits, IntPtr.Zero, 0);
            if (hBmp == IntPtr.Zero) return;

            BitmapData bd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                byte* sp = (byte*)bd.Scan0;
                byte* dp = (byte*)bits;
                int stride = bd.Stride;
                float r2 = radius * radius;
                for (int y = 0; y < h; y++)
                {
                    byte* drow = dp + y * w * 4;
                    byte* srow = sp + y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        float sx = x, sy = y;
                        float dx = x - cx, dy = y - cy;
                        float d2 = dx * dx + dy * dy;
                        if (d2 < r2)
                        {
                            float d = (float)Math.Sqrt(d2);
                            float t = 1f - d / radius;
                            float wgt = t * t * (3f - 2f * t);        // smooth falloff
                            float scale = 1f + k * wgt;
                            sx = cx + dx * scale;
                            sy = cy + dy * scale;
                        }

                        // bilinear sample
                        int x0 = (int)sx, y0 = (int)sy;
                        float fx = sx - x0, fy = sy - y0;
                        int x1 = x0 + 1, y1 = y0 + 1;
                        if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0;
                        if (x1 < 0) x1 = 0; if (y1 < 0) y1 = 0;
                        if (x0 > w - 1) x0 = w - 1; if (y0 > h - 1) y0 = h - 1;
                        if (x1 > w - 1) x1 = w - 1; if (y1 > h - 1) y1 = h - 1;

                        byte* p00 = srow - y * stride + y0 * stride + x0 * 4;
                        byte* p10 = srow - y * stride + y0 * stride + x1 * 4;
                        byte* p01 = srow - y * stride + y1 * stride + x0 * 4;
                        byte* p11 = srow - y * stride + y1 * stride + x1 * 4;

                        for (int c = 0; c < 4; c++)
                        {
                            float top = p00[c] + (p10[c] - p00[c]) * fx;
                            float bot = p01[c] + (p11[c] - p01[c]) * fx;
                            float v = top + (bot - top) * fy;
                            drow[x * 4 + c] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
                        }
                        // premultiply in place (BGRA, source alpha at +3)
                        byte a = drow[x * 4 + 3];
                        drow[x * 4 + 0] = (byte)(drow[x * 4 + 0] * a / 255);
                        drow[x * 4 + 1] = (byte)(drow[x * 4 + 1] * a / 255);
                        drow[x * 4 + 2] = (byte)(drow[x * 4 + 2] * a / 255);
                    }
                }
            }
            finally { src.UnlockBits(bd); }

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr old = Native.SelectObject(memDc, hBmp);
            try
            {
                Native.SIZE size = new Native.SIZE(w, h);
                Native.POINT srcPt = new Native.POINT(0, 0);
                Native.POINT dstPt = new Native.POINT(Left, Top);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;
                Native.UpdateLayeredWindow(Handle, screenDc, ref dstPt, ref size, memDc,
                    ref srcPt, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.SelectObject(memDc, old);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                Native.DeleteObject(hBmp);
            }
        }

        /// <summary>Belly = the only pale, warm, opaque area of the character.</summary>
        bool IsBelly(Point p)
        {
            try
            {
                Bitmap b = _shown;
                if (b == null) return false;
                int sx = p.X * 100 / Math.Max(_scale, 1);
                int sy = p.Y * 100 / Math.Max(_scale, 1);
                if (sx < 0 || sy < 0 || sx >= b.Width || sy >= b.Height) return false;
                if (b.GetPixel(sx, sy).A < 100) return false;      // must be on the character (edges included)
                // the belly is a fixed ellipse in frame space (the palette has no isolated
                // pale patch: the lightest area is the chest highlight, ch about 100)
                float nx = (float)sx / s_frameW;
                float ny = (float)sy / s_frameH;
                float dx = (nx - 0.45f) / 0.27f;     // mid-left of the body, fitted to 1.png
                float dy = (ny - 0.45f) / 0.25f;
                return dx * dx + dy * dy <= 1f;
            }
            catch { return false; }
        }

        void CancelPoke()
        {
            if (_pokeStart < 0) return;
            _pokeStart = -1;
            int idx = _shownIndex >= 0 ? _shownIndex : 0;
            _shownIndex = -1;
            ShowFrame(idx);
        }

        /// <summary>Convert WinForms' DPI-scaled mouse coordinates to physical pixels.</summary>
        Point PhysicalPoint(Point p)
        {
            try
            {
                using (Graphics gg = CreateGraphics())
                {
                    float dpi = gg.DpiX / 96f;
                    if (dpi > 0.01f && Math.Abs(dpi - 1f) > 0.01f)
                        return new Point((int)(p.X / dpi), (int)(p.Y / dpi));
                }
            }
            catch { }
            return p;
        }

        void StartPoke(Point p)
        {
            if (_pokeStart >= 0) return;
            if (_state == PetState.Playing) return;
            if (!IsBelly(p)) return;                       // only the belly springs
            int idx = _shownIndex >= 0 ? _shownIndex : 0;
            Bitmap frame = GetFrame(idx);
            ToDisplay(frame);                       // make sure _scaledBuf is current
            _pokeX = p.X;
            _pokeY = p.Y;
            _pokeRadius = Math.Max(38f, Width * 0.30f);      // local dent
            _pokeStart = Environment.TickCount;
        }

        /// <summary>One animation step of the poke; returns true while it owns the window.</summary>
        bool TickPoke()
        {
            if (_pokeStart < 0) return false;
            double t = (Environment.TickCount - _pokeStart) / 1000.0;
            if (t * 1000 > POKE_MS)
            {
                _pokeStart = -1;
                int idx = _shownIndex >= 0 ? _shownIndex : 0;
                _shownIndex = -1;
                ShowFrame(idx);                     // back to the normal sprite
                return false;
            }
            // fast indent, damped overshoot back out
            // positive k samples further out, so the outline recedes = the belly springs inward;
            // higher frequency + faster decay = a quicker, springier response
            double amp = 0.32 * Math.Exp(-8.0 * t) * Math.Cos(17.0 * t);
            Bitmap frame = GetFrame(_shownIndex >= 0 ? _shownIndex : 0);
            Bitmap src = ToDisplay(frame);
            WarpPush(src, _pokeX, _pokeY, _pokeRadius, (float)amp);
            return true;
        }

        void PushCurrent()
        {
            if (_shown != null) Push(_shown);
        }

        void StopPlayback()
        {
            _playing = false;
            _audio.Stop();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Normal) PushCurrent();
        }

        void Log(string line)
        {
            if (s_logPath == null) return;
            try { File.AppendAllText(s_logPath, line + Environment.NewLine); }
            catch { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // always a tool window: no taskbar button, the tray icon is the entry point
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        Bitmap GetFrame(int i)
        {
            if (i < 0) i = 0;
            if (i >= s_count) i = s_count - 1;
            Bitmap b;
            if (s_cache.TryGetValue(i, out b)) return b;
            string p = Path.Combine(s_frameDir, string.Format("{0:0000}.png", i));
            byte[] data = File.ReadAllBytes(p);
            using (MemoryStream ms = new MemoryStream(data))
            using (Image tmp = Image.FromStream(ms))
            {
                b = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(tmp, 0, 0, tmp.Width, tmp.Height);
                }
            }
            s_cache[i] = b;
            s_order.Enqueue(i);
            int guard = s_order.Count;
            while (s_order.Count > CacheMax && guard-- > 0)
            {
                int old = s_order.Dequeue();
                if (old == i) continue;
                int rc;
                if (s_use.TryGetValue(old, out rc) && rc > 0)
                {
                    s_order.Enqueue(old);      // still on screen - put it back, do not free
                    continue;
                }
                // Disposing a frame that another frog window still shows was the crash:
                // the next hit-test hit "ArgumentException: Parameter is not valid".
                Bitmap ob;
                if (s_cache.TryGetValue(old, out ob)) { s_cache.Remove(old); ob.Dispose(); }
            }
            return b;
        }

        /// <summary>Frame at window size: the cached bitmap, or a scaled copy in _scaledBuf.</summary>
        Bitmap ToDisplay(Bitmap frame)
        {
            if (_scale == 100) return frame;
            int sw = Math.Max(8, Width), sh = Math.Max(8, Height);
            if (_scaledBuf == null || _scaledBuf.Width != sw || _scaledBuf.Height != sh)
            {
                if (_scaledBuf != null) _scaledBuf.Dispose();
                _scaledBuf = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
            }
            using (Graphics g = Graphics.FromImage(_scaledBuf))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(frame, new Rectangle(0, 0, sw, sh),
                            0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
            }
            return _scaledBuf;
        }

        unsafe void Push(Bitmap bmp)
        {
            bmp = ToDisplay(bmp);
            int w = bmp.Width, h = bmp.Height;
            Native.BITMAPINFO bi = new Native.BITMAPINFO();
            bi.bmiHeader.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h;
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = (int)Native.BI_RGB;

            IntPtr bits;
            IntPtr hBmp = Native.CreateDIBSection(IntPtr.Zero, ref bi, Native.DIB_RGB_COLORS,
                out bits, IntPtr.Zero, 0);
            if (hBmp == IntPtr.Zero) return;

            BitmapData bd;
            try
            {
                bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
            }
            catch
            {
                Native.DeleteObject(hBmp);
                return;
            }
            try
            {
                byte* src = (byte*)bd.Scan0;
                byte* dst = (byte*)bits;
                int n = w * h;
                for (int i = 0; i < n; i++)
                {
                    byte bb = src[i * 4 + 0];
                    byte gg = src[i * 4 + 1];
                    byte rr = src[i * 4 + 2];
                    byte aa = src[i * 4 + 3];
                    // layered windows want premultiplied BGRA
                    dst[i * 4 + 0] = (byte)(bb * aa / 255);
                    dst[i * 4 + 1] = (byte)(gg * aa / 255);
                    dst[i * 4 + 2] = (byte)(rr * aa / 255);
                    dst[i * 4 + 3] = aa;
                }
            }
            finally { bmp.UnlockBits(bd); }

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr old = Native.SelectObject(memDc, hBmp);
            try
            {
                Native.SIZE size = new Native.SIZE(w, h);
                Native.POINT srcPt = new Native.POINT(0, 0);
                Native.POINT dstPt = new Native.POINT(Left, Top);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;
                Native.UpdateLayeredWindow(Handle, screenDc, ref dstPt, ref size, memDc,
                    ref srcPt, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.SelectObject(memDc, old);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                Native.DeleteObject(hBmp);
            }
        }

        void ShowFrame(int i)
        {
            if (i < 0) i = 0;
            if (i >= s_count) i = s_count - 1;
            if (i == _shownIndex && _shown != null) return;

            int rc;
            s_use.TryGetValue(i, out rc);
            s_use[i] = rc + 1;                  // pin before the decode can evict anything
            Bitmap b = GetFrame(i);

            if (_shownIndex >= 0)
            {
                int o;
                if (s_use.TryGetValue(_shownIndex, out o))
                {
                    if (o <= 1) s_use.Remove(_shownIndex);
                    else s_use[_shownIndex] = o - 1;
                }
            }
            _shownIndex = i;
            _shown = b;
            Push(b);
        }

        /// <summary>
        /// Start one run.  withSound=false makes this frog animate silently, so that
        /// "全部大笑" can have several frogs laughing while only one plays the audio.
        /// </summary>
        void Play(bool withSound) { PlayFrom(0, withSound); }

        /// <summary>
        /// Play from an arbitrary frame at 30 fps.  The audio is started at the same
        /// point of the track, so a run that begins at frame 45 (= 1.5 s) stays in
        /// sync with the picture exactly like the source clip did.
        /// </summary>
        void PlayFrom(int fromFrame, bool withSound) { PlayFrom(fromFrame, withSound, 1); }

        void PlayFrom(int fromFrame, bool withSound, int concurrent)
        {
            if (fromFrame < 0) fromFrame = 0;
            if (fromFrame > s_count - 1) fromFrame = s_count - 1;

            double offset = fromFrame / (double)FPS;
            if (_state == PetState.Playing)
            {
                double p0 = _useWallClock ? 0 : _audio.Position;
                if (p0 < offset + 0.15) return;     // two reports for one gesture
            }

            _muted = !withSound;
            if (_muted)
            {
                _audio.Stop();
                _useWallClock = true;       // silent frog runs on the clock
            }
            else
            {
                _audio.Start(offset, concurrent);
                _useWallClock = false;
            }
            _fallbackStart = (double)Environment.TickCount / 1000.0 - offset;
            _playing = true;
            _state = PetState.Playing;
            _lastFrame = -1;
            _lastAdvance = DateTime.Now;
            _lastPos = -1;
            Log(string.Format("play start from frame {0} (audio offset {1:F3}s) muted={2} audioOk={3}",
                fromFrame, offset, _muted, _audio.Available));
            ShowFrame(fromFrame);
        }

        int HoverPlayFrame()
        {
            if (_state == PetState.HoverFwd || _state == PetState.HoverHold || _state == PetState.HoverBack)
                return _hoverFrame;
            return 0;
        }

        public static void ForceHover(int v) { s_forcedHover = v; }

        public static void HoverPlayPublic(int i)
        {
            if (i >= 0 && i < s_pets.Count) s_pets[i].PlayFrom(s_pets[i].HoverPlayFrame(), true);
        }

        public int HoverFrameNow() { return _hoverFrame; }

        bool CursorOverMe()
        {
            if (s_forcedHover >= 0) return s_forcedHover == 1;
            try
            {
                Point p = Cursor.Position;
                if (!Bounds.Contains(p)) return false;
                return AlphaAt(PointToClient(p)) >= 16;
            }
            catch { return false; }
        }

        int _lastHoverLogMs = -1000;

        void HoverLog(string dir, int f, int now)
        {
            if (s_logPath == null) return;
            if (now - _lastHoverLogMs < 80) return;
            _lastHoverLogMs = now;
            Log(string.Format("{0} frame={1} t={2:F3}", dir, f, now / 1000.0));
        }

        /// <summary>Hover animation: 0 -> 45 at 60 fps, hold, and back down when leaving.</summary>
        void TickHover()
        {
            bool over = CursorOverMe();
            int now = Environment.TickCount;

            if (over && (_state == PetState.Idle || _state == PetState.HoverBack))
            {
                _hoverFrom = _hoverFrame;
                _hoverStartMs = now;
                _state = PetState.HoverFwd;
                Log(string.Format("hover enter at frame {0}", _hoverFrom));
            }
            else if (!over && (_state == PetState.HoverFwd || _state == PetState.HoverHold))
            {
                _hoverFrom = _hoverFrame;
                _hoverStartMs = now;
                _state = PetState.HoverBack;
                Log(string.Format("hover leave at frame {0}", _hoverFrom));
            }

            if (_state == PetState.HoverFwd)
            {
                int f = _hoverFrom + (int)((now - _hoverStartMs) * HOVER_FPS / 1000.0);
                if (f >= HOVER_FRAME) { f = HOVER_FRAME; _state = PetState.HoverHold; }
                _hoverFrame = f;
                ShowFrame(f);
                HoverLog("fwd", f, now);
            }
            else if (_state == PetState.HoverBack)
            {
                int f = _hoverFrom - (int)((now - _hoverStartMs) * HOVER_FPS / 1000.0);
                if (f <= 0) { f = 0; _state = PetState.Idle; }
                _hoverFrame = f;
                ShowFrame(f);
                HoverLog("back", f, now);
            }
        }

        bool _useWallClock;
        DateTime _lastAdvance = DateTime.Now;
        double _lastPos = -1;

        void OnTick()
        {
            if (TickPoke()) return;

            if (_state != PetState.Playing) { TickHover(); return; }
            TickPlayback();
        }

        void TickPlayback()
        {
            double wall = (double)Environment.TickCount / 1000.0 - _fallbackStart;
            double pos = _useWallClock || !_audio.Available ? wall : _audio.Position;

            // watchdog: if the device never advances, keep the show going on the clock
            if (!_useWallClock && _audio.Available)
            {
                if (pos > _lastPos + 0.001) { _lastPos = pos; _lastAdvance = DateTime.Now; }
                else if ((DateTime.Now - _lastAdvance).TotalMilliseconds > 500)
                {
                    _useWallClock = true;
                    Log(string.Format("audio stalled at {0:F3}s -> wall clock", pos));
                    pos = wall;
                }
            }

            double dur = _audio.Duration > 0 ? _audio.Duration : s_count / (double)FPS;
            if (pos >= dur)
            {
                _playing = false;
                _state = PetState.Idle;
                _hoverFrame = 0;
                _audio.Stop();
                Log(string.Format("play end pos={0:F3} wall={1:F3}", pos, wall));
                ShowFrame(0);
                return;
            }
            int f = (int)(pos * FPS);
            if (f >= s_count) f = s_count - 1;
            if (f != _lastFrame) _lastFrame = f;
            ShowFrame(f);
            if ((DateTime.Now - _lastLog).TotalMilliseconds > 400)
            {
                _lastLog = DateTime.Now;
                Log(string.Format("pos={0:F3} frame={1} audioOk={2} wallOk={3}",
                    pos, f, _audio.Available && !_useWallClock, _useWallClock));
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST)
            {
                Point p = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF,
                                                  (m.LParam.ToInt32() >> 16) & 0xFFFF));
                if (AlphaAt(p) < 16)
                {
                    m.Result = (IntPtr)Native.HTTRANSPARENT;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        int AlphaAt(Point p)
        {
            try
            {
                Bitmap b = _shown;
                if (b == null) return 0;
                int sx = p.X * 100 / Math.Max(_scale, 1);
                int sy = p.Y * 100 / Math.Max(_scale, 1);
                if (sx < 0 || sy < 0 || sx >= b.Width || sy >= b.Height) return 0;
                return b.GetPixel(sx, sy).A;
            }
            catch
            {
                return 255;      // unknown: treat as solid, never throw out of WndProc
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragOffset = new Point(e.X, e.Y);
                _downPoint = Cursor.Position;
                _dragging = false;
                int nowTick = Environment.TickCount;
                bool dbl = _lastDownTick != int.MinValue && (nowTick - _lastDownTick) < DOUBLE_MS;
                _lastDownTick = nowTick;
                if (dbl)
                {
                    CancelPoke();                            // second press: play instead of poking
                    PlayFrom(HoverPlayFrame(), true);
                }
                else
                {
                    StartPoke(e.Location);                   // real input is already physical pixels
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.Left && _dragOffset != Point.Empty)
            {
                Point now = Cursor.Position;
                if (!_dragging && Math.Abs(now.X - _downPoint.X) + Math.Abs(now.Y - _downPoint.Y) > 4)
                {
                    _dragging = true;                    // keep the poke running: it is only 420 ms
                }
                if (_dragging)
                {
                    Location = new Point(now.X - _dragOffset.X, now.Y - _dragOffset.Y);
                    // the poke repaints itself at the current Left/Top; do not overwrite it
                    if (_shown != null && _pokeStart < 0) Push(_shown);
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging)
            {
                _dragOffset = Point.Empty;
                _dragging = false;
                return;
            }
            _dragOffset = Point.Empty;
            // the double-click decision happens on press (see OnMouseDown)
        }

        DateTime _lastClick = DateTime.MinValue;

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);             // already handled by our own window in OnMouseDown
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            s_pets.Remove(this);
            _timer.Stop();
            _audio.Dispose();
            // the frame cache is shared by all frogs, so it is not disposed here
            base.OnFormClosed(e);
            if (s_pets.Count == 0)
            {
                s_spawnSeq = 0;                     // next frog starts from the right edge again
                if (AllClosed != null) AllClosed();
            }
        }
    }

    /// <summary>
    /// Keeps the message loop alive even with zero frogs: closing every window is not
    /// the same as quitting, the tray icon stays until "退出奶蛙" is chosen.
    /// </summary>
    sealed class PetContext : ApplicationContext
    {
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Native.SetProcessDPIAware();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string frameDir = Path.Combine(baseDir, "frames");

            if (args.Length >= 2 && args[0] == "--selftest")
            {
                int idx = args.Length >= 3 ? int.Parse(args[2]) : 0;
                SelfTest(frameDir, idx, args[1]);
                return 0;
            }

            int autoplay = -1;
            string log = null;
            int spawn = 0;
            string menudump = null;
            int laughAfter = -1;
            int closetest = -1;
            int stagger = -1;
            int hovertest = -1;
            int hoversim = -1;
            int quitafter = -1;
            bool elevatedRun = false;
            int startScale = 100;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--autoplay" && i + 1 < args.Length) autoplay = int.Parse(args[i + 1]);
                if (args[i] == "--log" && i + 1 < args.Length) log = args[i + 1];
                if (args[i] == "--spawn" && i + 1 < args.Length) spawn = int.Parse(args[i + 1]);
                if (args[i] == "--menudump" && i + 1 < args.Length) menudump = args[i + 1];
                if (args[i] == "--laughafter" && i + 1 < args.Length) laughAfter = int.Parse(args[i + 1]);
                if (args[i] == "--closetest" && i + 1 < args.Length) closetest = int.Parse(args[i + 1]);
                if (args[i] == "--stagger" && i + 1 < args.Length) stagger = int.Parse(args[i + 1]);
                if (args[i] == "--hovertest" && i + 1 < args.Length) hovertest = int.Parse(args[i + 1]);
                if (args[i] == "--hoversim" && i + 1 < args.Length) hoversim = int.Parse(args[i + 1]);
                if (args[i] == "--quitafter" && i + 1 < args.Length) quitafter = int.Parse(args[i + 1]);
                if (args[i] == "--elevated") elevatedRun = true;
                if (args[i] == "--scale" && i + 1 < args.Length) startScale = int.Parse(args[i + 1]);
            }

            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs te)
            {
                string where = RuntimeEnvironment.GetRuntimeDirectory();
                try
                {
                    File.AppendAllText(Path.Combine(baseDir, "error.log"),
                        DateTime.Now.ToString("HH:mm:ss") + " UI EXCEPTION: " + te.Exception + Environment.NewLine);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs ue)
            {
                try
                {
                    File.AppendAllText(Path.Combine(baseDir, "error.log"),
                        DateTime.Now.ToString("HH:mm:ss") + " FATAL: " + ue.ExceptionObject + Environment.NewLine);
                }
                catch { }
            };
            PetContext ctx = new PetContext();

            if (menudump != null)
            {
                StringBuilder sb = new StringBuilder();
                PetForm p = new PetForm(frameDir, null, -1);
                for (int n = 1; n <= 4; n++)
                {
                    if (n > 1) p = PetForm.SpawnOne();
                    sb.AppendLine(string.Format("[{0} 只]", n));
                    foreach (string s in p.MenuLabels()) sb.AppendLine("  " + s);
                }
                File.WriteAllText(menudump, sb.ToString(), Encoding.UTF8);
                return 0;
            }

            PetForm first = new PetForm(frameDir, log, autoplay);
            if (startScale != 100) first.SetScale(startScale);
            first.Show();
            for (int i = 0; i < spawn; i++) PetForm.SpawnOne(startScale);

            if (hoversim > 0)
            {
                After(300, delegate { PetForm.ForceHover(1); });
                After(300 + hoversim, delegate { PetForm.ForceHover(0); });
                After(300 + hoversim * 2, delegate { PetForm.ForceHover(1); });
                After(300 + hoversim * 3, delegate
                {
                    if (log != null) File.AppendAllText(log, "sim: double-click while parked" + Environment.NewLine);
                    PetForm.HoverPlayPublic(0);
                });
            }

            if (hovertest > 0)
            {
                System.Windows.Forms.Timer th = new System.Windows.Forms.Timer();
                th.Interval = Math.Max(1, hovertest);
                th.Tick += delegate { PetForm.ProbeAll(); };
                th.Start();

                System.Windows.Forms.Timer tr = new System.Windows.Forms.Timer();
                tr.Interval = 5000;
                tr.Tick += delegate
                {
                    if (log != null) File.AppendAllText(log,
                        string.Format("probes={0} failures={1}", PetForm.s_probes, PetForm.s_probeFail)
                        + Environment.NewLine);
                };
                tr.Start();
            }

            if (stagger > 0)
            {
                int n = PetForm.PetCount;
                for (int i = 0; i < n; i++)
                {
                    int idx = i;
                    System.Windows.Forms.Timer ts = new System.Windows.Forms.Timer();
                    ts.Interval = (idx + 1) * stagger;
                    ts.Tick += delegate
                    {
                        ts.Stop();
                        if (log != null) File.AppendAllText(log,
                            string.Format("stagger: click frog #{0}", idx) + Environment.NewLine);
                        PetForm.PlayOne(idx);
                    };
                    ts.Start();
                }
            }

            if (closetest >= 0)
            {
                int ct = Math.Max(200, closetest);
                System.Windows.Forms.Timer t1 = new System.Windows.Forms.Timer();
                t1.Interval = ct;
                t1.Tick += delegate
                {
                    t1.Stop();
                    first.KeepOnlyPublic();
                    if (log != null) File.AppendAllText(log,
                        string.Format("after keep-only: count={0}", PetForm.PetCount) + Environment.NewLine);
                };
                t1.Start();

                System.Windows.Forms.Timer t2 = new System.Windows.Forms.Timer();
                t2.Interval = ct * 2 + 400;
                t2.Tick += delegate
                {
                    t2.Stop();
                    if (log != null) File.AppendAllText(log, "closing all" + Environment.NewLine);
                    PetForm.CloseAllPublic();
                };
                t2.Start();
            }

            if (laughAfter >= 0)
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = Math.Max(100, laughAfter);
                t.Tick += delegate { t.Stop(); first.LaughAllPublic(); };
                t.Start();
            }

            NotifyIcon tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            tray.Text = "奶蛙桌宠";

            bool quitting = false;
            Action quit = delegate
            {
                if (quitting) return;
                quitting = true;
                PetForm.CloseAll();
                try { tray.Visible = false; tray.Dispose(); } catch { }
                Application.ExitThread();
            };

            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("增加一只", null, delegate { PetForm.SpawnOne(100); });
            trayMenu.Items.Add("全部大笑", null, delegate { PetForm.LaughAll(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("关掉所有奶蛙", null, delegate { PetForm.CloseAll(); });
            trayMenu.Items.Add("退出奶蛙", null, delegate { quit(); });
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            PetForm.QuitRequested = quit;

            // one-shot tray diagnostic: proves whether the shell accepts our icon at all
            try
            {
                StringBuilder d = new StringBuilder();
                d.AppendLine("time    : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                d.AppendLine("exe     : " + Application.ExecutablePath);
                Native.NOTIFYICONDATAW nid = new Native.NOTIFYICONDATAW();
                nid.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATAW));
                nid.hWnd = first.Handle;
                nid.uID = 4242;
                nid.uFlags = 0x1 | 0x2 | 0x4;               // MESSAGE|ICON|TIP
                nid.uCallbackMessage = 0x0400 + 7;
                Icon probeIcon = tray.Icon;
                nid.hIcon = probeIcon == null ? IntPtr.Zero : probeIcon.Handle;
                nid.szTip = "naiwa probe";
                bool ok = Native.Shell_NotifyIcon(0, ref nid);   // NIM_ADD
                int err = Marshal.GetLastWin32Error();
                d.AppendLine("probe   : add=" + ok + " err=" + err + " cbSize=" + nid.cbSize
                             + " hIconNull=" + (probeIcon == null));
                if (ok) Native.Shell_NotifyIcon(2, ref nid);     // NIM_DELETE
                d.AppendLine("winforms: Visible=" + tray.Visible + " IconNull=" + (tray.Icon == null));
                d.AppendLine("elevated: " + IsElevated() + " relaunchFlag=" + elevatedRun);

                // A process cannot raise its own integrity level.  If the shell refused the
                // icon (ERROR_ACCESS_DENIED, i.e. UIPI) the only way in is to restart once
                // with the "runas" verb - that asks for UAC, so it happens only on failure.
                if (!ok && err == 5 && !elevatedRun && !IsElevated())
                {
                    d.AppendLine("action  : relaunch elevated");
                    File.WriteAllText(Path.Combine(baseDir, "tray.log"), d.ToString(), Encoding.UTF8);
                    try
                    {
                        System.Diagnostics.ProcessStartInfo psi =
                            new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--elevated");
                        psi.UseShellExecute = true;
                        psi.Verb = "runas";
                        System.Diagnostics.Process.Start(psi);
                        tray.Visible = false;
                        tray.Dispose();
                        return 0;
                    }
                    catch (Exception ex2)
                    {
                        try
                        {
                            File.AppendAllText(Path.Combine(baseDir, "tray.log"),
                                "elevate failed: " + ex2.GetType().Name + " " + ex2.Message + Environment.NewLine);
                        }
                        catch { }
                    }
                }

                File.WriteAllText(Path.Combine(baseDir, "tray.log"), d.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(baseDir, "tray.log"), "EX " + ex + Environment.NewLine); }
                catch { }
            }

            if (quitafter > 0)
            {
                int qa = quitafter;
                After(qa, delegate
                {
                    if (PetForm.QuitRequested != null) PetForm.QuitRequested();
                });
            }

            Application.Run(ctx);

            if (!quitting)
            {
                tray.Visible = false;
                tray.Dispose();
            }
            return 0;
        }

        /// <summary>True when this process already runs elevated (administrator).</summary>
        static bool IsElevated()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal pr =
                    new System.Security.Principal.WindowsPrincipal(id);
                return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static void After(int ms, Action act)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = Math.Max(1, ms);
            t.Tick += delegate { t.Stop(); act(); };
            t.Start();
        }

        static void SelfTest(string frameDir, int idx, string outPath)
        {
            string[] fs = Directory.GetFiles(frameDir, "*.png");
            Array.Sort(fs, StringComparer.Ordinal);
            int w = 1100, h = 700;
            using (Bitmap canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                        new Rectangle(0, 0, w, h), Color.FromArgb(28, 34, 52), Color.FromArgb(9, 12, 20), 90f))
                    {
                        g.FillRectangle(lg, 0, 0, w, h);
                    }
                    g.FillRectangle(new SolidBrush(Color.FromArgb(24, 24, 28)), 0, h - 42, w, 42);
                    using (Font f = new Font("Segoe UI", 10f))
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(150, 150, 160)))
                    {
                        g.DrawString("desktop (mock)", f, br, 12, h - 30);
                    }
                    using (Image fr = Image.FromFile(fs[idx]))
                    {
                        g.DrawImage(fr, w - fr.Width - 40, h - 42 - fr.Height + 20, fr.Width, fr.Height);
                    }
                }
                canvas.Save(outPath, ImageFormat.Png);
            }
        }
    }
}
