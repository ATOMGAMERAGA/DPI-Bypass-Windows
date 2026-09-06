using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DpiBypass.Core.Network;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace DpiBypass.App;

/// <summary>Lets the user drag over a game's numeric RTT counter.</summary>
public sealed class LatencyRegionSelectorWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection = new()
    {
        Stroke = Brushes.DeepSkyBlue,
        StrokeThickness = 3,
        Fill = new SolidColorBrush(Color.FromArgb(45, 0, 174, 239)),
        Visibility = Visibility.Collapsed,
    };
    private Point _start;
    private bool _dragging;

    public LatencyRegionSelectorWindow()
    {
        Title = "Network RTT alanını seç";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(105, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _canvas.Children.Add(_selection);
        _canvas.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 20, 24, 32)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(24),
            Child = new TextBlock
            {
                Text = "VALORANT'taki yalnız sayısal Network RTT değerini sürükleyerek seçin. Esc: iptal",
                Foreground = Brushes.White,
                FontSize = 16,
            },
        });
        Content = _canvas;

        MouseLeftButtonDown += BeginSelection;
        MouseMove += UpdateSelection;
        MouseLeftButtonUp += FinishSelection;
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
    }

    public ScreenCaptureRegion? SelectedRegion { get; private set; }

    private void BeginSelection(object sender, MouseButtonEventArgs args)
    {
        _start = args.GetPosition(_canvas);
        _dragging = true;
        _selection.Visibility = Visibility.Visible;
        CaptureMouse();
        Draw(_start, _start);
        args.Handled = true;
    }

    private void UpdateSelection(object sender, MouseEventArgs args)
    {
        if (_dragging)
        {
            Draw(_start, args.GetPosition(_canvas));
        }
    }

    private void FinishSelection(object sender, MouseButtonEventArgs args)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        var end = args.GetPosition(_canvas);
        var left = Math.Min(_start.X, end.X);
        var top = Math.Min(_start.Y, end.Y);
        var width = Math.Abs(end.X - _start.X);
        var height = Math.Abs(end.Y - _start.Y);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
            ?? Matrix.Identity;
        var screenTopLeft = PointToScreen(new Point(left, top));
        var deviceSize = transform.Transform(new Vector(width, height));

        var region = new ScreenCaptureRegion(
            (int)Math.Round(screenTopLeft.X),
            (int)Math.Round(screenTopLeft.Y),
            (int)Math.Round(deviceSize.X),
            (int)Math.Round(deviceSize.Y));

        if (!region.IsValid)
        {
            _selection.Visibility = Visibility.Collapsed;
            return;
        }

        SelectedRegion = region;
        DialogResult = true;
        args.Handled = true;
    }

    private void Draw(Point first, Point second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        Canvas.SetLeft(_selection, left);
        Canvas.SetTop(_selection, top);
        _selection.Width = Math.Abs(second.X - first.X);
        _selection.Height = Math.Abs(second.Y - first.Y);
    }
}
