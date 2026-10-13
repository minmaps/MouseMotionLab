using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MouseMotionLab.Core.Analysis;
using MouseMotionLab.Core.Data;

namespace MouseMotionLab.Capture;

public partial class SpeedCurvesWindow : Window
{
    private const double PlotLeft = 76;
    private const double PlotTop = 38;
    private const double PlotRight = 24;
    private const double PlotBottom = 56;
    private readonly DatasetRepository _repository;
    private readonly DatasetWriteQueue? _writeQueue;
    private readonly CancellationTokenSource _lifetime = new();
    private SpeedCurveSummary? _summary;
    private bool _loading;
    private bool _reloadRequested;
    private bool _closed;

    public SpeedCurvesWindow(DatasetRepository repository, DatasetWriteQueue? writeQueue = null)
    {
        _repository = repository;
        _writeQueue = writeQueue;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_closed)
        {
            return;
        }

        if (_loading)
        {
            _reloadRequested = true;
            return;
        }

        _loading = true;
        RefreshButton.IsEnabled = false;
        var cancellationToken = _lifetime.Token;
        try
        {
            do
            {
                _reloadRequested = false;
                DatasetSummaryText.Text = "Chargement des essais acceptés…";
                if (_writeQueue is not null)
                {
                    await _writeQueue.FlushAsync(cancellationToken);
                }

                // Microsoft.Data.Sqlite reads synchronously even through its async
                // API. Keep database scans and curve calculations off the UI thread.
                var summary = await Task.Run(
                    () => _repository.GetSpeedCurvesAsync(cancellationToken), cancellationToken);
                if (_closed)
                {
                    return;
                }

                _summary = summary;
                DatasetSummaryText.Text = summary.TrialCount == 0
                    ? "Aucun essai accepté exploitable dans le dataset."
                    : $"{summary.TrialCount:N0} essais acceptés · 101 points · mise à jour automatique";
                if (summary.SkippedTrialCount > 0)
                {
                    DatasetSummaryText.Text += $" · {summary.SkippedTrialCount:N0} essais sans intervalle mesurable ignorés";
                }

                ResetReadout();
                DrawChart();
            }
            while (_reloadRequested && !_closed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the window cancels an ongoing dataset scan.
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                _summary = null;
                DatasetSummaryText.Text = $"Lecture des données impossible : {exception.Message}";
                DrawChart();
            }
        }
        finally
        {
            _loading = false;
            if (!_closed)
            {
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void Scale_Changed(object sender, RoutedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        SpeedChartCanvas.Children.Clear();
        var plotWidth = SpeedChartCanvas.ActualWidth - PlotLeft - PlotRight;
        var plotHeight = SpeedChartCanvas.ActualHeight - PlotTop - PlotBottom;
        if (plotWidth <= 0 || plotHeight <= 0)
        {
            return;
        }

        var points = _summary?.Points;
        var maximum = points is { Count: > 0 }
            ? points.Max(point => point.MaximumMillimetersPerSecond)
            : 0d;
        var axisMaximum = maximum > 0 ? maximum * 1.08 : 1d;
        var logarithmic = LogScaleCheckBox.IsChecked == true;
        var muted = (Brush)FindResource("MutedTextBrush");
        var gridBrush = new SolidColorBrush(Color.FromRgb(38, 56, 77));
        AddLabel("Vitesse (mm/s)", PlotLeft, 10, muted);

        for (var tick = 0; tick <= 5; tick++)
        {
            var x = PlotLeft + plotWidth * tick / 5d;
            var y = PlotTop + plotHeight * (1d - tick / 5d);
            AddLine(x, PlotTop, x, PlotTop + plotHeight, gridBrush);
            AddLine(PlotLeft, y, PlotLeft + plotWidth, y, gridBrush);
            AddLabel($"{tick * 20} %", x - 17, PlotTop + plotHeight + 10, muted);
            var label = new TextBlock
            {
                Text = FormatSpeed(logarithmic
                    ? Math.Exp(Math.Log(1d + axisMaximum) * tick / 5d) - 1d
                    : axisMaximum * tick / 5d),
                Foreground = muted,
                FontSize = 12,
                Width = PlotLeft - 12,
                TextAlignment = TextAlignment.Right
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 8);
            SpeedChartCanvas.Children.Add(label);
        }

        AddLabel("Progression dans le temps (% de la durée)",
            PlotLeft + plotWidth / 2d - 135, PlotTop + plotHeight + 33, muted);
        if (points is not { Count: > 0 })
        {
            AddLabel("Aucune courbe disponible",
                PlotLeft + 20, PlotTop + plotHeight / 2d, muted);
            return;
        }

        AddCurve(points, point => point.MaximumMillimetersPerSecond,
            Color.FromRgb(255, 206, 115), 2, plotWidth, plotHeight, axisMaximum);
        AddCurve(points, point => point.MeanMillimetersPerSecond,
            Color.FromRgb(78, 214, 178), 3, plotWidth, plotHeight, axisMaximum);
        AddCurve(points, point => point.MinimumMillimetersPerSecond,
            Color.FromRgb(112, 183, 255), 2, plotWidth, plotHeight, axisMaximum, dashed: true);
    }

    private void AddCurve(
        IReadOnlyList<SpeedCurvePoint> points,
        Func<SpeedCurvePoint, double> selectSpeed,
        Color color,
        double thickness,
        double plotWidth,
        double plotHeight,
        double axisMaximum,
        bool dashed = false)
    {
        var curve = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            Points = new PointCollection(points.Select(point => new Point(
                PlotLeft + plotWidth * point.ProgressPercent / 100d,
                PlotTop + plotHeight * (1d - (LogScaleCheckBox.IsChecked == true
                    ? Math.Log(1d + selectSpeed(point)) / Math.Log(1d + axisMaximum)
                    : selectSpeed(point) / axisMaximum)))))
        };
        if (dashed)
        {
            curve.StrokeDashArray = new DoubleCollection { 4, 2 };
        }

        SpeedChartCanvas.Children.Add(curve);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush) =>
        SpeedChartCanvas.Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false
        });

    private void AddLabel(string text, double x, double y, Brush brush)
    {
        var label = new TextBlock { Text = text, Foreground = brush, FontSize = 12, IsHitTestVisible = false };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        SpeedChartCanvas.Children.Add(label);
    }

    private void Chart_MouseMove(object sender, MouseEventArgs e)
    {
        if (_summary?.Points is not { Count: > 0 } points)
        {
            return;
        }

        var width = SpeedChartCanvas.ActualWidth - PlotLeft - PlotRight;
        var position = e.GetPosition(SpeedChartCanvas);
        if (width <= 0 || position.X < PlotLeft || position.X > PlotLeft + width ||
            position.Y < PlotTop || position.Y > SpeedChartCanvas.ActualHeight - PlotBottom)
        {
            Chart_MouseLeave(sender, e);
            return;
        }

        var index = (int)Math.Round((position.X - PlotLeft) / width * (points.Count - 1));
        var point = points[Math.Clamp(index, 0, points.Count - 1)];
        CursorReadoutText.Text = $"{point.ProgressPercent:0} % · " +
            $"minimum {FormatSpeed(point.MinimumMillimetersPerSecond)} · " +
            $"maximum {FormatSpeed(point.MaximumMillimetersPerSecond)} · " +
            $"moyenne {FormatSpeed(point.MeanMillimetersPerSecond)} mm/s";
    }

    private void Chart_MouseLeave(object sender, MouseEventArgs e) => ResetReadout();

    private void ResetReadout() =>
        CursorReadoutText.Text = "Survole le graphique pour lire les trois vitesses.";

    private static string FormatSpeed(double speed) =>
        speed.ToString(speed >= 10_000 ? "0.##E+0" : "0.#", CultureInfo.CurrentCulture);

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }
}
