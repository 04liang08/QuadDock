using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;
using System.Windows.Forms;
using QuadDock.Core;

namespace QuadDock.UI
{
    /// <summary>
    /// 一条边上的侧栏：列出它此刻压着的那个桌面上的窗口。
    /// 点标题把这个桌面换到中央并激活窗口，点空白只交换桌面；
    /// 拖动内边缘调宽窄，双击复位。
    ///
    /// 左右两侧窄的时候改走竖排：每行是一条"书脊"，文字转 90° 顺着边跑，
    /// 一行能放下整条标题，不用再截成三四个字。
    /// </summary>
    internal sealed class StripForm : Form, IMessageFilter
    {
        private sealed class Slot
        {
            public IntPtr Handle;
            public string Title;
            public Guid DesktopId;
            public bool Minimized;
            public Bitmap Icon;
            public Rectangle Rect;
            public bool Hover;

            /// <summary>竖排时把"图标 + 标题"预渲染成一条横向小图，贴的时候整体转 90°。</summary>
            public Bitmap Label;
            public string LabelKey;

            public void DropLabel()
            {
                if (Label != null)
                {
                    Label.Dispose();
                    Label = null;
                }
                LabelKey = null;
            }
        }

        private const int PadLogical = 10;
        private const int GapLogical = 8;
        private const int ChipPadXLogical = 8;
        private const int ChipPadYLogical = 6;
        private const int RadiusLogical = 10;
        private const int BarLogical = 3;
        private const int IconLogical = 16;
        private const int IconGapLogical = 5;

        /// <summary>窄于这个宽度（逻辑像素）的左右侧栏走竖排。</summary>
        private const int SpineThresholdLogical = 150;

        private readonly Edge _edge;
        private readonly DockManager _manager;
        private readonly List<Slot> _slots = new List<Slot>();
        private readonly ToolTip _tip = new ToolTip();

        private int _scroll;
        private int _content;
        private int _gap;
        private int _chipPadX;
        private int _chipPadY;
        private int _hoverIndex = -1;
        private int _pressIndex = -1;
        private bool _pressed;
        private bool _resizing;
        private bool _spines;

        /// <summary>竖排专用字号和图标尺寸，跟着书脊宽度变。</summary>
        private Font _spineFont;
        private int _spineFontWidth;
        private int _spineIcon;
        private int _resizePointerStart;
        private int _resizeThicknessStart;
        private int _lastDragTick;
        private string _signature = "";
        private int _desktopNumber;
        private IntPtr _recent;

        public StripForm(Edge edge, DockManager manager)
        {
            _edge = edge;
            _manager = manager;

            Text = "QuadDock";
            Font = Theme.UiFont;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            Bounds = InitialBounds(edge);
            SetStyle(
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw,
                true);

            _tip.InitialDelay = 400;
            _tip.ReshowDelay = 120;
            _tip.AutoPopDelay = 10000;

            Application.AddMessageFilter(this);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= (int)(Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
                return cp;
            }
        }

        internal void SyncBounds(Rectangle bounds)
        {
            if (Bounds != bounds)
            {
                Bounds = bounds;
            }
        }

        /// <summary>这条边压着第几个桌面。有内容时画成背景水印，空的时候画成居中提示。</summary>
        internal void SetDesktopNumber(int number)
        {
            if (_desktopNumber == number)
            {
                return;
            }

            _desktopNumber = number;
            Invalidate();
        }

        /// <summary>
        /// 标记"上次在这个桌面上用过的窗口"。
        /// 注意不能用前台窗口：四条侧栏装的全是非中央桌面，而前台窗口一定在中央那个桌面上，
        /// 所以前台标记永远不会亮。这里由扫描时记下的最近前台窗口来标。
        /// </summary>
        internal void SetRecent(IntPtr hwnd)
        {
            if (_recent == hwnd)
            {
                return;
            }

            _recent = hwnd;
            Invalidate();
        }

        internal void SetWindows(List<DesktopWindow> windows)
        {
            string signature = BuildSignature(windows);
            if (signature == _signature)
            {
                return;
            }
            _signature = signature;

            foreach (Slot old in _slots)
            {
                old.DropLabel();
            }
            _slots.Clear();

            foreach (DesktopWindow window in windows)
            {
                _slots.Add(new Slot
                {
                    Handle = window.Handle,
                    Title = window.Title,
                    DesktopId = window.DesktopId,
                    Minimized = window.IsMinimized,
                    // 图标只在列表变化时取一次，按 exe 缓存，不会每帧查盘
                    Icon = AppIcons.ForWindow(window.Handle)
                });
            }

            _hoverIndex = -1;
            _pressIndex = -1;
            _pressed = false;
            Relayout();
        }

        private static string BuildSignature(List<DesktopWindow> windows)
        {
            var builder = new StringBuilder(windows.Count * 48);
            foreach (DesktopWindow window in windows)
            {
                builder.Append(window.Handle.ToInt64())
                       .Append(':')
                       .Append(window.DesktopId.ToString())
                       .Append(window.IsMinimized ? '1' : '0')
                       .Append(window.Title)
                       .Append('\n');
            }
            return builder.ToString();
        }

        // ---------- 布局 ----------

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated && !IsDisposed)
            {
                Relayout();
            }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            Relayout();
        }

        private void Relayout()
        {
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }

            int width = ClientSize.Width;
            int height = ClientSize.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int dpi = DeviceDpi;
            bool vertical = _edge.IsVertical();
            int thickness = vertical ? width : height;

            _spines = vertical && thickness < Dpi.Scale(SpineThresholdLogical, dpi);
            bool narrow = thickness < Dpi.Scale(150, dpi);

            int pad = Dpi.Scale(narrow ? 6 : PadLogical, dpi);
            int gap = Dpi.Scale(narrow ? 6 : GapLogical, dpi);
            int chipPadX = Dpi.Scale(ChipPadXLogical, dpi);
            int chipPadY = Dpi.Scale(ChipPadYLogical, dpi);
            if (!_spines && chipPadX * 2 >= width)
            {
                chipPadX = Math.Max(2, (width - Dpi.Scale(12, dpi)) / 2);
            }

            _gap = gap;
            _chipPadX = chipPadX;
            _chipPadY = chipPadY;

            if (_spines)
            {
                LayoutSpines(dpi, pad, gap, chipPadX);
            }
            else
            {
                LayoutRows(dpi, pad, gap, chipPadX, chipPadY, vertical);
            }

            Invalidate();
        }

        /// <summary>横排：标题一行一条，宽度按文字长度自适应（上/下边栏和很宽的左右栏走这条）。</summary>
        private void LayoutRows(int dpi, int pad, int gap, int chipPadX, int chipPadY, bool vertical)
        {
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            int viewport = vertical ? height : width;

            int chipWidth = Math.Max(Dpi.Scale(24, dpi), width - pad * 2);
            int chipHeight = vertical
                ? Math.Max(Dpi.Scale(28, dpi), Font.Height + chipPadY * 2)
                : Math.Max(Dpi.Scale(22, dpi), height - pad * 2);

            var sizes = new int[_slots.Count];
            int total = _slots.Count == 0 ? 0 : pad * 2 + (_slots.Count - 1) * gap;
            for (int i = 0; i < _slots.Count; i++)
            {
                int size;
                if (vertical)
                {
                    size = chipHeight;
                }
                else
                {
                    int iconWidth = _slots[i].Icon != null
                        ? Dpi.Scale(IconLogical, dpi) + Dpi.Scale(IconGapLogical, dpi)
                        : 0;
                    size = Math.Max(
                        Dpi.Scale(70, dpi),
                        Math.Min(Dpi.Scale(300, dpi), Measure(_slots[i].Title) + chipPadX * 2 + iconWidth));
                }
                sizes[i] = size;
                total += size;
            }

            _content = total;
            int maxScroll = Math.Max(0, _content - viewport);
            _scroll = Math.Max(0, Math.Min(_scroll, maxScroll));

            int offset = pad - _scroll;
            for (int i = 0; i < _slots.Count; i++)
            {
                _slots[i].Rect = vertical
                    ? new Rectangle(pad, offset, chipWidth, sizes[i])
                    : new Rectangle(offset, pad, sizes[i], chipHeight);
                offset += sizes[i] + gap;
            }
        }

        /// <summary>
        /// 竖排：每一行是一条竖着的"书脊"，文字旋转 90° 顺着边跑，于是整条标题都能放进来。
        /// 宽度跟着侧栏走——侧栏加宽时把书脊本身加宽，而不是往里多塞几列；
        /// 加宽到上限之后再多出来的宽度就留白，两侧居中。
        /// </summary>
        private void LayoutSpines(int dpi, int pad, int gap, int chipPad)
        {
            int width = ClientSize.Width;
            int height = ClientSize.Height;

            int minSpine = Dpi.Scale(22, dpi);
            int maxSpineWidth = Dpi.Scale(48, dpi);

            int usable = Math.Max(minSpine, width - pad * 2);

            int columns = usable < minSpine * 2 + gap ? 1 : 2;
            int spineWidth = Math.Min(maxSpineWidth, (usable - (columns - 1) * gap) / columns);
            int block = columns * spineWidth + (columns - 1) * gap;
            int startX = pad + Math.Max(0, (width - pad * 2 - block) / 2);

            UpdateSpineMetrics(dpi, spineWidth);
            int iconSize = _spineIcon;
            int iconGap = Dpi.Scale(IconGapLogical, dpi);

            // 单条再长也留一半给别的窗口，超出的截断
            int maxSpine = Math.Max(Dpi.Scale(120, dpi), height / 2);

            var heights = new int[_slots.Count];
            int total = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                int extra = _slots[i].Icon != null ? iconSize + iconGap : 0;
                int h = chipPad * 2 + extra + MeasureSpine(_slots[i].Title);
                h = Math.Max(h, Dpi.Scale(52, dpi));
                h = Math.Min(h, maxSpine);
                heights[i] = h;
                total += h + gap;
            }
            if (total > 0)
            {
                total -= gap;
            }

            // 按累计高度分配列，尽量让每列一样高
            var columnOf = new int[_slots.Count];
            var columnHeight = new int[columns];
            int before = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                int column = total <= 0
                    ? 0
                    : Math.Min(columns - 1, before * columns / total);
                columnOf[i] = column;
                columnHeight[column] += heights[i] + gap;
                before += heights[i] + gap;
            }

            int tallest = 0;
            foreach (int value in columnHeight)
            {
                tallest = Math.Max(tallest, value);
            }

            _content = _slots.Count == 0 ? 0 : pad * 2 + tallest - gap;
            int maxScroll = Math.Max(0, _content - height);
            _scroll = Math.Max(0, Math.Min(_scroll, maxScroll));

            var cursor = new int[columns];
            for (int i = 0; i < _slots.Count; i++)
            {
                int column = columnOf[i];
                _slots[i].Rect = new Rectangle(
                    startX + column * (spineWidth + gap),
                    pad + cursor[column] - _scroll,
                    spineWidth,
                    heights[i]);
                cursor[column] += heights[i] + gap;
            }
        }

        private int Measure(string text)
        {
            return TextRenderer.MeasureText(
                text,
                Font,
                new Size(1000, 1000),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        }

        private int MeasureSpine(string text)
        {
            return TextRenderer.MeasureText(
                text,
                _spineFont ?? Font,
                new Size(1000, 1000),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        }

        /// <summary>
        /// 书脊里的字号和图标跟着书脊宽度走。只把卡片拉宽、字还是那么小，
        /// 看着就是一条空荡荡的白条，所以宽一点字也大一点（最多放到 1.5 倍）。
        /// </summary>
        private void UpdateSpineMetrics(int dpi, int spineWidth)
        {
            if (_spineFont == null || _spineFontWidth != spineWidth)
            {
                if (_spineFont != null)
                {
                    _spineFont.Dispose();
                }

                float basis = Math.Max(1, Dpi.Scale(23, dpi));
                float scale = Math.Min(1.5f, spineWidth / basis);
                _spineFont = new Font(Theme.UiFont.FontFamily, 9f * scale, FontStyle.Regular);
                _spineFontWidth = spineWidth;
            }

            _spineIcon = Math.Min(
                Dpi.Scale(22, dpi),
                Math.Max(Dpi.Scale(13, dpi), spineWidth * 2 / 3));
        }

        private int IconSize(int dpi)
        {
            return Dpi.Scale(IconLogical, dpi);
        }

        // ---------- 绘制 ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);

            int dpi = DeviceDpi;
            int radius = Dpi.Scale(RadiusLogical, dpi);
            Rectangle clip = ClientRectangle;

            if (_slots.Count > 0)
            {
                DrawWatermark(g, dpi);
            }

            for (int i = 0; i < _slots.Count; i++)
            {
                Slot slot = _slots[i];
                if (!clip.IntersectsWith(slot.Rect))
                {
                    continue;
                }

                bool marked = _recent != IntPtr.Zero && slot.Handle == _recent;
                PaintBackground(g, slot, i, dpi, radius, marked);
                PaintContent(g, slot, dpi);
            }

            if (_slots.Count == 0)
            {
                DrawEmptyHint(g, dpi);
            }

            DrawScrollIndicator(g, dpi);
            DrawEdgeLine(g, dpi);
        }

        private void PaintBackground(Graphics g, Slot slot, int index, int dpi, int radius, bool marked)
        {
            Rectangle rect = slot.Rect;
            int line = Math.Max(1, Dpi.Scale(1, dpi));

            if (Theme.Style == StripStyle.Card)
            {
                Rectangle shadow = rect;
                shadow.Offset(0, Math.Max(1, Dpi.Scale(2, dpi)));
                Painter.FillRounded(g, shadow, radius, Theme.CardShadow);

                Painter.FillRounded(
                    g,
                    rect,
                    radius,
                    slot.Hover || marked ? Theme.CardHoverFill : Theme.CardFill);

                // 顶部 1px 高光：比投影便宜，也更能让人觉得卡片是"浮"着的
                int inset = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
                int width = Math.Max(0, rect.Width - inset * 2);
                if (width > 0)
                {
                    using (var brush = new SolidBrush(Theme.CardHighlight))
                    {
                        g.FillRectangle(brush, new Rectangle(rect.X + inset, rect.Y, width, line));
                    }
                }

                if (slot.Hover)
                {
                    Painter.StrokeRounded(g, rect, radius, Theme.AccentRing, line);
                }
                else if (!marked)
                {
                    Painter.StrokeRounded(g, rect, radius, Theme.CardBorder, line);
                }
            }
            else
            {
                if (slot.Hover || marked)
                {
                    Painter.FillRounded(g, rect, radius, Theme.RowHoverFill);
                }
                else if (index > 0 && !_spines)
                {
                    int gap = Math.Max(2, _gap);
                    Rectangle separator = _edge.IsVertical()
                        ? new Rectangle(rect.X, rect.Y - gap / 2, rect.Width, line)
                        : new Rectangle(rect.X - gap / 2, rect.Y, line, rect.Height);
                    if (separator.Width > 0 && separator.Height > 0)
                    {
                        using (var brush = new SolidBrush(Theme.Separator))
                        {
                            g.FillRectangle(brush, separator);
                        }
                    }
                }
            }

            if (marked)
            {
                PaintMark(g, rect, dpi, radius);
            }
        }

        /// <summary>
        /// 上次用过的窗口：贴着卡片左沿的一条强调色竖条。
        /// 竖排的书脊只有 22px 宽，顶部放横条会从圆角外面露出来，所以统一放左边。
        /// </summary>
        private void PaintMark(Graphics g, Rectangle rect, int dpi, int radius)
        {
            int width = Math.Max(2, Dpi.Scale(BarLogical, dpi));
            int edge = Math.Max(1, Dpi.Scale(1, dpi));

            // 两端收到圆角直边之内，否则会从圆弧里冒出来
            int room = Math.Min(radius, Math.Max(0, (rect.Height - Dpi.Scale(10, dpi)) / 2));
            int height = rect.Height - room * 2;

            var bar = new Rectangle(rect.X + edge, rect.Y + room, width, height);
            if (bar.Width <= 0 || bar.Height <= 0)
            {
                return;
            }

            Painter.FillRounded(g, bar, width / 2, Theme.Accent);
        }

        private void PaintContent(Graphics g, Slot slot, int dpi)
        {
            if (_spines)
            {
                Bitmap label = LabelFor(slot, dpi);
                if (label == null)
                {
                    return;
                }

                GraphicsState state = g.Save();
                try
                {
                    // 从卡片右上角起转 90°：内容条的第一格落在顶部，文字顺着边往下走
                    g.TranslateTransform(slot.Rect.Right, slot.Rect.Y);
                    g.RotateTransform(90f);
                    g.DrawImage(label, 0, 0, label.Width, label.Height);
                }
                finally
                {
                    g.Restore(state);
                }
                return;
            }

            Rectangle textRect = Rectangle.Inflate(slot.Rect, -_chipPadX, -_chipPadY);
            int iconSize = IconSize(dpi);

            if (slot.Icon != null)
            {
                int iconGap = Dpi.Scale(IconGapLogical, dpi);
                var iconRect = new Rectangle(
                    slot.Rect.X + _chipPadX,
                    slot.Rect.Y + (slot.Rect.Height - iconSize) / 2,
                    iconSize,
                    iconSize);
                DrawIcon(g, slot.Icon, iconRect);
                textRect.X += iconSize + iconGap;
                textRect.Width -= iconSize + iconGap;
            }

            if (textRect.Width <= 0 || textRect.Height <= 0)
            {
                return;
            }

            TextRenderer.DrawText(
                g,
                slot.Title,
                Font,
                textRect,
                slot.Minimized ? Theme.TextDim : Theme.Text,
                TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);
        }

        /// <summary>
        /// 把"图标 + 标题"画成一条横向小图，交给外面旋转贴上去。
        /// 只有 90° 的整数旋转，用最近邻缩放也不会有任何损失。
        /// </summary>
        private Bitmap LabelFor(Slot slot, int dpi)
        {
            int length = slot.Rect.Height;
            int thickness = slot.Rect.Width;
            if (length <= 0 || thickness <= 0)
            {
                return null;
            }

            string key = slot.Title + "|" + length + "|" + thickness + "|" + dpi
                         + "|" + (slot.Minimized ? 1 : 0)
                         + "|" + (Theme.IsDark ? 1 : 0)
                         + "|" + (slot.Icon != null ? 1 : 0);
            if (slot.Label != null && slot.LabelKey == key)
            {
                return slot.Label;
            }

            slot.DropLabel();

            int pad = _chipPadX;
            int iconSize = _spineIcon > 0 ? _spineIcon : IconSize(dpi);
            int iconGap = Dpi.Scale(IconGapLogical, dpi);
            Font font = _spineFont ?? Font;
            int textX = pad + (slot.Icon != null ? iconSize + iconGap : 0);
            int textWidth = Math.Max(0, length - textX - pad);

            var bitmap = new Bitmap(length, thickness, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                // 旋转过的文字用不了 ClearType（它是按横向子像素排的），用灰度抗锯齿才对
                g.TextRenderingHint = TextRenderingHint.AntiAlias;

                if (slot.Icon != null)
                {
                    DrawIcon(g, slot.Icon, new Rectangle(pad, (thickness - iconSize) / 2, iconSize, iconSize));
                }

                if (textWidth > 0)
                {
                    using (var brush = new SolidBrush(slot.Minimized ? Theme.TextDim : Theme.Text))
                    using (var format = new StringFormat(StringFormatFlags.NoWrap))
                    {
                        format.Trimming = StringTrimming.EllipsisCharacter;
                        format.LineAlignment = StringAlignment.Center;
                        g.DrawString(
                            slot.Title,
                            font,
                            brush,
                            new RectangleF(textX, 0, textWidth, thickness),
                            format);
                    }
                }
            }

            slot.Label = bitmap;
            slot.LabelKey = key;
            return bitmap;
        }

        private static void DrawIcon(Graphics g, Bitmap icon, Rectangle rect)
        {
            InterpolationMode previous = g.InterpolationMode;
            PixelOffsetMode previousOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            try
            {
                g.DrawImage(icon, rect);
            }
            finally
            {
                g.InterpolationMode = previous;
                g.PixelOffsetMode = previousOffset;
            }
        }

        /// <summary>
        /// 把这条边压着的桌面序号画成背景水印。上边栏只有 56px 高，塞一行标头就把内容挤没了；
        /// 水印不占布局，又比空无一物更容易看出这条边的归属。
        /// </summary>
        private void DrawWatermark(Graphics g, int dpi)
        {
            if (_desktopNumber <= 0)
            {
                return;
            }

            string text = _desktopNumber.ToString();
            Size size = TextRenderer.MeasureText(
                text, Theme.WatermarkFont, new Size(800, 800), TextFormatFlags.NoPrefix);
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            int margin = Dpi.Scale(4, dpi);

            // 放在远端：内容从近端往里长，远端那块永远是最空的
            Rectangle area = _edge.IsVertical()
                ? new Rectangle(0, ClientSize.Height - size.Height - margin, ClientSize.Width, size.Height)
                : new Rectangle(ClientSize.Width - size.Width - margin, 0, size.Width, ClientSize.Height);

            if (area.Width <= 0 || area.Height <= 0)
            {
                return;
            }

            TextRenderer.DrawText(
                g,
                text,
                Theme.WatermarkFont,
                area,
                Theme.Watermark,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        /// <summary>没有任何窗口时，把这条边绑定的桌面写出来，避免看起来像坏了。</summary>
        private void DrawEmptyHint(Graphics g, int dpi)
        {
            if (_desktopNumber <= 0)
            {
                return;
            }

            int pad = Dpi.Scale(4, dpi);
            Rectangle area = Rectangle.Inflate(ClientRectangle, -pad, -pad);
            if (area.Width <= 0 || area.Height <= 0)
            {
                return;
            }

            TextRenderer.DrawText(
                g,
                "桌面 " + _desktopNumber,
                Font,
                area,
                Theme.EmptyHint,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.WordBreak
                | TextFormatFlags.NoPrefix);
        }

        /// <summary>整条内沿都画线：贴内容的一段是主区轮廓，贴着相邻侧栏的一段就是接缝。</summary>
        private void DrawEdgeLine(Graphics g, int dpi)
        {
            int thickness = Math.Max(1, Dpi.Scale(1, dpi));
            Rectangle line;
            if (_edge.IsVertical())
            {
                int x = _edge == Edge.Left ? ClientSize.Width - thickness : 0;
                line = new Rectangle(x, 0, thickness, ClientSize.Height);
            }
            else
            {
                int y = _edge == Edge.Top ? ClientSize.Height - thickness : 0;
                line = new Rectangle(0, y, ClientSize.Width, thickness);
            }

            if (line.Width <= 0 || line.Height <= 0)
            {
                return;
            }

            using (var brush = new SolidBrush(Theme.Edge))
            {
                g.FillRectangle(brush, line);
            }
        }

        private void DrawScrollIndicator(Graphics g, int dpi)
        {
            bool vertical = _edge.IsVertical();
            int viewport = vertical ? ClientSize.Height : ClientSize.Width;
            if (_content <= viewport)
            {
                return;
            }

            int trackLength = viewport - Dpi.Scale(20, dpi);
            if (trackLength <= Dpi.Scale(20, dpi))
            {
                return;
            }

            int thickness = Dpi.Scale(3, dpi);
            int slider = Math.Max(
                Dpi.Scale(28, dpi),
                (int)Math.Round(trackLength * (double)viewport / _content));
            slider = Math.Min(slider, trackLength);
            int maxScroll = _content - viewport;
            int travel = trackLength - slider;
            int offset = maxScroll <= 0
                ? 0
                : (int)Math.Round(travel * (double)_scroll / maxScroll);

            Rectangle bar = vertical
                ? new Rectangle(
                    ClientSize.Width - Dpi.Scale(7, dpi) - thickness,
                    Dpi.Scale(10, dpi) + offset,
                    thickness,
                    slider)
                : new Rectangle(
                    Dpi.Scale(10, dpi) + offset,
                    ClientSize.Height - Dpi.Scale(7, dpi) - thickness,
                    slider,
                    thickness);

            Painter.FillRounded(g, bar, thickness / 2 + 1, Theme.ScrollThumb);
        }

        // ---------- 交互 ----------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Right)
            {
                _manager.ShowMenu(this, e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (IsOnResizeHandle(e.Location))
            {
                _pressed = false;
                _pressIndex = -1;
                _resizing = true;
                _resizePointerStart = _edge.IsVertical() ? Cursor.Position.X : Cursor.Position.Y;
                _resizeThicknessStart = _edge.IsVertical() ? Width : Height;
                _lastDragTick = 0;
                Capture = true;
                Cursor = ResizeCursor();
                _manager.BeginDrag();
                return;
            }

            _pressed = true;
            _pressIndex = HitTest(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_resizing)
            {
                // 鼠标消息可能一帧来好几条，按 ~60fps 合并，避免重复摆四条窗口
                int now = Environment.TickCount;
                if (unchecked(now - _lastDragTick) < 16)
                {
                    return;
                }
                _lastDragTick = now;

                int pointer = _edge.IsVertical() ? Cursor.Position.X : Cursor.Position.Y;
                int delta = pointer - _resizePointerStart;
                int thickness;
                switch (_edge)
                {
                    case Edge.Left:
                        thickness = _resizeThicknessStart + delta;
                        break;
                    case Edge.Right:
                        thickness = _resizeThicknessStart - delta;
                        break;
                    case Edge.Top:
                        thickness = _resizeThicknessStart + delta;
                        break;
                    default:
                        thickness = _resizeThicknessStart - delta;
                        break;
                }

                _manager.PreviewThickness(_edge, thickness);
                return;
            }

            if (IsOnResizeHandle(e.Location))
            {
                Cursor = ResizeCursor();
                SetHover(-1);
                return;
            }

            int index = HitTest(e.Location);
            SetHover(index);
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_resizing)
            {
                _resizing = false;
                Capture = false;
                Cursor = Cursors.Default;
                _manager.EndDrag(_edge, _edge.IsVertical() ? Width : Height);
                return;
            }

            if (e.Button != MouseButtons.Left || !_pressed)
            {
                return;
            }

            _pressed = false;
            int index = HitTest(e.Location);
            if (index != _pressIndex)
            {
                return;
            }

            if (index >= 0 && index < _slots.Count)
            {
                _manager.ActivateWindow(_slots[index].Handle, _slots[index].DesktopId);
            }
            else
            {
                _manager.ClickStrip(_edge);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_resizing)
            {
                Cursor = Cursors.Default;
            }
            SetHover(-1);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (IsOnResizeHandle(PointToClient(Cursor.Position)))
            {
                _manager.ResetThickness(_edge);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollBy(-Math.Sign(e.Delta) * Dpi.Scale(80, DeviceDpi));
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != Win32.WM_MOUSEWHEEL || !Visible || !IsHandleCreated)
            {
                return false;
            }

            if (!Bounds.Contains(Cursor.Position))
            {
                return false;
            }

            int viewport = _edge.IsVertical() ? ClientSize.Height : ClientSize.Width;
            if (_content <= viewport)
            {
                return false;
            }

            int delta = (short)((long)m.WParam >> 16);
            if (delta == 0)
            {
                return false;
            }

            ScrollBy(-Math.Sign(delta) * Dpi.Scale(80, DeviceDpi));
            return true;
        }

        private void ScrollBy(int delta)
        {
            int viewport = _edge.IsVertical() ? ClientSize.Height : ClientSize.Width;
            int maxScroll = Math.Max(0, _content - viewport);
            if (maxScroll <= 0)
            {
                return;
            }

            int target = Math.Max(0, Math.Min(maxScroll, _scroll + delta));
            if (target == _scroll)
            {
                return;
            }

            _scroll = target;
            Relayout();
        }

        private void SetHover(int index)
        {
            if (_hoverIndex == index)
            {
                return;
            }

            if (_hoverIndex >= 0 && _hoverIndex < _slots.Count)
            {
                _slots[_hoverIndex].Hover = false;
            }

            _hoverIndex = index;

            if (_hoverIndex >= 0 && _hoverIndex < _slots.Count)
            {
                _slots[_hoverIndex].Hover = true;
            }

            _tip.SetToolTip(
                this,
                _hoverIndex >= 0 && _hoverIndex < _slots.Count ? _slots[_hoverIndex].Title : "");
            Invalidate();
        }

        private int HitTest(Point point)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Rect.Contains(point))
                {
                    return i;
                }
            }
            return -1;
        }

        private bool IsOnResizeHandle(Point point)
        {
            int grip = Dpi.Scale(6, DeviceDpi);
            switch (_edge)
            {
                case Edge.Left:
                    return point.X >= ClientSize.Width - grip;
                case Edge.Right:
                    return point.X <= grip;
                case Edge.Top:
                    return point.Y >= ClientSize.Height - grip;
                default:
                    return point.Y <= grip;
            }
        }

        private Cursor ResizeCursor()
        {
            return _edge.IsVertical() ? Cursors.SizeWE : Cursors.SizeNS;
        }

        private static Rectangle InitialBounds(Edge edge)
        {
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            const int vertical = 220;
            const int horizontal = 130;

            switch (edge)
            {
                case Edge.Left:
                    return new Rectangle(screen.Left, screen.Top, vertical, screen.Height);
                case Edge.Right:
                    return new Rectangle(screen.Right - vertical, screen.Top, vertical, screen.Height);
                case Edge.Top:
                    return new Rectangle(screen.Left, screen.Top, screen.Width, horizontal);
                default:
                    return new Rectangle(screen.Left, screen.Bottom - horizontal, screen.Width, horizontal);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.RemoveMessageFilter(this);
            _tip.Dispose();
            foreach (Slot slot in _slots)
            {
                slot.DropLabel();
            }
            if (_spineFont != null)
            {
                _spineFont.Dispose();
                _spineFont = null;
            }
            base.OnFormClosed(e);
        }
    }
}
