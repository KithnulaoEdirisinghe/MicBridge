using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MicBridge
{
    /// <summary>
    /// The look of the phone app (android/res/values-night/colors.xml) for the PC window:
    /// same colours, rounded cards, one big action button, switches instead of tick boxes.
    /// </summary>
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0x0C, 0x11, 0x1B);
        public static readonly Color Card = Color.FromArgb(0x17, 0x20, 0x30);
        public static readonly Color Text = Color.FromArgb(0xF2, 0xF4, 0xF7);
        public static readonly Color Text2 = Color.FromArgb(0xA3, 0xAC, 0xBA);
        public static readonly Color Accent = Color.FromArgb(0x3B, 0x82, 0xF6);
        public static readonly Color Danger = Color.FromArgb(0xF0, 0x55, 0x4A);
        public static readonly Color Track = Color.FromArgb(0x26, 0x32, 0x47);
        public static readonly Color Level = Color.FromArgb(0x22, 0xC5, 0x5E);

        // Desktop-only additions in the same family.
        public static readonly Color Field = Color.FromArgb(0x0F, 0x16, 0x23);
        public static readonly Color Outline = Color.FromArgb(0x2F, 0x3C, 0x55);
        public static readonly Color Amber = Color.FromArgb(0xF5, 0x9E, 0x0B);
        public static readonly Color Idle = Color.FromArgb(0x6B, 0x76, 0x88);
        public static readonly Color Link = Color.FromArgb(0x7F, 0xB2, 0xFF);

        public const int CardRadius = 18;

        public static float Scale(Graphics g) { return g.DpiX / 96f; }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
        }

        public static void FillRound(Graphics g, Color color, float x, float y, float w, float h, float radius)
        {
            if (w <= 0 || h <= 0) return;
            float d = Math.Min(radius * 2, Math.Min(w, h));
            using (var brush = new SolidBrush(color))
            {
                if (d < 1) { g.FillRectangle(brush, x, y, w, h); return; }
                using (var path = new GraphicsPath())
                {
                    path.AddArc(x, y, d, d, 180, 90);
                    path.AddArc(x + w - d, y, d, d, 270, 90);
                    path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                    path.AddArc(x, y + h - d, d, d, 90, 90);
                    path.CloseFigure();
                    g.FillPath(brush, path);
                }
            }
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public const ControlStyles Painted = ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                                           | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw;
    }

    /// <summary>A rounded panel like the cards in the phone app. Its children inherit the card colour.</summary>
    sealed class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.Smooth(g);
            Theme.FillRound(g, BackColor, 0, 0, ClientSize.Width, ClientSize.Height, Theme.CardRadius * Theme.Scale(g));
        }
    }

    /// <summary>The level bar: a rounded track with the level clipped into it, as on the phone.</summary>
    sealed class LevelMeter : Control
    {
        float _value;

        public LevelMeter()
        {
            SetStyle(Theme.Painted, true);
            TabStop = false;
        }

        public float Value
        {
            get { return _value; }
            set
            {
                if (value < 0) value = 0; else if (value > 1) value = 1;
                if (Math.Abs(value - _value) < 0.004f) return;
                _value = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int w = ClientSize.Width, h = ClientSize.Height;
            g.Clear(BackColor);
            Theme.Smooth(g);
            Theme.FillRound(g, Theme.Track, 0, 0, w, h, h / 2f);
            float fill = w * _value;
            if (fill >= 1)
            {
                g.SetClip(new RectangleF(0, 0, fill, h));
                Theme.FillRound(g, _value > 0.97f ? Theme.Danger : Theme.Level, 0, 0, w, h, h / 2f);
                g.ResetClip();
            }
        }
    }

    /// <summary>The small coloured light next to the status text.</summary>
    sealed class StatusDot : Control
    {
        Color _color = Theme.Idle;

        public StatusDot()
        {
            SetStyle(Theme.Painted, true);
            TabStop = false;
        }

        public Color Color
        {
            get { return _color; }
            set { if (_color != value) { _color = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Theme.Smooth(g);
            using (var brush = new SolidBrush(_color)) g.FillEllipse(brush, 0, 0, ClientSize.Width, ClientSize.Height);
        }
    }

    /// <summary>The wide rounded action button (blue to connect, red to stop), like Start/Stop on the phone.</summary>
    sealed class RoundButton : Control, IButtonControl
    {
        Color _fill = Theme.Accent;
        bool _hover, _down;

        public RoundButton()
        {
            SetStyle(Theme.Painted | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
        }

        public Color FillColor
        {
            get { return _fill; }
            set { if (_fill != value) { _fill = value; Invalidate(); } }
        }

        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value) { }

        public void PerformClick()
        {
            if (Enabled && Visible) OnClick(EventArgs.Empty);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { _down = true; Focus(); Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _down = false;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; PerformClick(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float s = Theme.Scale(g);
            g.Clear(BackColor);
            Theme.Smooth(g);
            Color fill = _down ? Theme.Mix(_fill, Color.Black, 0.18f) : _hover ? Theme.Mix(_fill, Color.White, 0.10f) : _fill;
            Theme.FillRound(g, fill, 0, 0, ClientSize.Width, ClientSize.Height, Theme.CardRadius * s);
            if (Focused && ShowFocusCues)
            {
                using (var pen = new Pen(Color.FromArgb(150, Color.White), s))
                    g.DrawLine(pen, ClientSize.Width * 0.42f, ClientSize.Height - 9 * s, ClientSize.Width * 0.58f, ClientSize.Height - 9 * s);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>An on/off switch that behaves like a tick box (Checked, CheckedChanged).</summary>
    sealed class ToggleSwitch : Control
    {
        static readonly Color OffTrack = Color.FromArgb(0x3A, 0x45, 0x58), OffThumb = Color.FromArgb(0xB9, 0xBF, 0xCA);
        bool _checked;

        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(Theme.Painted | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                EventHandler handler = CheckedChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            Focus();
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space) { e.Handled = true; Checked = !Checked; }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float w = ClientSize.Width, h = ClientSize.Height;
            g.Clear(BackColor);
            Theme.Smooth(g);
            float thumb = h - 4, trackH = h * 0.58f, inset = thumb * 0.18f;
            Theme.FillRound(g, _checked ? Theme.Mix(Theme.Accent, BackColor, 0.5f) : OffTrack, inset, (h - trackH) / 2, w - inset * 2, trackH, trackH / 2);
            float x = _checked ? w - thumb - 1 : 1;
            using (var brush = new SolidBrush(_checked ? Theme.Accent : OffThumb)) g.FillEllipse(brush, x, (h - thumb) / 2, thumb, thumb);
            if (Focused && ShowFocusCues)
            {
                using (var pen = new Pen(Color.FromArgb(170, Color.White), Theme.Scale(g)))
                    g.DrawEllipse(pen, x + 1, (h - thumb) / 2 + 1, thumb - 2, thumb - 2);
            }
        }
    }

    /// <summary>A slim slider that behaves like a track bar (Minimum, Maximum, Value, ValueChanged).</summary>
    sealed class Slider : Control
    {
        int _minimum, _maximum = 100, _value;
        bool _dragging;

        public int SmallChange = 5, LargeChange = 25;
        public event EventHandler ValueChanged;

        public Slider()
        {
            SetStyle(Theme.Painted | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }

        public int Minimum { get { return _minimum; } set { _minimum = value; Value = _value; Invalidate(); } }
        public int Maximum { get { return _maximum; } set { _maximum = value; Value = _value; Invalidate(); } }

        public int Value
        {
            get { return _value; }
            set
            {
                if (value < _minimum) value = _minimum; else if (value > _maximum) value = _maximum;
                if (value == _value) return;
                _value = value;
                Invalidate();
                EventHandler handler = ValueChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        float ThumbSize { get { return ClientSize.Height * 0.66f; } }

        void SetFromMouse(int x)
        {
            float r = ThumbSize / 2, usable = ClientSize.Width - ThumbSize;
            if (usable <= 0 || _maximum <= _minimum) return;
            float t = (x - r) / usable;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            Value = _minimum + (int)Math.Round(t * (_maximum - _minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _dragging = true;
            SetFromMouse(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetFromMouse(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value = _value + (e.Delta > 0 ? SmallChange : -SmallChange);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: Value = _value - SmallChange; e.Handled = true; break;
                case Keys.Right: case Keys.Up: Value = _value + SmallChange; e.Handled = true; break;
                case Keys.PageDown: Value = _value - LargeChange; e.Handled = true; break;
                case Keys.PageUp: Value = _value + LargeChange; e.Handled = true; break;
                case Keys.Home: Value = _minimum; e.Handled = true; break;
                case Keys.End: Value = _maximum; e.Handled = true; break;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float w = ClientSize.Width, h = ClientSize.Height, s = Theme.Scale(g);
            g.Clear(BackColor);
            Theme.Smooth(g);
            float thumb = ThumbSize, line = 4 * s, usable = w - thumb;
            float t = _maximum > _minimum ? (_value - _minimum) / (float)(_maximum - _minimum) : 0;
            float cx = thumb / 2 + usable * t;
            Theme.FillRound(g, Theme.Track, thumb / 2 - line / 2, (h - line) / 2, usable + line, line, line / 2);
            Theme.FillRound(g, Theme.Accent, thumb / 2 - line / 2, (h - line) / 2, cx - thumb / 2 + line, line, line / 2);
            using (var brush = new SolidBrush(Theme.Accent)) g.FillEllipse(brush, cx - thumb / 2, (h - thumb) / 2, thumb, thumb);
            if (Focused && ShowFocusCues)
            {
                using (var pen = new Pen(Color.FromArgb(170, Color.White), s))
                    g.DrawEllipse(pen, cx - thumb / 2 + 1, (h - thumb) / 2 + 1, thumb - 2, thumb - 2);
            }
        }
    }

    /// <summary>
    /// A normal Windows drop-down list in the dark colours. Windows still does all the work (typing,
    /// keyboard, the pop-up list); this only draws the rows, the border and the arrow itself.
    /// </summary>
    sealed class DarkComboBox : ComboBox
    {
        const int WmPaint = 0x000F, WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmMouseLeave = 0x02A3;

        public DarkComboBox()
        {
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = Theme.Field;
            ForeColor = Theme.Text;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool inField = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool highlighted = !inField && (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(highlighted ? Theme.Accent : BackColor)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index < 0 || e.Index >= Items.Count) return;
            Rectangle r = e.Bounds;
            int pad = (int)(5 * Theme.Scale(e.Graphics));
            r.X += pad;
            r.Width -= pad * 2;
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, r, highlighted ? Color.White : ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            switch (m.Msg)
            {
                // Windows repaints its own light border and arrow button at these moments: draw ours over them.
                case WmPaint: case WmMouseMove: case WmLButtonDown: case WmLButtonUp: case WmMouseLeave:
                    if (IsHandleCreated && Visible) PaintChrome();
                    break;
            }
        }

        void PaintChrome()
        {
            using (Graphics g = Graphics.FromHwnd(Handle))
            {
                float s = Theme.Scale(g);
                int w = Width, h = Height, arrow = SystemInformation.HorizontalScrollBarArrowWidth;
                var button = new Rectangle(w - arrow - 3, 1, arrow + 2, h - 2);
                using (var brush = new SolidBrush(BackColor)) g.FillRectangle(brush, button);
                using (var pen = new Pen(Focused || DroppedDown ? Theme.Accent : Theme.Outline)) g.DrawRectangle(pen, 0, 0, w - 1, h - 1);

                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = button.Left + button.Width / 2f, cy = h / 2f, d = 4 * s;
                using (var pen = new Pen(Theme.Text2, 1.6f * s))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new[] { new PointF(cx - d, cy - d / 2), new PointF(cx, cy + d / 2), new PointF(cx + d, cy - d / 2) });
                }
            }
        }
    }
}
