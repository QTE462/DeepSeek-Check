// DeepSeek 余额挂件 —— 桌面常驻小挂件
// 左键点一下刷新一次余额；右键出菜单；拖动可移动。
// 编译: 见 build.ps1

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DeepSeekBalance
{
    internal static class AppPaths
    {
        public static string BaseDir;
        public static string ConfigPath { get { return Path.Combine(BaseDir, "config.json"); } }
        public static string StatePath { get { return Path.Combine(BaseDir, "state.json"); } }
        public static string LogPath { get { return Path.Combine(BaseDir, "widget.log"); } }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        private static int _errors;

        public static string Describe(Exception ex)
        {
            if (ex == null) return "(null)";
            string text = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null) text += "  <<< " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            text += Environment.NewLine + ex.StackTrace;
            return text;
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    if (File.Exists(AppPaths.LogPath) && new FileInfo(AppPaths.LogPath).Length > 512 * 1024)
                    {
                        File.Move(AppPaths.LogPath, AppPaths.LogPath + ".old");
                    }
                }
            }
            catch { }
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(AppPaths.LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }

        // 动画里的异常不要刷屏，只记前若干次
        public static void WriteOnce(string message)
        {
            if (_errors >= 5) return;
            _errors++;
            Write(message);
        }
    }

    internal sealed class Config
    {
        public string ApiKey = "";
        public string ApiBase = "https://api.deepseek.com";
        public string CharacterPng = "";
        public double Scale = 0.7;
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public bool AlwaysOnTop = true;
        public int AutoRefreshSeconds;
        public bool RefreshOnStart = true;
        public bool SnapEnabled = true;
        public double SnapDistance = 72;
        public bool StartCentered = true;

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static Config Load()
        {
            Config cfg = new Config();
            try
            {
                if (File.Exists(AppPaths.ConfigPath))
                {
                    string raw = File.ReadAllText(AppPaths.ConfigPath, Encoding.UTF8);
                    Dictionary<string, object> root = Json.DeserializeObject(raw) as Dictionary<string, object>;
                    if (root != null)
                    {
                        cfg.ApiKey = Str(root, "api_key", cfg.ApiKey);
                        cfg.ApiBase = Str(root, "api_base", cfg.ApiBase);
                        cfg.CharacterPng = Str(root, "character_png", cfg.CharacterPng);
                        cfg.Scale = Num(root, "scale", cfg.Scale);
                        cfg.AlwaysOnTop = Bool(root, "always_on_top", cfg.AlwaysOnTop);
                        cfg.AutoRefreshSeconds = (int)Num(root, "auto_refresh_seconds", cfg.AutoRefreshSeconds);
                        cfg.RefreshOnStart = Bool(root, "refresh_on_start", cfg.RefreshOnStart);
                        cfg.SnapEnabled = Bool(root, "snap_enabled", cfg.SnapEnabled);
                        cfg.SnapDistance = Num(root, "snap_distance", cfg.SnapDistance);
                        cfg.StartCentered = Bool(root, "start_centered", cfg.StartCentered);
                        cfg.X = (int)Num(root, "x", cfg.X);
                        cfg.Y = (int)Num(root, "y", cfg.Y);
                    }
                }
            }
            catch (Exception ex) { Log.Write("读取配置失败: " + ex.Message); }
            if (cfg.Scale < 0.4) cfg.Scale = 0.4;
            if (cfg.Scale > 2.5) cfg.Scale = 2.5;
            if (cfg.SnapDistance < 0) cfg.SnapDistance = 0;
            if (cfg.SnapDistance > 300) cfg.SnapDistance = 300;
            return cfg;
        }

        public void Save()
        {
            try
            {
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["api_key"] = ApiKey;
                root["api_base"] = ApiBase;
                root["character_png"] = CharacterPng;
                root["scale"] = Scale;
                root["x"] = (X == int.MinValue) ? (object)null : X;
                root["y"] = (Y == int.MinValue) ? (object)null : Y;
                root["always_on_top"] = AlwaysOnTop;
                root["auto_refresh_seconds"] = AutoRefreshSeconds;
                root["refresh_on_start"] = RefreshOnStart;
                root["snap_enabled"] = SnapEnabled;
                root["snap_distance"] = SnapDistance;
                root["start_centered"] = StartCentered;
                File.WriteAllText(AppPaths.ConfigPath, Json.Serialize(root), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("保存配置失败: " + ex.Message); }
        }

        private static string Str(Dictionary<string, object> d, string k, string fallback)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null) return v.ToString();
            return fallback;
        }

        private static double Num(Dictionary<string, object> d, string k, double fallback)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null)
            {
                double parsed;
                if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) return parsed;
            }
            return fallback;
        }

        private static bool Bool(Dictionary<string, object> d, string k, bool fallback)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null)
            {
                bool parsed;
                if (bool.TryParse(v.ToString(), out parsed)) return parsed;
            }
            return fallback;
        }
    }

    internal sealed class UsageState
    {
        public string Date = "";
        public double Baseline = double.NaN;
        public double Last = double.NaN;
        public string LastCheck = "";

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static UsageState Load()
        {
            UsageState s = new UsageState();
            try
            {
                if (File.Exists(AppPaths.StatePath))
                {
                    Dictionary<string, object> root = Json.DeserializeObject(
                        File.ReadAllText(AppPaths.StatePath, Encoding.UTF8)) as Dictionary<string, object>;
                    if (root != null)
                    {
                        if (root.ContainsKey("date") && root["date"] != null) s.Date = root["date"].ToString();
                        s.Baseline = ReadDouble(root, "baseline");
                        s.Last = ReadDouble(root, "last");
                        if (root.ContainsKey("last_check") && root["last_check"] != null) s.LastCheck = root["last_check"].ToString();
                    }
                }
            }
            catch (Exception ex) { Log.Write("读取记账失败: " + ex.Message); }
            return s;
        }

        private static double ReadDouble(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null)
            {
                double parsed;
                if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) return parsed;
            }
            return double.NaN;
        }

        public void Save()
        {
            try
            {
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["date"] = Date;
                root["baseline"] = double.IsNaN(Baseline) ? (object)null : Baseline;
                root["last"] = double.IsNaN(Last) ? (object)null : Last;
                root["last_check"] = LastCheck;
                File.WriteAllText(AppPaths.StatePath, Json.Serialize(root), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("保存记账失败: " + ex.Message); }
        }

        // 用余额差值记今日已用：跨天重置基准；充值（余额变大）也重置基准
        public double Observe(double balance)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (Date != today || double.IsNaN(Baseline) || balance > Last + 0.0001)
            {
                Date = today;
                Baseline = balance;
            }
            Last = balance;
                LastCheck = DateTime.Now.ToString("HH:mm");
            double used = Baseline - balance;
            if (used < 0) used = 0;
            Save();
            return used;
        }

        public double TodayUsed
        {
            get
            {
                if (Date != DateTime.Now.ToString("yyyy-MM-dd")) return 0;
                if (double.IsNaN(Baseline) || double.IsNaN(Last)) return 0;
                double used = Baseline - Last;
                return used < 0 ? 0 : used;
            }
        }
    }

    internal enum FetchState { Idle, Loading, Ok, Error, NoKey }

    internal sealed class BalanceSnapshot
    {
        public bool Ok;
        public bool IsAvailable = true;
        public string Currency = "CNY";
        public double Total;
        public string Error = "";
        public string Raw = "";
    }

    internal static class BalanceApi
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static void BeginFetch(string apiBase, string apiKey, Action<BalanceSnapshot> done)
        {
            BalanceSnapshot fail = new BalanceSnapshot();
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }
            catch { }

            if (string.IsNullOrEmpty(apiKey) || !apiKey.StartsWith("sk-"))
            {
                fail.Ok = false;
                fail.Error = "没有配置 API Key";
                done(fail);
                return;
            }

            WebClient client = new WebClient();
            client.Encoding = Encoding.UTF8;
            client.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
            client.Headers[HttpRequestHeader.Accept] = "application/json";
            client.Headers[HttpRequestHeader.UserAgent] = "DeepSeekBalanceWidget/1.0";

            client.DownloadStringCompleted += delegate(object sender, DownloadStringCompletedEventArgs e)
            {
                BalanceSnapshot snap = new BalanceSnapshot();
                try
                {
                    if (e.Error != null)
                    {
                        Exception inner = e.Error;
                        if (inner is WebException && ((WebException)inner).Response != null)
                        {
                            WebException we = (WebException)inner;
                            using (StreamReader reader = new StreamReader(we.Response.GetResponseStream()))
                            {
                                string body = reader.ReadToEnd();
                                snap.Ok = false;
                                snap.Error = Shorten(body, 90);
                            }
                            int code = 0;
                            try { code = (int)((HttpWebResponse)we.Response).StatusCode; } catch { }
                            snap.Error = "HTTP " + code + " " + snap.Error;
                            Log.Write("余额接口错误: " + snap.Error);
                        }
                        else
                        {
                            snap.Ok = false;
                            snap.Error = "网络不通: " + Shorten(e.Error.Message, 70);
                            Log.Write("余额接口异常: " + e.Error.Message);
                        }
                    }
                    else
                    {
                        snap.Raw = e.Result;
                        Dictionary<string, object> root = Json.DeserializeObject(e.Result) as Dictionary<string, object>;
                        if (root == null) throw new Exception("返回内容无法解析");

                        object avail;
                        if (root.TryGetValue("is_available", out avail) && avail != null)
                            snap.IsAvailable = Convert.ToBoolean(avail);

                        object infosObj;
                        if (root.TryGetValue("balance_infos", out infosObj))
                        {
                            object[] list = infosObj as object[];
                            if (list != null && list.Length > 0)
                            {
                                Dictionary<string, object> first = list[0] as Dictionary<string, object>;
                                if (first != null)
                                {
                                    object cur;
                                    if (first.TryGetValue("currency", out cur) && cur != null) snap.Currency = cur.ToString();
                                    object total;
                                    if (first.TryGetValue("total_balance", out total) && total != null)
                                    {
                                        double parsed;
                                        if (double.TryParse(total.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                                            snap.Total = parsed;
                                    }
                                }
                            }
                        }
                        snap.Ok = true;
                        Log.Write("余额刷新成功: " + snap.Currency + " " + snap.Total.ToString("0.00", CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception ex)
                {
                    snap.Ok = false;
                    snap.Error = Shorten(ex.Message, 80);
                    Log.Write("解析余额失败: " + ex.Message);
                }
                try { client.Dispose(); } catch { }
                done(snap);
            };

            try
            {
                client.DownloadStringAsync(new Uri(apiBase.TrimEnd('/') + "/user/balance"));
            }
            catch (Exception ex)
            {
                fail.Ok = false;
                fail.Error = "发起请求失败: " + ex.Message;
                Log.Write(fail.Error);
                done(fail);
            }
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }

    internal static class Draw
    {
        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void String(Graphics g, string text, Font font, Brush brush, PointF at)
        {
            StringFormat fmt = StringFormat.GenericTypographic;
            fmt.FormatFlags |= StringFormatFlags.NoWrap;
            g.DrawString(text, font, brush, at, fmt);
        }

        public static SizeF Measure(Graphics g, string text, Font font)
        {
            StringFormat fmt = StringFormat.GenericTypographic;
            fmt.FormatFlags |= StringFormatFlags.NoWrap;
            return g.MeasureString(text, font, new PointF(0, 0), fmt);
        }

        // 矩形排版时 GDI+ 用的是字体步进宽度，比通用排版的紧贴宽度宽，测宽度要用这个
        public static SizeF MeasureWide(Graphics g, string text, Font font)
        {
            return g.MeasureString(text, font);
        }

        public static void StringIn(Graphics g, string text, Font font, Brush brush, RectangleF rect, StringAlignment align)
        {
            StringIn(g, text, font, brush, rect, align, StringTrimming.EllipsisCharacter);
        }

        public static void StringIn(Graphics g, string text, Font font, Brush brush, RectangleF rect, StringAlignment align, StringTrimming trimming)
        {
            using (StringFormat fmt = new StringFormat())
            {
                fmt.Alignment = align;
                fmt.LineAlignment = StringAlignment.Center;
                fmt.FormatFlags = StringFormatFlags.NoWrap;
                fmt.Trimming = trimming;
                g.DrawString(text, font, brush, rect, fmt);
            }
        }
    }

    internal sealed class WidgetForm : Form
    {
        private const int WsExLayered = 0x00080000;
        private const int WsExNoActivate = 0x08000000;
        private const int UlwAlpha = 0x00000002;
        private const byte AcSrcOver = 0x00;
        private const byte AcSrcAlpha = 0x01;

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref NativePoint pptDst, ref NativeSize psize, IntPtr hdcSrc, ref NativePoint pptSrc,
            int crKey, ref BlendFunction pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi,
            uint usage, out IntPtr bits, IntPtr section, uint offset);

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        // ---------- 外观常量（设计尺寸，1.0 缩放时） ----------
        private const int DesignWidth = 322;
        private const int DesignHeight = 302;
        private const float CharacterSize = 238f;
        private static readonly Color Navy = Color.FromArgb(255, 27, 42, 94);
        private static readonly Color NavySoft = Color.FromArgb(255, 34, 48, 107);
        private static readonly Color Grey = Color.FromArgb(255, 122, 134, 168);
        private static readonly Color AccentBlue = Color.FromArgb(255, 59, 130, 246);
        private static readonly Color AccentGreen = Color.FromArgb(255, 34, 197, 94);
        private static readonly Color AccentRed = Color.FromArgb(255, 220, 62, 62);
        private static readonly Color AccentOrange = Color.FromArgb(255, 234, 122, 20);

        private enum BubbleView { Balance, Period, Cheap }

        // DeepSeek 计价规则：工作日 9:00-12:00、14:00-18:00 为高峰，其余为空闲；周末全天空闲
        private static readonly int[][] PeakWindows = new int[][] { new int[] { 9, 12 }, new int[] { 14, 18 } };

        private static bool IsWeekend(DateTime time)
        {
            return time.DayOfWeek == DayOfWeek.Saturday || time.DayOfWeek == DayOfWeek.Sunday;
        }

        private static bool IsOffPeak(DateTime time)
        {
            if (IsWeekend(time)) return true;
            int minutes = time.Hour * 60 + time.Minute;
            for (int i = 0; i < PeakWindows.Length; i++)
            {
                int from = PeakWindows[i][0] * 60;
                int to = PeakWindows[i][1] * 60;
                if (minutes >= from && minutes < to) return false;
            }
            return true;
        }

        // 当前空闲时段结束后，下一个高峰从什么时候开始
        private static DateTime NextPeakStart(DateTime time)
        {
            DateTime cursor = time.Date.AddHours(PeakWindows[0][0]);
            while (true)
            {
                if (IsWeekend(cursor)) { cursor = cursor.Date.AddDays(1).AddHours(PeakWindows[0][0]); continue; }
                for (int i = 0; i < PeakWindows.Length; i++)
                {
                    DateTime start = cursor.Date.AddHours(PeakWindows[i][0]);
                    if (start > time) return start;
                }
                cursor = cursor.Date.AddDays(1).AddHours(PeakWindows[0][0]);
            }
        }

        private static string DurationText(TimeSpan span)
        {
            int total = (int)Math.Round(span.TotalMinutes);
            if (total < 1) total = 1;
            int hours = total / 60;
            int minutes = total % 60;
            if (hours > 0 && minutes == 0) return hours + " 小时";
            if (hours > 0) return hours + " 小时 " + minutes + " 分";
            return minutes + " 分钟";
        }

        // 「当前计价时段」这一泡的内容
        private static void BuildPeriodText(DateTime now, out string big, out string sub, out Color color)
        {
            if (IsOffPeak(now))
            {
                big = "空闲时段";
                color = AccentGreen;
                if (IsWeekend(now))
                {
                    sub = "周末全天闲时价 · 单价是高峰的一半";
                }
                else
                {
                    DateTime next = NextPeakStart(now);
                    string day = next.Date == now.Date ? "今天 " : (next.Date == now.Date.AddDays(1) ? "明天 " : "周一 ");
                    sub = "闲时价减半 · " + day + next.ToString("HH:mm") + " 转高峰";
                }
            }
            else
            {
                big = "高峰时段";
                color = AccentOrange;
                DateTime end = now.Hour < 12 ? now.Date.AddHours(12) : now.Date.AddHours(18);
                sub = "单价翻倍 · " + end.ToString("HH:mm") + " 转空闲";
            }
        }

        // 「什么时候更便宜」这一泡的内容
        private static void BuildCheapText(DateTime now, out string big, out string sub, out Color color)
        {
            if (!IsOffPeak(now))
            {
                DateTime end = now.Hour < 12 ? now.Date.AddHours(12) : now.Date.AddHours(18);
                big = end.ToString("HH:mm") + " 后";
                color = AccentGreen;
                sub = "还有 " + DurationText(end - now) + " · 转闲时减半";
                return;
            }

            big = IsWeekend(now) ? "全天便宜" : "现在最便宜";
            color = AccentGreen;
            DateTime next = NextPeakStart(now);
            string when = next.Date == now.Date
                ? "今天 " + next.ToString("HH:mm")
                : (next.Date == now.Date.AddDays(1) ? "明天 " + next.ToString("HH:mm") : "周一 " + next.ToString("HH:mm"));

            if (next.Date == now.Date)
                sub = "高峰 " + next.ToString("HH:mm") + " 开始 · 还有 " + DurationText(next - now);
            else
                sub = "下次高峰 " + when;
        }

        private readonly Config _config;
        private readonly UsageState _usage;
        private readonly Image _character;
        private readonly ContextMenuStrip _menu;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
        private readonly List<Form> _themeWatch = new List<Form>();

        private float _scale = 1f;
        private double _phase;
        private double _lastTick;
        private FetchState _state = FetchState.Idle;
        private BalanceSnapshot _snapshot;
        private double _shownValue = double.NaN;      // 正在显示（滚动中）的余额
        private double _fromValue = double.NaN;
        private double _toValue = double.NaN;
        private double _rollT = 1;
        private double _todayUsed;
        private double _spinner;
        private double _ringT = 2;
        private double _shakeT = 2;
        private double _squash;          // 0..1 按下程度
        private double _springVel;       // Q 弹速度
        private double _hover;           // 0..1 悬停程度
        private double _popT = 1;        // 气泡弹出进度
        private double _errShake;
        private double _autoTimer;
        private double _diagTimer;
        private double _snapT = 1.0;
        private int _snapFromX, _snapFromY, _snapToX, _snapToY;
        private BubbleView _view = BubbleView.Balance;
        private int _clickCount;
        private double _lastClickTime = -10;
        private double _hopY;
        private double _hopVel;
        private bool _mouseDown;
        private bool _dragged;
        private Point _dragOrigin;
        private Point _windowOrigin;
        private Font _fontTitle, _fontBig, _fontMid, _fontSmall, _fontTiny;
        private string _hoverTip = "";
        private Bitmap _frame;
        private byte[] _copyBuffer;
        private IntPtr _memDc = IntPtr.Zero;
        private IntPtr _dibBitmap = IntPtr.Zero;
        private IntPtr _dibBits = IntPtr.Zero;
        private IntPtr _oldSurface = IntPtr.Zero;
        private int _surfaceWidth;
        private int _surfaceHeight;
        private bool _loggedStride;

        public WidgetForm(Config config, UsageState usage)
        {
            _config = config;
            _usage = usage;
            _scale = (float)(_config.Scale * (GetDpiForSystem() / 96.0));
            _character = LoadCharacter(_config);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            TopMost = _config.AlwaysOnTop;
            Text = "DeepSeek 余额";
            BackColor = Color.Black;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            CreateFonts();
            _menu = BuildMenu();
            ApplyLayout(true);
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 33;
            _timer.Tick += OnTick;
            _timer.Start();
            _lastTick = 0;
            _clock.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExLayered | WsExNoActivate;
                return cp;
            }
        }

        private static Image LoadCharacter(Config config)
        {
            try
            {
                if (!string.IsNullOrEmpty(config.CharacterPng) && File.Exists(config.CharacterPng))
                {
                    using (Image raw = Image.FromFile(config.CharacterPng))
                        return CopyOf(raw);
                }
            }
            catch (Exception ex) { Log.Write("读取自定义角色图失败: " + ex.Message); }

            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("character.png"))
                {
                    if (s != null)
                    {
                        using (Image raw = Image.FromStream(s))
                            return CopyOf(raw);
                    }
                }
            }
            catch (Exception ex) { Log.Write("读取内置角色图失败: " + ex.Message); }
            return null;
        }

        private static Image CopyOf(Image source)
        {
            Bitmap copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(copy))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }
            return copy;
        }

        private void CreateFonts()
        {
            string family = PickFamily(new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑", "Segoe UI" });
            float k = _scale;
            // 必须用像素单位：用 Point 的话，在 150% 缩放的屏幕上会被再乘一次 1.5，
            // 文字比排版格子大出一半，副标题就会被截断成省略号
            _fontTitle = new Font(family, 14f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontBig = new Font(PickFamily(new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" }), 31f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            _fontMid = new Font(family, 13f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontSmall = new Font(family, 11f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontTiny = new Font(family, 10f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static string PickFamily(string[] candidates)
        {
            foreach (string name in candidates)
            {
                try
                {
                    FontFamily fam = new FontFamily(name);
                    return fam.Name;
                }
                catch { }
            }
            return FontFamily.GenericSansSerif.Name;
        }

        private void ApplyLayout()
        {
            ApplyLayout(false);
        }

        // initial = true 表示启动时摆放：默认居中，也可改成沿用上次位置
        private void ApplyLayout(bool initial)
        {
            int w = (int)Math.Round(DesignWidth * _scale);
            int h = (int)Math.Round(DesignHeight * _scale);
            ClientSize = new Size(w, h);

            int x, y;
            if (!initial)
            {
                x = Left;
                y = Top;
            }
            else if (_config.StartCentered)
            {
                Point center = CenterPosition(w, h);
                x = center.X;
                y = center.Y;
            }
            else if (_config.X == int.MinValue || _config.Y == int.MinValue)
            {
                Point fallback = DefaultPosition(w, h);
                x = fallback.X;
                y = fallback.Y;
            }
            else
            {
                x = _config.X;
                y = _config.Y;
            }

            Point safe = ClampToVisibleArea(x, y, w, h);
            if (safe.X != x || safe.Y != y)
            {
                Log.Write("上次的位置 (" + x + "," + y + ") 已经不在屏幕里，改放到 (" + safe.X + "," + safe.Y + ")");
                _config.X = safe.X;
                _config.Y = safe.Y;
                _config.Save();
            }
            Location = safe;
            Log.Write(string.Format(CultureInfo.InvariantCulture,
                "窗口位置 {0},{1}  尺寸 {2}x{3}{4}", safe.X, safe.Y, w, h, initial ? "（启动摆放）" : ""));
        }

        private Point CenterPosition(int w, int h)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
        }

        private Point DefaultPosition(int w, int h)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Right - w - (int)(18 * _scale), wa.Bottom - h - (int)(6 * _scale));
        }

        // 屏幕分辨率/缩放改过之后，上次的位置可能在屏幕外；这里把窗口拉回最近的屏幕里
        private static Point ClampToVisibleArea(int x, int y, int w, int h)
        {
            Rectangle box = new Rectangle(x, y, w, h);
            Screen best = Screen.PrimaryScreen;
            int bestDistance = int.MaxValue;
            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle inter = Rectangle.Intersect(box, screen.WorkingArea);
                if (inter.Width >= 60 && inter.Height >= 60) return new Point(x, y);

                Rectangle wa = screen.WorkingArea;
                int dx = 0, dy = 0;
                if (box.Right < wa.Left) dx = wa.Left - box.Right;
                else if (box.Left > wa.Right) dx = box.Left - wa.Right;
                if (box.Bottom < wa.Top) dy = wa.Top - box.Bottom;
                else if (box.Top > wa.Bottom) dy = box.Top - wa.Bottom;
                int distance = dx * dx + dy * dy;
                if (distance < bestDistance) { bestDistance = distance; best = screen; }
            }

            Rectangle target = best.WorkingArea;
            int nx = Math.Max(target.Left, Math.Min(x, target.Right - w));
            int ny = Math.Max(target.Top, Math.Min(y, target.Bottom - h));
            return new Point(nx, ny);
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;

            ToolStripMenuItem gestures = new ToolStripMenuItem("点一下＝刷新余额 ｜ 点两下＝当前时段 ｜ 点三下＝何时便宜");
            gestures.Enabled = false;
            menu.Items.Add(gestures);

            ToolStripMenuItem refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += delegate { RefreshBalance(); };
            menu.Items.Add(refresh);

            ToolStripMenuItem setKey = new ToolStripMenuItem("设置 API Key…");
            setKey.Click += delegate { PromptForApiKey(); };
            menu.Items.Add(setKey);

            ToolStripMenuItem auto = new ToolStripMenuItem("自动刷新（60 秒）");
            auto.CheckOnClick = true;
            auto.Checked = _config.AutoRefreshSeconds > 0;
            auto.Click += delegate(object s, EventArgs e)
            {
                ToolStripMenuItem item = (ToolStripMenuItem)s;
                _config.AutoRefreshSeconds = item.Checked ? 60 : 0;
                _autoTimer = 0;
                _config.Save();
            };
            menu.Items.Add(auto);

            ToolStripMenuItem top = new ToolStripMenuItem("窗口置顶");
            top.CheckOnClick = true;
            top.Checked = _config.AlwaysOnTop;
            top.Click += delegate(object s, EventArgs e)
            {
                _config.AlwaysOnTop = ((ToolStripMenuItem)s).Checked;
                TopMost = _config.AlwaysOnTop;
                _config.Save();
            };
            menu.Items.Add(top);

            ToolStripMenuItem size = new ToolStripMenuItem("大小");
            double[] steps = new double[] { 0.5, 0.7, 0.9, 1.15 };
            string[] labels = new string[] { "小", "中（默认）", "大", "特大" };
            for (int i = 0; i < steps.Length; i++)
            {
                double value = steps[i];
                ToolStripMenuItem item = new ToolStripMenuItem(labels[i]);
                item.Checked = Math.Abs(_config.Scale - value) < 0.01;
                item.Click += delegate
                {
                    _config.Scale = value;
                    _scale = (float)(value * (GetDpiForSystem() / 96.0));
                    DisposeFonts();
                    CreateFonts();
                    ApplyLayout();
                    foreach (ToolStripMenuItem sibling in size.DropDownItems) sibling.Checked = false;
                    item.Checked = true;
                    _config.Save();
                };
                size.DropDownItems.Add(item);
            }
            menu.Items.Add(size);

            ToolStripMenuItem snap = new ToolStripMenuItem("拖动后自动靠边吸附");
            snap.CheckOnClick = true;
            snap.Checked = _config.SnapEnabled;
            snap.Click += delegate(object s, EventArgs e)
            {
                _config.SnapEnabled = ((ToolStripMenuItem)s).Checked;
                _config.Save();
                Log.Write("靠边吸附开关: " + (_config.SnapEnabled ? "开" : "关"));
                if (_config.SnapEnabled) ApplySnap();
            };
            menu.Items.Add(snap);

            ToolStripMenuItem snapNow = new ToolStripMenuItem("立刻吸附到最近边缘（不管开关）");
            snapNow.Click += delegate
            {
                _hopVel = -140.0 * _scale;
                ApplySnap(true);
            };
            menu.Items.Add(snapNow);

            ToolStripMenuItem centerNow = new ToolStripMenuItem("启动时居中显示（在屏幕正中间）");
            centerNow.CheckOnClick = true;
            centerNow.Checked = _config.StartCentered;
            centerNow.Click += delegate(object s, EventArgs e)
            {
                _config.StartCentered = ((ToolStripMenuItem)s).Checked;
                _config.Save();
                Log.Write("启动居中开关: " + (_config.StartCentered ? "开" : "关"));
            };
            menu.Items.Add(centerNow);

            ToolStripMenuItem resetPos = new ToolStripMenuItem("立刻移到屏幕中间");
            resetPos.Click += delegate
            {
                Point target = CenterPosition(Width, Height);
                _snapFromX = Left;
                _snapFromY = Top;
                _snapToX = target.X;
                _snapToY = target.Y;
                _snapT = 0.0;
                _config.X = target.X;
                _config.Y = target.Y;
                _config.Save();
                Log.Write("手动移到屏幕中间 (" + target.X + "," + target.Y + ")");
            };
            menu.Items.Add(resetPos);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem copy = new ToolStripMenuItem("复制余额数字");
            copy.Click += delegate
            {
                try
                {
                    if (_snapshot != null && _snapshot.Ok)
                    {
                        Clipboard.SetText(_snapshot.Total.ToString("0.00", CultureInfo.InvariantCulture));
                        _hoverTip = "已复制";
                    }
                }
                catch { }
            };
            menu.Items.Add(copy);

            ToolStripMenuItem openConfig = new ToolStripMenuItem("打开配置文件");
            openConfig.Click += delegate
            {
                try { System.Diagnostics.Process.Start("notepad.exe", AppPaths.ConfigPath); }
                catch (Exception ex) { Log.Write("打开配置失败: " + ex.Message); }
            };
            menu.Items.Add(openConfig);

            ToolStripMenuItem openLog = new ToolStripMenuItem("查看运行日志");
            openLog.Click += delegate
            {
                try { System.Diagnostics.Process.Start("notepad.exe", AppPaths.LogPath); }
                catch (Exception ex) { Log.Write("打开日志失败: " + ex.Message); }
            };
            menu.Items.Add(openLog);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出挂件");
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);
            return menu;
        }

        private void DisposeFonts()
        {
            if (_fontTitle != null) _fontTitle.Dispose();
            if (_fontBig != null) _fontBig.Dispose();
            if (_fontMid != null) _fontMid.Dispose();
            if (_fontSmall != null) _fontSmall.Dispose();
            if (_fontTiny != null) _fontTiny.Dispose();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Log.Write("挂件启动，缩放 " + _scale.ToString("0.00", CultureInfo.InvariantCulture));
            if (string.IsNullOrEmpty(_config.ApiKey))
            {
                _state = FetchState.NoKey;
                Log.Write("还没配置 API Key，弹出输入窗口");
                BeginInvoke(new Action(PromptForApiKey));
            }
            else if (_config.RefreshOnStart) BeginInvoke(new Action(RefreshBalance));
        }

        // 弹 API Key 输入框；保存后立刻刷新
        private void PromptForApiKey()
        {
            try
            {
                using (ApiKeyDialog dialog = new ApiKeyDialog(_config.ApiBase, _config.ApiKey))
                {
                    if (dialog.ShowDialog() == DialogResult.OK && dialog.Saved)
                    {
                        _config.ApiKey = dialog.KeyValue;
                        _config.Save();
                        string prefix = _config.ApiKey.Length >= 6 ? _config.ApiKey.Substring(0, 6) : _config.ApiKey;
                        Log.Write("已更新 API Key（前 6 位 " + prefix + "）");
                        _view = BubbleView.Balance;
                        RefreshBalance();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("打开 API Key 窗口失败: " + Log.Describe(ex));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _config.X = Left;
            _config.Y = Top;
            _config.Save();
            _usage.Save();
            base.OnFormClosing(e);
        }

        // ---------------- 交互 ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _snapT = 1.0;
                _mouseDown = true;
                _dragged = false;
                _dragOrigin = Cursor.Position;
                _windowOrigin = Location;
                _springVel += 42;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_mouseDown)
            {
                Point now = Cursor.Position;
                int dx = now.X - _dragOrigin.X;
                int dy = now.Y - _dragOrigin.Y;
                if (!_dragged && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
                {
                    _dragged = true;
                    _squash = 0;
                    _springVel = 0;
                    Cursor = Cursors.SizeAll;
                }
                if (_dragged)
                {
                    Location = new Point(_windowOrigin.X + dx, _windowOrigin.Y + dy);
                    Render();
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                bool wasDrag = _dragged;
                _mouseDown = false;
                _dragged = false;
                Cursor = Cursors.Hand;
                _config.X = Left;
                _config.Y = Top;
                _config.Save();
                if (!wasDrag) HandleClick();
                else ApplySnap();
            }
        }

        // 点一下：刷新余额；连点两下：看当前时段；连点三下：看什么时候更便宜
        private void HandleClick()
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _lastClickTime <= 0.45 && _clickCount > 0) _clickCount++;
            else _clickCount = 1;
            _lastClickTime = now;

            _hopVel = -185.0 * _scale;   // 被点一下，弹一下

            if (_clickCount == 1)
            {
                _view = BubbleView.Balance;
                RefreshBalance();
            }
            else if (_clickCount == 2)
            {
                _view = BubbleView.Period;
                _popT = 0;
            }
            else if (_clickCount == 3)
            {
                _view = BubbleView.Cheap;
                _popT = 0;
            }
            else
            {
                _clickCount = 0;
                _view = BubbleView.Balance;
                _popT = 0;
            }
            Render();
        }

        // ---------------- 靠边吸附 ----------------

        // 算出吸附后的位置；返回 false 表示没靠近任何边缘
        private bool ComputeSnapTarget(out int x, out int y)
        {
            return ComputeSnapTarget(out x, out y, false);
        }

        private bool ComputeSnapTarget(out int x, out int y, bool force)
        {
            x = Left;
            y = Top;
            if (!_config.SnapEnabled && !force) return false;

            bool snapped = false;
            int taskbarGap = (int)Math.Round(_config.SnapDistance * _scale);
            int edgeGap = (int)Math.Round(24 * _scale);

            Rectangle work = Screen.FromControl(this).WorkingArea;
            Rectangle full = Screen.FromControl(this).Bounds;
            // 工作区比整屏窄/矮的那一边就是任务栏所在边；四边差都为 0 说明任务栏是自动隐藏的
            int taskbarBottom = full.Bottom - work.Bottom;
            int taskbarTop = work.Top - full.Top;
            int taskbarLeft = work.Left - full.Left;
            int taskbarRight = full.Right - work.Right;

            // 1) 任务栏那一条边：吸附距离给得大一些
            if (taskbarBottom > 0 && Math.Abs((Top + Height) - work.Bottom) <= taskbarGap) { y = work.Bottom - Height; snapped = true; }
            if (taskbarTop > 0 && Math.Abs(Top - work.Top) <= taskbarGap) { y = work.Top; snapped = true; }
            if (taskbarLeft > 0 && Math.Abs(Left - work.Left) <= taskbarGap) { x = work.Left; snapped = true; }
            if (taskbarRight > 0 && Math.Abs((Left + Width) - work.Right) <= taskbarGap) { x = work.Right - Width; snapped = true; }

            // 2) 屏幕边缘：吸附距离小一些
            if (Math.Abs(Left - work.Left) <= edgeGap) { x = work.Left; snapped = true; }
            if (Math.Abs((Left + Width) - work.Right) <= edgeGap) { x = work.Right - Width; snapped = true; }
            if (Math.Abs(Top - work.Top) <= edgeGap) { y = work.Top; snapped = true; }
            if (Math.Abs((Top + Height) - work.Bottom) <= edgeGap) { y = work.Bottom - Height; snapped = true; }

            return snapped;
        }

        private void ApplySnap()
        {
            ApplySnap(false);
        }

        private void ApplySnap(bool force)
        {
            int x, y;
            if (!ComputeSnapTarget(out x, out y, force)) return;
            if (x == Left && y == Top)
            {
                // 已经在边缘上了，给个弹跳当反馈
                if (force) _hopVel = -140.0 * _scale;
                return;
            }
            Log.Write("吸附: (" + Left + "," + Top + ") -> (" + x + "," + y + ")");
            _snapFromX = Left;
            _snapFromY = Top;
            _snapToX = x;
            _snapToY = y;
            _snapT = 0.0;
        }

        // 离线自测时段规则：不用等到真实时间点就能检查判断对不对
        public static string ScheduleReport()
        {
            DateTime[] samples = new DateTime[]
            {
                new DateTime(2026, 9, 16, 8, 0, 0),
                new DateTime(2026, 9, 16, 10, 30, 0),
                new DateTime(2026, 9, 16, 13, 0, 0),
                new DateTime(2026, 9, 16, 16, 0, 0),
                new DateTime(2026, 9, 16, 20, 0, 0),
                new DateTime(2026, 9, 19, 10, 0, 0),
                new DateTime(2026, 9, 20, 15, 0, 0)
            };
            string[] names = new string[]
            {
                "周三 08:00", "周三 10:30", "周三 13:00", "周三 16:00", "周三 20:00", "周六 10:00", "周日 15:00"
            };
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("时段规则自测：");
            for (int i = 0; i < samples.Length; i++)
            {
                string periodBig, periodSub, cheapBig, cheapSub;
                Color c1, c2;
                BuildPeriodText(samples[i], out periodBig, out periodSub, out c1);
                BuildCheapText(samples[i], out cheapBig, out cheapSub, out c2);
                sb.Append("  ").Append(names[i]).Append(" → ")
                  .Append(periodBig).Append("（").Append(periodSub).Append("）")
                  .Append("  ｜  便宜：").Append(cheapBig).Append("（").Append(cheapSub).Append("）")
                  .AppendLine();
            }
            return sb.ToString();
        }

        public string SnapDiagnostic(int testX, int testY)
        {
            Location = new Point(testX, testY);
            int x, y;
            bool ok = ComputeSnapTarget(out x, out y);
            StringBuilder sb = new StringBuilder();
            sb.Append(ok ? "会吸附" : "不吸附");
            sb.Append(": (").Append(testX).Append(",").Append(testY).Append(") -> (").Append(x).Append(",").Append(y).Append(")");
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Rectangle full = Screen.FromControl(this).Bounds;
            sb.Append("  整屏 ").Append(full.Width).Append("x").Append(full.Height);
            sb.Append("  工作区 ").Append(work.Width).Append("x").Append(work.Height);
            sb.Append("  边距[上").Append(work.Top - full.Top)
              .Append(" 下").Append(full.Bottom - work.Bottom)
              .Append(" 左").Append(work.Left - full.Left)
              .Append(" 右").Append(full.Right - work.Right).Append("]");
            sb.Append("  窗口 ").Append(Width).Append("x").Append(Height);
            return sb.ToString();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Cursor = Cursors.Default;
            if (!_mouseDown) _squash = 0;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Right)
            {
                UpdateMenuChecks();
                _menu.Show(this, new Point(e.X, e.Y));
            }
        }

        private void UpdateMenuChecks()
        {
            foreach (ToolStripItem item in _menu.Items)
            {
                ToolStripMenuItem mi = item as ToolStripMenuItem;
                if (mi == null) continue;
                if (mi.Text == "自动刷新（60 秒）") mi.Checked = _config.AutoRefreshSeconds > 0;
                if (mi.Text == "窗口置顶") mi.Checked = _config.AlwaysOnTop;
                if (mi.Text == "拖动后自动靠边吸附") mi.Checked = _config.SnapEnabled;
                if (mi.Text == "启动时居中显示（在屏幕正中间）") mi.Checked = _config.StartCentered;
            }
        }

        private void RefreshBalance()
        {
            if (_state == FetchState.Loading) return;
            if (string.IsNullOrEmpty(_config.ApiKey))
            {
                _state = FetchState.NoKey;
                _snapshot = new BalanceSnapshot();
                _snapshot.Ok = false;
                _snapshot.Error = "右键 → 设置 API Key";
                Render();
                _shakeT = 0;
                return;
            }
            _state = FetchState.Loading;
            _spinner = 0;
            _popT = 0;
            _autoTimer = 0;
            BalanceApi.BeginFetch(_config.ApiBase, _config.ApiKey, delegate(BalanceSnapshot snap)
            {
                try
                {
                    BeginInvoke(new Action(delegate { ApplySnapshot(snap); }));
                }
                catch { }
            });
        }

        private void ApplySnapshot(BalanceSnapshot snap)
        {
            _snapshot = snap;
            if (snap.Ok)
            {
                double previous = double.IsNaN(_shownValue) ? snap.Total : _shownValue;
                _fromValue = previous;
                _toValue = snap.Total;
                _rollT = 0;
                _state = FetchState.Ok;
                _ringT = 0;
                _todayUsed = _usage.Observe(snap.Total);
            }
            else
            {
                _state = FetchState.Error;
                _shakeT = 0;
            }
            _popT = 0;
            Render();
        }

        // ---------------- 动画 ----------------

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.WriteOnce("动画帧异常: " + Log.Describe(ex));
            }
        }

        private void Tick()
        {
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastTick;
            _lastTick = now;
            if (dt > 0.25) dt = 0.25;
            _phase += dt;

            if (_state == FetchState.Loading) _spinner += dt;

            // Q 弹：阻尼弹簧回到 0
            double accel = -_squash * 260.0 - _springVel * 11.0;
            _springVel += accel * dt;
            _squash += _springVel * dt;
            if (_squash < 0) { _squash = 0; if (_springVel < 0) _springVel = 0; }
            if (_squash > 1.2) _squash = 1.2;

            // 点击后的弹跳：平时完全静止，只有被点才动
            double hopAccel = -_hopY * 300.0 - _hopVel * 12.0;
            _hopVel += hopAccel * dt;
            _hopY += _hopVel * dt;
            if (Math.Abs(_hopY) < 0.05 && Math.Abs(_hopVel) < 0.6) { _hopY = 0; _hopVel = 0; }

            double now2 = _clock.Elapsed.TotalSeconds;
            if (_clickCount > 0 && now2 - _lastClickTime > 0.6) _clickCount = 0;

            double hoverTarget = _mouseDown || ClientRectangle.Contains(PointToClient(Cursor.Position)) ? 1.0 : 0.0;
            _hover += (hoverTarget - _hover) * Math.Min(1.0, dt * 12);

            if (_rollT < 1.0) _rollT = Math.Min(1.0, _rollT + dt / 0.7);
            if (_ringT < 1.0) _ringT = Math.Min(1.0, _ringT + dt / 0.9);
            if (_shakeT < 1.0) _shakeT = Math.Min(1.0, _shakeT + dt / 0.6);
            if (_popT < 1.0) _popT = Math.Min(1.0, _popT + dt / 0.32);

            if (_rollT < 1.0)
            {
                double t = 1.0 - Math.Pow(1.0 - _rollT, 3.0);
                _shownValue = _fromValue + (_toValue - _fromValue) * t;
            }
            else if (!double.IsNaN(_toValue)) _shownValue = _toValue;

            if (_state == FetchState.Error)
            {
                _errShake = Math.Sin(_shakeT * Math.PI * 10) * (1 - _shakeT) * 7 * _scale;
            }
            else _errShake = 0;

            if (_config.AutoRefreshSeconds > 0 && _state != FetchState.Loading)
            {
            _autoTimer += dt;
            if (_autoTimer >= _config.AutoRefreshSeconds) RefreshBalance();
            }

            _diagTimer += dt;
            if (_diagTimer >= 60.0)
            {
                _diagTimer = 0;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Log.Write(string.Format(CultureInfo.InvariantCulture,
                    "内存诊断: 工作集 {0:N1} MB, 回收后存活 {1:N1} MB",
                    Environment.WorkingSet / 1048576.0,
                    GC.GetTotalMemory(true) / 1048576.0));
            }

            if (_snapT < 1.0)
            {
                _snapT = Math.Min(1.0, _snapT + dt / 0.16);
                double eased = 1.0 - Math.Pow(1.0 - _snapT, 3.0);
                int nx = (int)Math.Round(_snapFromX + (_snapToX - _snapFromX) * eased);
                int ny = (int)Math.Round(_snapFromY + (_snapToY - _snapFromY) * eased);
                if (Location.X != nx || Location.Y != ny) Location = new Point(nx, ny);
                if (_snapT >= 1.0)
                {
                    _config.X = Left;
                    _config.Y = Top;
                    _config.Save();
                }
            }

            Render();
        }

        // ---------------- 绘制 ----------------

        private void Render()
        {
            if (!IsHandleCreated || IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) return;
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 4 || h <= 4) return;

            if (_frame == null || _frame.Width != w || _frame.Height != h)
            {
                if (_frame != null) _frame.Dispose();
                _frame = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            }
            using (Graphics g = Graphics.FromImage(_frame))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);
                DrawFrame(g);
            }
            Blit(_frame);
        }

        private void DrawFrame(Graphics g)
        {
            float k = _scale;

            // 按下 Q 弹 + 悬停放大
            double squash = _squash;
            double hover = _hover;
            double scaleX = 1.0 + 0.09 * squash - 0.012 * squash + 0.018 * hover;
            double scaleY = 1.0 - 0.11 * squash + 0.022 * hover;

            // 平时完全静止（一直浮动的话贴着任务栏会看着脱开），只有被点的一瞬间才弹一下
            double bob = _hopY;

            float charSize = CharacterSize * k;
            float charX = ClientSize.Width - charSize;
            float charY = ClientSize.Height - charSize;

            // 悬停 / 按下时整体以底部中心为锚点缩放（脚不动）
            double anchorX = charX + charSize / 2.0;
            double anchorY = charY + charSize;

            double headX = charX + charSize * 0.55;
            double headY = charY + charSize * 0.50;
            if (_state == FetchState.Loading) DrawSpinner(g, headX, headY, charSize * 0.42);
            if (_ringT < 1.0) DrawSuccessRing(g, headX, headY, charSize * 0.42, _ringT);

            GraphicsState state = g.Save();
            g.TranslateTransform((float)(anchorX + _errShake * 0.4), (float)(anchorY + bob));
            g.ScaleTransform((float)Math.Max(0.02, scaleX), (float)Math.Max(0.02, scaleY));
            g.TranslateTransform(-(float)anchorX, -(float)anchorY);

            if (_character != null)
            {
                g.DrawImage(_character, new RectangleF(charX, charY, charSize, charSize));
            }
            else
            {
                using (Brush placeholder = new SolidBrush(Color.FromArgb(200, 90, 120, 200)))
                    g.FillEllipse(placeholder, charX, charY, charSize, charSize);
            }
            g.Restore(state);

            DrawBubble(g, bob * 0.35);
        }

        private void DrawBubble(Graphics g, double bob)
        {
            float k = _scale;
            double pop = _popT;
            double eased = 1.0 - Math.Pow(1.0 - pop, 3.0);
            double overshoot = 1.0 + 0.06 * Math.Sin(Math.Min(1.0, pop) * Math.PI);
            // GDI+ 不接受 0 或负的缩放系数，弹出动画起始帧必须留一个下限
            float popScale = (float)Math.Max(0.02, eased * overshoot);

            RectangleF bubble = new RectangleF(6f * k, 4f * k, 272f * k, 98f * k);
            PointF tail1 = new PointF(208f * k, 116f * k + (float)bob);
            PointF tail2 = new PointF(240f * k, 136f * k + (float)bob);
            float tail1R = 13f * k;
            float tail2R = 7.5f * k;

            GraphicsState outer = g.Save();
            g.TranslateTransform((float)(bubble.X + bubble.Width / 2 + _errShake), (float)(bubble.Y + bubble.Height / 2));
            g.ScaleTransform(popScale, popScale);
            g.TranslateTransform(-(float)(bubble.X + bubble.Width / 2), -(float)(bubble.Y + bubble.Height / 2));

            float stroke = Math.Max(2f, 3.0f * k);

            // 尾部两个小泡泡（先画，压在气泡下面）
            using (Brush fill = new SolidBrush(Color.FromArgb(252, 255, 255, 255)))
            using (Pen pen = new Pen(NavySoft, stroke))
            {
                g.FillEllipse(fill, tail1.X - tail1R, tail1.Y - tail1R, tail1R * 2, tail1R * 2);
                g.DrawEllipse(pen, tail1.X - tail1R, tail1.Y - tail1R, tail1R * 2, tail1R * 2);
                g.FillEllipse(fill, tail2.X - tail2R, tail2.Y - tail2R, tail2R * 2, tail2R * 2);
                g.DrawEllipse(pen, tail2.X - tail2R, tail2.Y - tail2R, tail2R * 2, tail2R * 2);
            }

            using (GraphicsPath path = Draw.RoundedRect(bubble, 30f * k))
            {
                // 柔和投影
                for (int i = 3; i >= 1; i--)
                {
                    using (Pen shadow = new Pen(Color.FromArgb(10 + 6 * i, 20, 30, 70), stroke + i * 2f))
                    {
                        GraphicsState s2 = g.Save();
                        g.TranslateTransform(0, 2.2f * k);
                        g.DrawPath(shadow, path);
                        g.Restore(s2);
                    }
                }
                using (Brush fill = new SolidBrush(Color.FromArgb(252, 255, 255, 255)))
                    g.FillPath(fill, path);
                using (Pen pen = new Pen(NavySoft, stroke))
                    g.DrawPath(pen, path);
            }

            DrawBubbleText(g, bubble);
            g.Restore(outer);
        }

        private void DrawBubbleText(Graphics g, RectangleF bubble)
        {
            float k = _scale;
            float padX = 20f * k;
            float left = bubble.X + padX;
            float width = bubble.Width - padX * 2;

            using (Brush grey = new SolidBrush(Grey))
            using (Brush navy = new SolidBrush(Navy))
            using (Brush red = new SolidBrush(AccentRed))
            using (Brush green = new SolidBrush(AccentGreen))
            using (Brush orange = new SolidBrush(AccentOrange))
            {
                // 用矩形定位，三行各占各的格子，不依赖字体度量
                RectangleF titleRect = new RectangleF(left, bubble.Y + 6f * k, width, 20f * k);
                RectangleF bigRect = new RectangleF(left, bubble.Y + 26f * k, width, 46f * k);
                RectangleF subRect = new RectangleF(left, bubble.Y + 72f * k, width, 18f * k);

                string title = "DeepSeek 余额";
                string big;
                string sub;
                Brush bigBrush = navy;
                if (_view == BubbleView.Period)
                {
                    Color color;
                    title = "当前计价时段";
                    BuildPeriodText(DateTime.Now, out big, out sub, out color);
                    bigBrush = color == AccentGreen ? green : orange;
                }
                else if (_view == BubbleView.Cheap)
                {
                    Color color;
                    title = "什么时候更便宜";
                    BuildCheapText(DateTime.Now, out big, out sub, out color);
                    bigBrush = color == AccentGreen ? green : orange;
                }
                else if (_state == FetchState.Loading)
                {
                    big = "查询中" + new string('.', 1 + (int)(_phase * 2.2) % 3);
                    sub = "正在向 DeepSeek 查询…";
                }
                else if (_state == FetchState.Error)
                {
                    big = "查询失败";
                    sub = _snapshot == null ? "" : _snapshot.Error;
                    bigBrush = red;
                }
                else if (_state == FetchState.NoKey)
                {
                    big = "尚未配置 Key";
                    sub = _snapshot == null ? "右键 → 设置 API Key" : _snapshot.Error;
                    bigBrush = orange;
                }
                else
                {
                    string symbol = SymbolOf(_snapshot == null ? "CNY" : _snapshot.Currency);
                    double value = double.IsNaN(_shownValue) ? 0 : _shownValue;
                    big = symbol + value.ToString("0.00", CultureInfo.InvariantCulture);
                    sub = "今日已用 " + symbol + _todayUsed.ToString("0.00", CultureInfo.InvariantCulture)
                        + (_usage.LastCheck == "" ? "" : "  ·  " + _usage.LastCheck);
                }

                Draw.StringIn(g, title, _fontTitle, grey, titleRect, StringAlignment.Near);

                Font bigFont = _fontBig;
                Font shrunk = null;
                SizeF bigSize = Draw.MeasureWide(g, big, bigFont);
                if (bigSize.Width > bigRect.Width - 2f * k)
                {
                    float size = Math.Max(12f * k, bigFont.Size * (bigRect.Width - 4f * k) / bigSize.Width);
                    shrunk = new Font(bigFont.FontFamily, size, FontStyle.Bold, GraphicsUnit.Pixel);
                    bigFont = shrunk;
                }

                Draw.StringIn(g, big, bigFont, bigBrush, bigRect, StringAlignment.Near, StringTrimming.None);
                if (shrunk != null) shrunk.Dispose();

                // 副标题也按实际测量宽度自动缩小，避免被省略号截掉
                if (sub.Length > 0)
                {
                    Font subFont = _fontSmall;
                    Font subShrunk = null;
                    SizeF subSize = Draw.MeasureWide(g, sub, subFont);
                    if (subSize.Width > subRect.Width - 2f * k)
                    {
                        float size = Math.Max(7f * k, subFont.Size * (subRect.Width - 4f * k) / subSize.Width);
                        subShrunk = new Font(subFont.FontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
                        subFont = subShrunk;
                    }
                    Draw.StringIn(g, sub, subFont, grey, subRect, StringAlignment.Near);
                    if (subShrunk != null) subShrunk.Dispose();
                }

                if (!string.IsNullOrEmpty(_hoverTip))
                    Draw.StringIn(g, _hoverTip, _fontTiny, green, subRect, StringAlignment.Far);
                else if (_view == BubbleView.Balance && _state == FetchState.Ok && _ringT < 0.8)
                    Draw.StringIn(g, "✓", _fontSmall, green,
                        new RectangleF(bubble.Right - 22f * k, bubble.Y + 4f * k, 16f * k, 18f * k), StringAlignment.Far);
            }
        }

        private string Clamp(Graphics g, string text, Font font, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (Draw.Measure(g, text, font).Width <= maxWidth) return text;
            string cut = text;
            while (cut.Length > 4 && Draw.Measure(g, cut + "…", font).Width > maxWidth)
                cut = cut.Substring(0, cut.Length - 1);
            return cut + "…";
        }

        private static string SymbolOf(string currency)
        {
            if (string.IsNullOrEmpty(currency)) return "¥";
            switch (currency.ToUpperInvariant())
            {
                case "CNY": case "RMB": return "¥";
                case "USD": return "$";
                default: return currency + " ";
            }
        }

        private void DrawSpinner(Graphics g, double cx, double cy, double radius)
        {
            float k = _scale;
            float thick = 4.5f * k;
            RectangleF rect = new RectangleF((float)(cx - radius), (float)(cy - radius), (float)(radius * 2), (float)(radius * 2));
            using (Pen track = new Pen(Color.FromArgb(46, 59, 130, 246), thick))
                g.DrawEllipse(track, rect);
            float start = (float)((_spinner * 360.0 / 1.1) % 360.0);
            using (Pen arc = new Pen(AccentBlue, thick))
            {
                arc.StartCap = LineCap.Round;
                arc.EndCap = LineCap.Round;
                g.DrawArc(arc, rect, start, 78f);
            }
        }

        private void DrawSuccessRing(Graphics g, double cx, double cy, double baseRadius, double t)
        {
            float k = _scale;
            double eased = 1.0 - Math.Pow(1.0 - t, 2.4);
            double radius = baseRadius * (0.7 + 0.45 * eased);
            int alpha = (int)(150 * (1 - t));
            if (alpha <= 0) return;
            float thick = 3.0f * k * (float)(1 - t * 0.5);
            using (Pen pen = new Pen(Color.FromArgb(alpha, AccentGreen), thick))
            {
                pen.StartCap = LineCap.Round;
                RectangleF rect = new RectangleF((float)(cx - radius), (float)(cy - radius), (float)(radius * 2), (float)(radius * 2));
                g.DrawEllipse(pen, rect);
            }
        }

        // ---------------- 提交到层叠窗口 ----------------

        private void Blit(Bitmap frame)
        {
            int w = frame.Width;
            int h = frame.Height;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                EnsureSurface(screenDc, w, h);

                // 直接把预乘 alpha 像素搬进复用的 DIB，省掉每帧一次 Bitmap->HBITMAP 转换
                BitmapData data = frame.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    if (data.Stride == w * 4)
                    {
                        int total = w * h * 4;
                        if (_copyBuffer == null || _copyBuffer.Length < total) _copyBuffer = new byte[total];
                        Marshal.Copy(data.Scan0, _copyBuffer, 0, total);
                        Marshal.Copy(_copyBuffer, 0, _dibBits, total);
                    }
                    else
                    {
                        if (_copyBuffer == null || _copyBuffer.Length < w * 4) _copyBuffer = new byte[w * 4];
                        for (int y = 0; y < h; y++)
                        {
                            IntPtr source = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                            IntPtr target = new IntPtr(_dibBits.ToInt64() + (long)y * w * 4);
                            Marshal.Copy(source, _copyBuffer, 0, w * 4);
                            Marshal.Copy(_copyBuffer, 0, target, w * 4);
                        }
                    }
                    if (!_loggedStride)
                    {
                        _loggedStride = true;
                        Log.Write("绘制缓冲校验: stride=" + data.Stride + ", 期望=" + (w * 4) + ", 尺寸=" + w + "x" + h);
                    }
                }
                finally { frame.UnlockBits(data); }

                NativeSize size = new NativeSize();
                size.cx = w;
                size.cy = h;
                NativePoint src = new NativePoint();
                src.X = 0; src.Y = 0;
                NativePoint dst = new NativePoint();
                dst.X = Left; dst.Y = Top;
                BlendFunction blend = new BlendFunction();
                blend.BlendOp = AcSrcOver;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AcSrcAlpha;
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, UlwAlpha);
            }
            catch (Exception ex) { Log.Write("绘制失败: " + ex.Message); }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // 复用的绘制目标：一块和窗口同尺寸的 32bpp 自顶向下 DIB
        private void EnsureSurface(IntPtr screenDc, int w, int h)
        {
            if (_memDc != IntPtr.Zero && _surfaceWidth == w && _surfaceHeight == h) return;
            ReleaseSurface();

            _memDc = CreateCompatibleDC(screenDc);
            BitmapInfo info = new BitmapInfo();
            info.Header.Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader));
            info.Header.Width = w;
            info.Header.Height = -h;
            info.Header.Planes = 1;
            info.Header.BitCount = 32;
            info.Header.Compression = 0;
            _dibBitmap = CreateDIBSection(_memDc, ref info, 0, out _dibBits, IntPtr.Zero, 0);
            if (_dibBitmap == IntPtr.Zero) throw new InvalidOperationException("创建 DIB 失败");
            _oldSurface = SelectObject(_memDc, _dibBitmap);
            _surfaceWidth = w;
            _surfaceHeight = h;
        }

        private void ReleaseSurface()
        {
            if (_memDc != IntPtr.Zero)
            {
                if (_oldSurface != IntPtr.Zero) SelectObject(_memDc, _oldSurface);
                if (_dibBitmap != IntPtr.Zero) DeleteObject(_dibBitmap);
                DeleteDC(_memDc);
            }
            _memDc = IntPtr.Zero;
            _dibBitmap = IntPtr.Zero;
            _oldSurface = IntPtr.Zero;
            _dibBits = IntPtr.Zero;
            _surfaceWidth = 0;
            _surfaceHeight = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timer != null) { _timer.Stop(); _timer.Dispose(); }
                DisposeFonts();
                if (_character != null) _character.Dispose();
                if (_frame != null) _frame.Dispose();
                ReleaseSurface();
            }
            base.Dispose(disposing);
        }

        // ---------------- 供 --preview / --selftest 使用 ----------------

        // 自动化回归测试用：等价于点一下刷新
        public void SimulateClick()
        {
            RefreshBalance();
        }

        public void PreviewTo(string path, string state, double value, double todayUsed, string view)
        {
            _hover = 0.35;
            _phase = 0.8;
            _popT = 1.0;
            if (view == "period") _view = BubbleView.Period;
            else if (view == "cheap") _view = BubbleView.Cheap;
            else _view = BubbleView.Balance;
            if (state == "loading")
            {
                _state = FetchState.Loading;
                _spinner = 0.35;
            }
            else if (state == "error")
            {
                _state = FetchState.Error;
                _snapshot = new BalanceSnapshot();
                _snapshot.Ok = false;
                _snapshot.Error = "网络不通: 连接 api.deepseek.com 超时";
                _shakeT = 0.15;
                _errShake = Math.Sin(0.15 * Math.PI * 10) * (1 - 0.15) * 7 * _scale;
            }
            else if (state == "nokey")
            {
                _state = FetchState.NoKey;
                _snapshot = new BalanceSnapshot();
                _snapshot.Ok = false;
                _snapshot.Error = "右键 → 设置 API Key";
            }
            else
            {
                _state = FetchState.Ok;
                _snapshot = new BalanceSnapshot();
                _snapshot.Ok = true;
                _snapshot.Currency = "CNY";
                _snapshot.Total = value;
                _shownValue = value;
                _toValue = value;
                _rollT = 1;
                _ringT = 0.18;
                _todayUsed = todayUsed;
                _usage.LastCheck = DateTime.Now.ToString("HH:mm:ss");
            }

            int w = (int)Math.Round(DesignWidth * _scale);
            int h = (int)Math.Round(DesignHeight * _scale);
            using (Bitmap frame = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(frame))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.Clear(Color.Transparent);
                    ClientSize = new Size(w, h);
                    DrawFrame(g);
                }
                using (Bitmap composed = new Bitmap(w + 60, h + 60, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(composed))
                    {
                        g.Clear(Color.FromArgb(255, 30, 34, 44));
                        g.DrawImage(frame, 30, 30);
                    }
                    composed.Save(path, ImageFormat.Png);
                }
            }
            Log.Write("预览已生成: " + path);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            AppPaths.BaseDir = AppDomain.CurrentDomain.BaseDirectory;

            string preview = null;
            string previewState = "ok";
            string previewView = "balance";
            double previewValue = 19.33;
            double previewToday = 0.42;
            bool selfTest = false;
            string selfTestOut = null;
            int autoTestSeconds = 0;
            int snapTestX = int.MinValue, snapTestY = int.MinValue;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--preview" && i + 1 < args.Length) preview = args[++i];
                else if (a == "--state" && i + 1 < args.Length) previewState = args[++i];
                else if (a == "--view" && i + 1 < args.Length) previewView = args[++i];
                else if (a == "--value" && i + 1 < args.Length) double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out previewValue);
                else if (a == "--today" && i + 1 < args.Length) double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out previewToday);
                else if (a == "--selftest")
                {
                    selfTest = true;
                    if (i + 1 < args.Length) selfTestOut = args[++i];
                }
                else if (a == "--autotest")
                {
                    autoTestSeconds = 12;
                    int parsed;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out parsed)) autoTestSeconds = parsed;
                }
                else if (a == "--snaptest")
                {
                    int px, py;
                    if (i + 2 < args.Length && int.TryParse(args[i + 1], out px) && int.TryParse(args[i + 2], out py))
                    {
                        snapTestX = px;
                        snapTestY = py;
                        i += 2;
                    }
                }
                else if (a == "--schedule")
                {
                    Log.Write(WidgetForm.ScheduleReport());
                    return;
                }
                else if (a == "--keyshot")
                {
                    if (i + 1 < args.Length)
                    {
                        ApiKeyDialog.RenderPreview(args[++i], "sk-0000000000000000000000000000demo");
                        Log.Write("API Key 窗口预览已生成");
                    }
                    return;
                }
                else if (a.StartsWith("--basedir=")) AppPaths.BaseDir = a.Substring("--basedir=".Length);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log.Write("UI 线程异常: " + Log.Describe(e.Exception));
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Write("未处理异常: " + Log.Describe(e.ExceptionObject as Exception));
            };

            Config config = Config.Load();
            UsageState usage = UsageState.Load();

            if (selfTest)
            {
                ManualResetEvent done = new ManualResetEvent(false);
                BalanceSnapshot result = null;
                BalanceApi.BeginFetch(config.ApiBase, config.ApiKey, delegate(BalanceSnapshot snap)
                {
                    result = snap;
                    done.Set();
                });
                done.WaitOne(20000);
                string text = result == null
                    ? "超时：20 秒内没有返回"
                    : (result.Ok
                        ? "OK " + result.Currency + " " + result.Total.ToString("0.00", CultureInfo.InvariantCulture) + " raw=" + result.Raw
                        : "FAIL " + result.Error);
                if (selfTestOut != null) File.WriteAllText(selfTestOut, text, new UTF8Encoding(false));
                return;
            }

            if (preview != null)
            {
                using (WidgetForm form = new WidgetForm(config, usage))
                {
                    form.PreviewTo(preview, previewState, previewValue, previewToday, previewView);
                }
                return;
            }

            if (autoTestSeconds > 0)
            {
                WidgetForm form = new WidgetForm(config, usage);
                form.Shown += delegate
                {
                    System.Windows.Forms.Timer click = new System.Windows.Forms.Timer();
                    click.Interval = 2500;
                    click.Tick += delegate
                    {
                        click.Stop();
                        click.Dispose();
                        Log.Write("自动测试: 模拟点击刷新");
                        form.SimulateClick();
                    };
                    click.Start();

                    System.Windows.Forms.Timer stop = new System.Windows.Forms.Timer();
                    stop.Interval = (autoTestSeconds + 2) * 1000;
                    stop.Tick += delegate
                    {
                        stop.Stop();
                        stop.Dispose();
                        Log.Write("自动测试结束，进程存活");
                        form.Close();
                    };
                    stop.Start();
                };
                Application.Run(form);
                return;
            }

            if (snapTestX != int.MinValue)
            {
                using (WidgetForm form = new WidgetForm(config, usage))
                {
                    string report = form.SnapDiagnostic(snapTestX, snapTestY);
                    Log.Write("吸附自测: " + report);
                    Console.WriteLine(report);
                }
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "DeepSeekBalanceWidget.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("DeepSeek 余额挂件已经在运行了。", "DeepSeek 余额",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new WidgetForm(config, usage));
            }
        }
    }
}
