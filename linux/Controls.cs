using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace OverByte;

// ══════════════════════════════════════════════════
//  Palette (same values as the Windows version)
// ══════════════════════════════════════════════════

internal static class Pal
{
    public static readonly Color GreenPrimary = Color.FromRgb(16, 163, 100);
    public static readonly Color GreenDark = Color.FromRgb(12, 130, 80);
    public static readonly Color GreenLight = Color.FromRgb(230, 248, 240);
    public static readonly Color BgWhite = Color.FromRgb(247, 248, 250);
    public static readonly Color BgCard = Colors.White;
    public static readonly Color BorderLight = Color.FromRgb(228, 230, 235);
    public static readonly Color TextDark = Color.FromRgb(30, 33, 40);
    public static readonly Color TextMid = Color.FromRgb(100, 105, 115);
    public static readonly Color TextLight = Color.FromRgb(160, 165, 175);
    public static readonly Color AccentRed = Color.FromRgb(230, 60, 60);
    public static readonly Color HeaderBg = Color.FromRgb(20, 24, 30);

    public static SolidColorBrush B(Color c) => new(c);
}

// ══════════════════════════════════════════════════
//  Custom Button
// ══════════════════════════════════════════════════

internal class OBButton : Control
{
    bool _hover, _pressed;
    readonly bool _primary;
    string _label;

    static readonly Color GreenHover = Color.FromRgb(14, 145, 88);
    static readonly Color GreenPress = Color.FromRgb(10, 120, 72);
    static readonly Color SecBg = Color.FromRgb(247, 248, 250);
    static readonly Color SecHover = Color.FromRgb(237, 238, 242);
    static readonly Color SecBorder = Color.FromRgb(210, 213, 220);

    public event EventHandler? Click;

    public OBButton(string label, double w, double h, bool primary = false)
    {
        _label = label; _primary = primary;
        Width = w; Height = h;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public void SetLabel(string l) { _label = l; InvalidateVisual(); }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1);

        Color bg, fg, border;
        if (!IsEnabled) { bg = Color.FromRgb(240, 241, 243); fg = Color.FromRgb(180, 183, 190); border = Color.FromRgb(228, 230, 235); }
        else if (_primary) { bg = _pressed ? GreenPress : (_hover ? GreenHover : Pal.GreenPrimary); fg = Colors.White; border = bg; }
        else { bg = (_pressed || _hover) ? SecHover : SecBg; fg = _hover ? Pal.GreenPrimary : Color.FromRgb(60, 63, 70); border = _hover ? Pal.GreenPrimary : SecBorder; }

        ctx.DrawRectangle(new SolidColorBrush(bg), new Pen(new SolidColorBrush(border), 1), rect, 6, 6);

        var ft = new FormattedText(_label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            _primary ? 14.5 : 10.5, new SolidColorBrush(fg));
        ctx.DrawText(ft, new Point((Bounds.Width - ft.Width) / 2, (Bounds.Height - ft.Height) / 2));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEnabledProperty) InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e) { _hover = true; InvalidateVisual(); base.OnPointerEntered(e); }
    protected override void OnPointerExited(PointerEventArgs e) { _hover = false; _pressed = false; InvalidateVisual(); base.OnPointerExited(e); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressed = true; InvalidateVisual(); }
        base.OnPointerPressed(e);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        bool fire = _pressed && _hover && IsEnabled;
        _pressed = false; InvalidateVisual();
        base.OnPointerReleased(e);
        if (fire) Click?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsEnabled && (e.Key == Key.Enter || e.Key == Key.Space)) { Click?.Invoke(this, EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }
}

// ══════════════════════════════════════════════════
//  Simple modal message / confirmation dialog
// ══════════════════════════════════════════════════

internal sealed class MessageDialog : Window
{
    bool _result;

    MessageDialog(string title, string message, bool yesNo)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Pal.B(Pal.BgWhite);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        if (yesNo)
        {
            var no = new OBButton("NO", 80, 32); no.Click += (_, _) => { _result = false; Close(); };
            var yes = new OBButton("YES", 80, 32, primary: true); yes.Click += (_, _) => { _result = true; Close(); };
            buttons.Children.Add(no); buttons.Children.Add(yes);
        }
        else
        {
            var ok = new OBButton("OK", 80, 32, primary: true); ok.Click += (_, _) => { _result = true; Close(); };
            buttons.Children.Add(ok);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Pal.B(Pal.TextDark) },
                buttons
            }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { _result = false; Close(); } };
    }

    public static async Task<bool> ShowAsync(Window owner, string message, string title = "OverByte", bool yesNo = false)
    {
        var dlg = new MessageDialog(title, message, yesNo);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }
}
