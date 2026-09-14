using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OmenSuperHub {
  public partial class FloatingForm : Form {
    private const int ContentPadding = 10;
    private const int ScreenMargin = 10;

    private PictureBox displayPictureBox;

    // ── 渲染缓存──────────────────────────────────────────────────
    // 目的：消除 250ms 周期内重复创建 Bitmap / Font / SolidBrush / StringFormat 造成的
    // Gen0 GC 压力，并通过脏检测跳过内容未变化的重绘。
    private Bitmap _backBuffer;                 // 复用的全彩位图缓冲（仅尺寸变化时重建）
    private Font _cachedFont;                   // 复用的字体对象
    private int _cachedFontSize;
    private StringFormat _textFormat;           // 复用的排版格式
    private readonly Dictionary<string, SolidBrush> _brushPool = new Dictionary<string, SolidBrush>();
    private SolidBrush _valueBrush;             // 复用的数值画刷
    private Bitmap _measureSurface;             // 度量用 1x1 位图（仅窗体句柄未就绪时使用）

    // 脏检测键：内容 + 字号 + 目标显示器 DeviceName + 目标工作区 Bounds。
    // 后两项不可省略 —— 否则跨显示器拖动或工作区变化（分辨率/DPI/任务栏）后不会重绘。
    private string _lastText;
    private int _lastTextSize;
    private string _lastScreenDevice;
    private Rectangle _lastScreenBounds;
    private bool _hasRendered;

    private sealed class DisplayLine {
      public string Title;
      public string Value;
      public float TitleWidth;
      public float ValueWidth;
    }

    public FloatingForm(string text, int textSize, string loc, Screen screen = null) {
      this.FormBorderStyle = FormBorderStyle.None;
      this.TopMost = true;
      this.ShowInTaskbar = false;
      this.StartPosition = FormStartPosition.Manual;

      displayPictureBox = new PictureBox();
      displayPictureBox.BackColor = Color.Transparent;
      displayPictureBox.SizeMode = PictureBoxSizeMode.AutoSize;
      this.Controls.Add(displayPictureBox);
      this.HandleCreated += (s, e) => RenderCurrentImage();

      ApplySupersampling(text, textSize, screen);
      AdjustFormSize();
      SetAnchoredPosition(loc, screen);
    }

    // ── 缓存获取器 ───────────────────────────────────────────────────────
    private Font GetOrCreateFont(int size) {
      if (_cachedFont == null || _cachedFontSize != size) {
        var previous = _cachedFont;
        _cachedFont = new Font("Calibri", size, FontStyle.Bold, GraphicsUnit.World);
        _cachedFontSize = size;
        previous?.Dispose();
      }
      return _cachedFont;
    }

    private SolidBrush GetCachedBrush(Color color) {
      string key = color.ToArgb().ToString();
      SolidBrush brush;
      if (!_brushPool.TryGetValue(key, out brush)) {
        brush = new SolidBrush(color);
        _brushPool[key] = brush;
      }
      return brush;
    }

    private SolidBrush ValueBrush {
      get {
        if (_valueBrush == null)
          _valueBrush = new SolidBrush(Color.FromArgb(255, 128, 0));
        return _valueBrush;
      }
    }

    private StringFormat TextFormat {
      get {
        if (_textFormat == null)
          _textFormat = CreateTextFormat();
        return _textFormat;
      }
    }

    /// <summary>
    /// 获取度量用 Graphics：优先复用窗体自身的设备上下文（避免每次分配临时位图）；
    /// 窗体句柄尚未创建（构造函数早期）时，退回一个**复用**的 1x1 度量位图。
    /// 调用方负责 Dispose 返回的 Graphics。
    /// </summary>
    private Graphics CreateMeasureGraphics() {
      if (IsHandleCreated) {
        try { return this.CreateGraphics(); } catch { /* 句柄竞态时退回度量位图 */ }
      }
      if (_measureSurface == null)
        _measureSurface = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
      return Graphics.FromImage(_measureSurface);
    }

    /// <summary>
    /// 按需重建悬浮窗位图。返回 true 表示位图内容已重绘（调用方需重新呈现），
    /// 返回 false 表示脏检测命中或渲染失败，位图内容与上次一致。
    /// </summary>
    private bool ApplySupersampling(string text, int textSize, Screen screen) {
      if (string.IsNullOrEmpty(text) || textSize <= 0)
        return false;

      var targetScreen = screen ?? Screen.PrimaryScreen;
      var workingArea = targetScreen.WorkingArea;

      // ── 脏检测 ─────────────────────────────────────────────────────────
      if (_hasRendered
          && text == _lastText
          && textSize == _lastTextSize
          && targetScreen.DeviceName == _lastScreenDevice
          && workingArea == _lastScreenBounds) {
        return false;   // 内容与目标显示环境均未变化，跳过整条重绘管线
      }

      int maxBitmapWidth = Math.Max(1, workingArea.Width - ScreenMargin * 2);
      float maxContentWidth = Math.Max(1, maxBitmapWidth - ContentPadding * 2);

      Bitmap previousBuffer = _backBuffer;
      Bitmap newBuffer = null;
      bool bufferRecreated = false;

      try {
        var font = GetOrCreateFont(textSize);
        var format = TextFormat;

        using (var measureGraphics = CreateMeasureGraphics()) {
          var lines = BuildDisplayLines(text, font, measureGraphics, format, maxContentWidth);
          float lineHeight = (float)Math.Ceiling(font.GetHeight(measureGraphics));
          float widestLine = 1;
          foreach (var line in lines)
            widestLine = Math.Max(widestLine, line.TitleWidth + line.ValueWidth);

          int bitmapWidth = Math.Min(maxBitmapWidth,
            Math.Max(1, (int)Math.Ceiling(widestLine) + ContentPadding * 2));
          int bitmapHeight = Math.Max(1,
            (int)Math.Ceiling(lineHeight * lines.Count) + ContentPadding * 2);

          // 位图缓冲仅在尺寸变化时重建
          if (previousBuffer == null
              || previousBuffer.Width != bitmapWidth
              || previousBuffer.Height != bitmapHeight) {
            newBuffer = new Bitmap(bitmapWidth, bitmapHeight, PixelFormat.Format32bppArgb);
            bufferRecreated = true;
          } else {
            newBuffer = previousBuffer;
          }

          using (Graphics graphics = Graphics.FromImage(newBuffer)) {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // graphics.Clear(Color.FromArgb(180, 0, 0, 0));
            //透明
            graphics.Clear(Color.Transparent);

            float y = ContentPadding;
            foreach (var line in lines) {
              float x = ContentPadding;
              if (!string.IsNullOrEmpty(line.Title)) {
                string titleKey = line.Title.TrimEnd(':').Trim();
                graphics.DrawString(line.Title, font, GetCachedBrush(GetColorForTitle(titleKey)),
                                    new PointF(x, y), format);
                x += line.TitleWidth;
              }

              graphics.DrawString(line.Value, font, ValueBrush, new PointF(x, y), format);
              y += lineHeight;
            }
          }
        }
      } catch (ArgumentException ex) {
        if (bufferRecreated) newBuffer?.Dispose();   // 失败时丢弃新建缓冲，保留原缓冲
        System.Diagnostics.Debug.WriteLine($"Bitmap 创建失败: {ex.Message}");
        return false;
      }

      // 先切换引用，再释放旧缓冲（旧缓冲此刻已无任何引用者）
      _backBuffer = newBuffer;
      displayPictureBox.Image = newBuffer;
      displayPictureBox.Size = newBuffer.Size;
      if (bufferRecreated)
        previousBuffer?.Dispose();

      _lastText = text;
      _lastTextSize = textSize;
      _lastScreenDevice = targetScreen.DeviceName;
      _lastScreenBounds = workingArea;
      _hasRendered = true;
      return true;
    }

    private static StringFormat CreateTextFormat() {
      var format = (StringFormat)StringFormat.GenericTypographic.Clone();
      format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
      return format;
    }

    private static float MeasureText(Graphics graphics, string text, Font font, StringFormat format) {
      if (string.IsNullOrEmpty(text)) return 0;
      return graphics.MeasureString(text, font, int.MaxValue, format).Width;
    }

    private static List<DisplayLine> BuildDisplayLines(string text, Font font,
        Graphics graphics, StringFormat format, float maxContentWidth) {
      var result = new List<DisplayLine>();
      string[] sourceLines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

      foreach (string sourceLine in sourceLines) {
        int separatorIndex = sourceLine.IndexOf(':');
        string title = separatorIndex >= 0 ? sourceLine.Substring(0, separatorIndex).Trim() + ":" : "";
        string value = separatorIndex >= 0 ? sourceLine.Substring(separatorIndex + 1).Trim() : sourceLine.Trim();
        string valuePrefix = title.Length > 0 ? " " : "";
        float titleWidth = MeasureText(graphics, title, font, format);
        string[] segments = value.Split(new[] { ',' }, StringSplitOptions.None);
        string current = "";
        bool firstOutputLine = true;

        for (int i = 0; i < segments.Length; i++) {
          string segment = segments[i].Trim();
          if (i < segments.Length - 1) segment += ",";
          string candidate = current.Length == 0 ? segment : current + " " + segment;
          float prefixWidth = firstOutputLine ? titleWidth : 0;
          float candidateWidth = MeasureText(graphics, valuePrefix + candidate, font, format);

          if (current.Length > 0 && prefixWidth + candidateWidth > maxContentWidth) {
            AddDisplayLine(result, firstOutputLine ? title : "", valuePrefix + current,
              font, graphics, format);
            firstOutputLine = false;
            valuePrefix = "";
            current = segment;
          } else {
            current = candidate;
          }
        }

        AddDisplayLine(result, firstOutputLine ? title : "", valuePrefix + current,
          font, graphics, format);
      }

      if (result.Count == 0)
        AddDisplayLine(result, "", " ", font, graphics, format);
      return result;
    }

    private static void AddDisplayLine(List<DisplayLine> lines, string title, string value,
        Font font, Graphics graphics, StringFormat format) {
      lines.Add(new DisplayLine {
        Title = title,
        Value = value,
        TitleWidth = MeasureText(graphics, title, font, format),
        ValueWidth = MeasureText(graphics, value, font, format)
      });
    }

    private Color GetColorForTitle(string title) {
      switch (title) {
        case "CPU": return Color.FromArgb(0, 128, 192);
        case "GPU": return Color.FromArgb(0, 128, 192);
        case "Fan": return Color.FromArgb(0, 128, 64);
        default:    return Color.FromArgb(0, 128, 192);
      }
    }

    public void SetText(string text, int textSize, string loc, Screen screen = null) {
      if (IsDisposed) return;   // 窗体已释放：缓存对象均已 Dispose，直接忽略
      if (InvokeRequired) {
        BeginInvoke(new Action(() => SetText(text, textSize, loc, screen)));
        return;
      }
      if (textSize <= 0) return;

      // 脏检测命中且锚定位置未变化时跳过整条呈现管线：否则 RenderLayered 中的
      // GetHbitmap 仍会每帧复制整张位图并调用 UpdateLayeredWindow（250ms 档下每秒 4 次）。
      bool contentChanged = ApplySupersampling(text, textSize, screen);
      if (contentChanged) AdjustFormSize();

      Point previousLocation = this.Location;
      SetAnchoredPosition(loc, screen);          // 位置变化时 OnMove 已用最新位图调用 RenderLayered
      bool moved = this.Location != previousLocation;

      if (contentChanged && !moved)
        RenderCurrentImage();
    }

    private void AdjustFormSize() {
      this.Size = displayPictureBox.Size;
      displayPictureBox.Location = Point.Empty;
    }

    protected override void OnMove(EventArgs e) {
      base.OnMove(e);
      if (displayPictureBox?.Image is Bitmap bmp && IsHandleCreated)
        RenderLayered(bmp);
    }

    protected override CreateParams CreateParams {
      get {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= WS_EX_LAYERED
                   | WS_EX_TRANSPARENT
                   | WS_EX_NOACTIVATE;
        return cp;
      }
    }

    public void SetPositionTopLeft(Screen screen = null) {
      SetAnchoredPosition("left", screen);
    }

    public void SetPositionTopRight(int textSize, Screen screen = null) {
      SetAnchoredPosition("right", screen);
    }

    private void SetAnchoredPosition(string loc, Screen screen) {
      var wa = (screen ?? Screen.PrimaryScreen).WorkingArea;
      int desiredX = loc == "left"
        ? wa.Left + ScreenMargin
        : wa.Right - this.Width - ScreenMargin;
      int desiredY = wa.Top + ScreenMargin;
      int maxX = wa.Right - this.Width;
      int maxY = wa.Bottom - this.Height;

      int x = maxX < wa.Left ? wa.Left : Math.Max(wa.Left, Math.Min(desiredX, maxX));
      int y = maxY < wa.Top ? wa.Top : Math.Max(wa.Top, Math.Min(desiredY, maxY));
      this.Location = new Point(x, y);
    }

    private void RenderCurrentImage() {
      if (displayPictureBox?.Image is Bitmap bitmap && IsHandleCreated)
        RenderLayered(bitmap);
    }

    private void RenderLayered(Bitmap bitmap) {
      if (bitmap == null) return;

      IntPtr screenDC = IntPtr.Zero;
      IntPtr memDC = IntPtr.Zero;
      IntPtr hBitmap = IntPtr.Zero;
      IntPtr oldBitmap = IntPtr.Zero;

      try {
        screenDC = GetDC(IntPtr.Zero);
        if (screenDC == IntPtr.Zero) return;
        memDC = CreateCompatibleDC(screenDC);
        if (memDC == IntPtr.Zero) return;
        hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        if (hBitmap == IntPtr.Zero) return;
        oldBitmap = SelectObject(memDC, hBitmap);

        NativeSize size = new NativeSize(bitmap.Width, bitmap.Height);
        NativePoint ptSrc = new NativePoint(0, 0);
        NativePoint ptDst = new NativePoint(this.Left, this.Top);

        BLENDFUNCTION blend = new BLENDFUNCTION {
          BlendOp = AC_SRC_OVER,
          BlendFlags = 0,
          SourceConstantAlpha = 255,
          AlphaFormat = AC_SRC_ALPHA
        };

        UpdateLayeredWindow(this.Handle, screenDC, ref ptDst, ref size,
                            memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);
      } catch (Exception ex) {
        // 显卡重置 / 分辨率变更 / 句柄失效等异常不得导致 GDI 句柄泄漏
        System.Diagnostics.Debug.WriteLine($"RenderLayered 失败: {ex.Message}");
      } finally {
        // 释放顺序与获取顺序严格相反；每一步独立 try/catch，
        // 确保异常路径下 GDI 句柄数不增长。
        try { if (memDC != IntPtr.Zero && oldBitmap != IntPtr.Zero) SelectObject(memDC, oldBitmap); } catch { }
        try { if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap); } catch { }
        try { if (memDC != IntPtr.Zero) DeleteDC(memDC); } catch { }
        try { if (screenDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDC); } catch { }
      }
    }

    /// <summary>
    /// 释放全部渲染缓存对象。
    /// 缓存对象（位图缓冲 / 字体 / 画刷池 / 排版格式 / 度量位图）均为本窗体独占，
    /// 必须在窗体释放时显式 Dispose，否则会在托管堆外滞留 GDI 句柄。
    /// </summary>
    protected override void Dispose(bool disposing) {
      if (disposing) {
        // 先解除 PictureBox 对位图的引用，再释放位图，避免释放后仍被访问
        if (displayPictureBox != null)
          displayPictureBox.Image = null;

        _backBuffer?.Dispose();
        _backBuffer = null;

        _cachedFont?.Dispose();
        _cachedFont = null;
        _cachedFontSize = 0;

        foreach (var brush in _brushPool.Values)
          brush.Dispose();
        _brushPool.Clear();

        _valueBrush?.Dispose();
        _valueBrush = null;

        _textFormat?.Dispose();
        _textFormat = null;

        _measureSurface?.Dispose();
        _measureSurface = null;

        displayPictureBox?.Dispose();
        displayPictureBox = null;
      }

      base.Dispose(disposing);
    }

    // ── 常量 ─────────────────────────────────────────────────────────────
    private const int  WS_EX_LAYERED     = 0x80000;
    private const int  WS_EX_TRANSPARENT = 0x20;
    private const int  WS_EX_NOACTIVATE  = 0x08000000;
    private const int  ULW_ALPHA         = 0x02;
    private const byte AC_SRC_OVER       = 0x00;
    private const byte AC_SRC_ALPHA      = 0x01;

    // ── 结构体 ───────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize  { public int cx, cy; public NativeSize(int x, int y)  { cx = x; cy = y; } }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int x,  y;  public NativePoint(int x, int y) { this.x = x; this.y = y; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    // ── P/Invoke ─────────────────────────────────────────────────────────
    [DllImport("user32.dll")] static extern bool   UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize, IntPtr hdcSrc, ref NativePoint pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int    ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")]  static extern bool   DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")]  static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")]  static extern bool   DeleteObject(IntPtr hObject);
  }
}
