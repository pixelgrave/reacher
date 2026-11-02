using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Reacher;

internal enum BubblePhase { Chasing, Docked, Closing }

internal enum BubbleSignal { None, ChaseEnded, Expired }

/// <summary>
/// The attention bubble. Positioned in physical pixels via SetWindowPos so it works across
/// mixed-DPI monitors; never takes focus; click-through while it is chasing the cursor.
/// </summary>
internal sealed class BubbleWindow : Window
{
    private const double ShadowMargin = 18; // DIPs around the card reserved for the drop shadow

    public ReachItem Item { get; }
    public BubblePhase Phase { get; private set; } = BubblePhase.Docked;
    public DateTimeOffset DockedAt { get; private set; }
    public bool IsHovered { get; private set; }

    /// <summary>The user acted on the bubble: (bubble, status, clicked action or null).</summary>
    public event Action<BubbleWindow, ReachStatus, string?>? UserResponded;

    private readonly Color _accent;
    private readonly Grid _root;
    private readonly Border _card;
    private readonly ScaleTransform _cardScale = new(1, 1);
    private readonly ScaleTransform _progressScale = new(1, 1);
    private IntPtr _hwnd;
    private bool _clickThrough;
    private bool _placed;
    private double _x, _y;
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private double _sinceTopmost;
    private double _chaseLeft;
    private double _expiryLeft;
    private double _dockedFor;

    public BubbleWindow(ReachItem item)
    {
        Item = item;
        _accent = item.Priority switch
        {
            Priority.Info => Color.FromRgb(0x4C, 0x9A, 0xFF),
            Priority.Urgent => Color.FromRgb(0xFF, 0x4D, 0x5E),
            _ => Color.FromRgb(0xFF, 0xB2, 0x24),
        };
        _expiryLeft = item.TimeoutSeconds;

        Title = "Reacher";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _card = BuildCard();
        _root = new Grid { Margin = new Thickness(ShadowMargin), Opacity = 0 };
        _root.Children.Add(_card);
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, Native.WS_EX_APPWINDOW);
            ApplyClickThrough();
            // Start on the cursor's monitor so the first DPI is the right one.
            var c = Native.CursorPos();
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, c.X, c.Y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        };
    }

    private Border BuildCard()
    {
        var content = new StackPanel { Margin = new Thickness(14, 10, 12, 12), MinWidth = 240, MaxWidth = 340 };

        // Header: dot · source · time · close
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(_accent), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var source = new TextBlock
        {
            Text = Item.Source,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Gray(0x9A, 0xA0, 0xAE),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var waited = DateTimeOffset.Now - Item.CreatedAt > TimeSpan.FromMinutes(1);
        var time = new TextBlock
        {
            Text = waited ? $"waiting since {Item.CreatedAt:HH:mm}" : "now",
            FontSize = 11,
            Foreground = Gray(0x6F, 0x75, 0x84),
            Margin = new Thickness(10, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            Padding = new Thickness(5, 2, 2, 2),
            Foreground = Gray(0x8A, 0x90, 0xA0),
            Cursor = Cursors.Hand,
            ToolTip = "Dismiss",
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.MouseEnter += (_, _) => close.Foreground = Brushes.White;
        close.MouseLeave += (_, _) => close.Foreground = Gray(0x8A, 0x90, 0xA0);
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Respond(ReachStatus.Dismissed, null); };

        Grid.SetColumn(dot, 0); Grid.SetColumn(source, 1); Grid.SetColumn(time, 2); Grid.SetColumn(close, 3);
        header.Children.Add(dot); header.Children.Add(source); header.Children.Add(time); header.Children.Add(close);
        content.Children.Add(header);

        if (Item.Title is { } title)
        {
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Gray(0xF3, 0xF4, 0xF7),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0),
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = Item.Message,
            FontSize = 13,
            LineHeight = 18.5,
            Foreground = Gray(0xCD, 0xD1, 0xDA),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 18.5 * 12,
            Margin = new Thickness(0, Item.Title is null ? 7 : 3, 0, 0),
        });

        if (Item.Actions.Length > 0)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            for (var i = 0; i < Item.Actions.Length; i++)
                actions.Children.Add(BuildAction(Item.Actions[i], primary: i == 0));
            content.Children.Add(actions);
        }
        else if (Item.Open is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Click to open ↗",
                FontSize = 11,
                Foreground = new SolidColorBrush(_accent),
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        if (Item.TimeoutSeconds > 0)
        {
            content.Children.Add(new Rectangle
            {
                Height = 2,
                RadiusX = 1,
                RadiusY = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x80, _accent.R, _accent.G, _accent.B)),
                Margin = new Thickness(0, 10, 0, 0),
                RenderTransform = _progressScale,
            });
        }

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bar = new Border { Background = new SolidColorBrush(_accent), CornerRadius = new CornerRadius(11, 0, 0, 11) };
        Grid.SetColumn(content, 1);
        layout.Children.Add(bar);
        layout.Children.Add(content);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x1D, 0x1E, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, _accent.R, _accent.G, _accent.B)),
            BorderThickness = new Thickness(1),
            Child = layout,
            Cursor = Item.Actions.Length == 0 ? Cursors.Hand : Cursors.Arrow,
            RenderTransform = _cardScale,
            RenderTransformOrigin = new Point(0, 0),
            Effect = BuildShadow(),
        };
        card.MouseEnter += (_, _) => IsHovered = true;
        card.MouseLeave += (_, _) => IsHovered = false;
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (Item.Actions.Length == 0) Respond(ReachStatus.Acknowledged, null);
        };
        card.MouseRightButtonUp += (_, _) => Respond(ReachStatus.Dismissed, null);
        return card;
    }

    private Border BuildAction(string label, bool primary)
    {
        var idle = Color.FromArgb(primary ? (byte)0x40 : (byte)0x1C, _accent.R, _accent.G, _accent.B);
        var hover = Color.FromArgb(primary ? (byte)0x70 : (byte)0x40, _accent.R, _accent.G, _accent.B);
        var button = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 5, 12, 6),
            Margin = new Thickness(0, 0, 6, 4),
            Background = new SolidColorBrush(idle),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, _accent.R, _accent.G, _accent.B)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
        };
        button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(hover);
        button.MouseLeave += (_, _) => button.Background = new SolidColorBrush(idle);
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; Respond(ReachStatus.Acknowledged, label); };
        return button;
    }

    private DropShadowEffect BuildShadow()
    {
        if (Item.Priority == Priority.Info)
            return new DropShadowEffect { Color = Colors.Black, BlurRadius = 20, ShadowDepth = 2, Direction = 270, Opacity = 0.5 };

        var glow = new DropShadowEffect { Color = _accent, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.55 };
        if (Item.Priority == Priority.Urgent)
        {
            var duration = TimeSpan.FromMilliseconds(650);
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
                new DoubleAnimation(12, 30, duration) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            glow.BeginAnimation(DropShadowEffect.OpacityProperty,
                new DoubleAnimation(0.45, 1, duration) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        return glow;
    }

    private static SolidColorBrush Gray(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private void Respond(ReachStatus status, string? response)
    {
        if (Phase == BubblePhase.Closing) return;
        UserResponded?.Invoke(this, status, response);
    }

    // ---- lifecycle driven by the Coordinator ----

    public void StartChase(double seconds)
    {
        Phase = BubblePhase.Chasing;
        _chaseLeft = seconds;
        IsHovered = false;
        SetClickThrough(true);
    }

    public void Dock()
    {
        Phase = BubblePhase.Docked;
        DockedAt = DateTimeOffset.Now;
        _dockedFor = 0;
        SetClickThrough(false);
    }

    public void FadeOut()
    {
        if (Phase == BubblePhase.Closing) return;
        Phase = BubblePhase.Closing;
        _root.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170));
        fade.Completed += (_, _) => Close();
        _root.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(170));
        _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>Advance timers. <paramref name="present"/> pauses expiry while the user is away.</summary>
    public BubbleSignal Advance(double dt, bool present)
    {
        switch (Phase)
        {
            case BubblePhase.Chasing:
                _chaseLeft -= dt;
                return _chaseLeft <= 0 ? BubbleSignal.ChaseEnded : BubbleSignal.None;

            case BubblePhase.Docked when present && !IsHovered:
                _dockedFor += dt;
                if (Item.TimeoutSeconds > 0)
                {
                    _expiryLeft -= dt;
                    _progressScale.ScaleX = Math.Clamp(_expiryLeft / Item.TimeoutSeconds, 0, 1);
                    if (_expiryLeft <= 0) return BubbleSignal.Expired;
                }
                // Urgent bubbles come back for another lap if they sit unanswered.
                if (Item.Priority == Priority.Urgent && _dockedFor > 45)
                    StartChase(2.5);
                return BubbleSignal.None;

            default:
                return BubbleSignal.None;
        }
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private (double w, double h) Size()
    {
        Native.GetWindowRect(_hwnd, out var r);
        return (r.Width, r.Height);
    }

    /// <summary>Card height in physical pixels (for stacking).</summary>
    public double CardHeight => Size().h - 2 * ShadowMargin * Scale;

    public void ChaseToward(Native.POINT cursor, Native.RECT work, double dt)
    {
        if (_hwnd == IntPtr.Zero) return;
        var s = Scale;
        var m = ShadowMargin * s;
        var (w, h) = Size();
        double cardW = w - 2 * m, cardH = h - 2 * m;
        double cx = cursor.X + 18 * s, cy = cursor.Y + 22 * s;
        if (cx + cardW > work.Right - 6 * s) cx = cursor.X - 14 * s - cardW;
        if (cy + cardH > work.Bottom - 6 * s) cy = cursor.Y - 14 * s - cardH;
        cx = Math.Max(cx, work.Left + 6 * s);
        cy = Math.Max(cy, work.Top + 6 * s);
        MoveToward(cx - m, cy - m, rate: 18, dt);
    }

    /// <summary>Glide so the card's bottom-right corner sits at the given physical point.</summary>
    public void DockToward(double cardRight, double cardBottom, double dt)
    {
        if (_hwnd == IntPtr.Zero) return;
        var m = ShadowMargin * Scale;
        var (w, h) = Size();
        MoveToward(cardRight - (w - 2 * m) - m, cardBottom - (h - 2 * m) - m, rate: 9, dt);
    }

    private void MoveToward(double tx, double ty, double rate, double dt)
    {
        if (!_placed)
        {
            _x = tx; _y = ty; _placed = true;
            Apply(dt);
            PopIn();
            return;
        }
        var a = 1 - Math.Exp(-rate * dt);
        _x += (tx - _x) * a;
        _y += (ty - _y) * a;
        Apply(dt);
    }

    private void Apply(double dt)
    {
        int ix = (int)Math.Round(_x), iy = (int)Math.Round(_y);
        _sinceTopmost += dt;
        if (ix == _lastX && iy == _lastY && _sinceTopmost < 1) return;
        _lastX = ix; _lastY = iy; _sinceTopmost = 0;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, ix, iy, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    private void PopIn()
    {
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        var grow = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut },
        };
        _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void SetClickThrough(bool on)
    {
        _clickThrough = on;
        ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_clickThrough) Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT, 0);
        else Native.SetExStyle(_hwnd, 0, Native.WS_EX_TRANSPARENT);
    }
}
