using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Edi.Core.Device.Simulator;
using Edi.Core.Funscript.Command;
using Edi.Core.Gallery.Funscript;

namespace Edi.Forms
{
    // Real-time visualizer for the funscript EDI is currently playing: draws the script line,
    // a playhead sweeping across it, a live position dot, and the script name (flashes on swap).
    // Reads live state from a PreviewDevice each frame via CompositionTarget.Rendering.
    public partial class FunscriptPreview : UserControl
    {
        public static readonly DependencyProperty DeviceProperty =
            DependencyProperty.Register(nameof(Device), typeof(PreviewDevice), typeof(FunscriptPreview));

        public PreviewDevice? Device
        {
            get => (PreviewDevice?)GetValue(DeviceProperty);
            set => SetValue(DeviceProperty, value);
        }

        private object? _drawnGallery;
        private FunscriptGallery? _lastGallery;   // kept so the line can stay frozen while paused/stopped
        private double _drawnW, _drawnH;
        private int _drawnMin, _drawnMax = 100;   // last range the line was drawn at (for Live-Edit rescaling)
        private double _drawnVib;                  // last vibration-overlay amount the line was drawn at
        private bool _hooked;

        // Live "Vibration" overlay amount (0-100) from the UI bar: draws a high-frequency buzz on top of
        // the funscript line and jitters the live dot, so the added vibration is visible on playback.
        public double VibrationAmount { get; set; }
        private readonly Stopwatch _vibClock = Stopwatch.StartNew();

        // Demo / test mode: animates a synthetic funscript on its own clock so the visualizer can
        // be exercised with no device connected and nothing playing. Lives entirely in the control.
        private bool _demo;
        private FunscriptGallery? _demoGallery;
        private readonly Stopwatch _demoClock = new();

        public bool IsDemo => _demo;

        public FunscriptPreview()
        {
            InitializeComponent();
            Loaded   += (_, _) => Hook(true);
            Unloaded += (_, _) => Hook(false);
        }

        public void StartDemo()
        {
            _demoGallery = BuildDemoGallery();
            _drawnGallery = null;          // force a rebuild on the next frame
            _demoClock.Restart();
            _demo = true;
            if (btnDemo != null) btnDemo.Content = "■ Stop test";
        }

        public void StopDemo()
        {
            _demo = false;
            _demoClock.Stop();
            _demoGallery = null;
            linePoly.Points.Clear();
            playhead.Visibility = Visibility.Collapsed;
            dot.Visibility = Visibility.Collapsed;
            lblName.Text = "";
            _drawnGallery = null;
            _lastGallery = null;
            lblEmpty.Visibility = Visibility.Visible;
            if (btnDemo != null) btnDemo.Content = "▶ Test pattern";
        }

        private void Demo_Click(object sender, RoutedEventArgs e)
        {
            if (_demo) StopDemo();
            else StartDemo();
        }

        private void Hook(bool on)
        {
            if (on && !_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; }
            else if (!on && _hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; }
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (!IsVisible) return;   // skip work while the panel is hidden

            // Demo mode draws a synthetic script on its own clock — unless real playback starts,
            // in which case it yields so the live data takes over.
            if (_demo)
            {
                var live = Device?.CurrentGallery;
                bool liveActive = live != null && live.Duration > 0 && live.Commands != null && live.Commands.Count > 0;
                if (liveActive) { StopDemo(); }
                else { DrawDemoFrame(); return; }
            }

            var dev = Device;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (dev == null)
            {
                // Preview detached (eye toggled off) — reset to the placeholder.
                _lastGallery = null; _drawnGallery = null;
                linePoly.Points.Clear();
                playhead.Visibility = Visibility.Collapsed;
                dot.Visibility = Visibility.Collapsed;
                lblName.Text = "";
                lblEmpty.Visibility = Visibility.Visible;
                return;
            }
            if (w < 4 || h < 4) return;

            // Live Edit (Intensity) drives the device's output range; mirror it so the drawn line + dot
            // scale like the toy's real output. Vibration adds a buzz overlay (ripple + dot jitter below).
            int dMin = dev.Min, dMax = dev.Max;
            double vib = VibrationAmount;

            var gallery = dev.CurrentGallery;
            bool playing = gallery != null && gallery.Duration > 0 && gallery.Commands != null && gallery.Commands.Count > 0;

            if (!playing)
            {
                // Paused / stopped: FREEZE the last line with the playhead parked where it was,
                // instead of blanking. Still rescale if the user nudges Intensity while paused.
                if (_lastGallery != null)
                {
                    lblEmpty.Visibility = Visibility.Collapsed;
                    if (w != _drawnW || h != _drawnH || dMin != _drawnMin || dMax != _drawnMax || vib != _drawnVib)
                    {
                        RebuildGrid(w, h);
                        RebuildLine(_lastGallery, w, h, dMin, dMax, vib);
                        _drawnW = w; _drawnH = h; _drawnMin = dMin; _drawnMax = dMax; _drawnVib = vib;
                    }
                    return;   // leave playhead + dot frozen at their last spot
                }
                // Nothing has played yet — show the placeholder.
                if (linePoly.Points.Count > 0) linePoly.Points.Clear();
                playhead.Visibility = Visibility.Collapsed;
                dot.Visibility = Visibility.Collapsed;
                lblEmpty.Visibility = Visibility.Visible;
                if (_drawnGallery != null) { lblName.Text = ""; _drawnGallery = null; }
                return;
            }
            lblEmpty.Visibility = Visibility.Collapsed;
            _lastGallery = gallery;

            bool swap = !ReferenceEquals(gallery, _drawnGallery);
            if (swap || w != _drawnW || h != _drawnH || dMin != _drawnMin || dMax != _drawnMax || vib != _drawnVib)
            {
                RebuildGrid(w, h);
                RebuildLine(gallery!, w, h, dMin, dMax, vib);
                _drawnGallery = gallery; _drawnW = w; _drawnH = h; _drawnMin = dMin; _drawnMax = dMax; _drawnVib = vib;
                lblName.Text = gallery!.Name ?? "";
                if (swap)
                    nameChip.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(350)));
            }

            double x = Math.Clamp(dev.CurrentTime / (double)gallery!.Duration, 0, 1) * w;
            playhead.X1 = playhead.X2 = x;
            playhead.Y1 = 0; playhead.Y2 = h;
            playhead.Visibility = Visibility.Visible;

            double y = (1 - Math.Clamp(dev.ProgressValue, 0, 100) / 100.0) * h;
            if (vib > 0.5)
            {
                // Jitter the live dot to show the added vibration buzz on top of the script position.
                double amp = (vib / 100.0) * h * 0.07;
                y += amp * Math.Sin(_vibClock.Elapsed.TotalSeconds * (2 * Math.PI) * 24);
                y = Math.Clamp(y, 0, h);
            }
            Canvas.SetLeft(dot, x - dot.Width / 2);
            Canvas.SetTop(dot, y - dot.Height / 2);
            dot.Visibility = Visibility.Visible;
        }

        private void DrawDemoFrame()
        {
            var g = _demoGallery;
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (g == null || w < 4 || h < 4) return;
            lblEmpty.Visibility = Visibility.Collapsed;

            if (!ReferenceEquals(g, _drawnGallery) || w != _drawnW || h != _drawnH)
            {
                RebuildGrid(w, h);
                RebuildLine(g, w, h);
                _drawnGallery = g; _drawnW = w; _drawnH = h;
                lblName.Text = g.Name ?? "";
                nameChip.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(350)));
            }

            int dur = g.Duration;
            int t = dur <= 0 ? 0 : (int)(_demoClock.ElapsedMilliseconds % dur);
            double x = (dur <= 0 ? 0 : (double)t / dur) * w;
            playhead.X1 = playhead.X2 = x;
            playhead.Y1 = 0; playhead.Y2 = h;
            playhead.Visibility = Visibility.Visible;

            double y = (1 - Math.Clamp(ValueAt(g, t), 0, 100) / 100.0) * h;
            Canvas.SetLeft(dot, x - dot.Width / 2);
            Canvas.SetTop(dot, y - dot.Height / 2);
            dot.Visibility = Visibility.Visible;
        }

        // Value of the synthetic script at time t (linear interp between commands == rides the line).
        private static double ValueAt(FunscriptGallery g, int t)
        {
            var cmds = g.Commands;
            if (cmds == null || cmds.Count == 0) return 0;
            double prevT = 0, prevV = cmds[0].InitialValue;
            foreach (var c in cmds)
            {
                if (t <= c.AbsoluteTime)
                {
                    double span = c.AbsoluteTime - prevT;
                    double f = span <= 0 ? 1 : Math.Clamp((t - prevT) / span, 0, 1);
                    return prevV + (c.Value - prevV) * f;
                }
                prevT = c.AbsoluteTime; prevV = c.Value;
            }
            return cmds[^1].Value;
        }

        // A varied stroke pattern (fast/slow, shallow/deep) so the visualizer has something lively.
        private static FunscriptGallery BuildDemoGallery()
        {
            var pts = new (int millis, int val)[]
            {
                (600,100),(400,0),(500,80),(300,20),(250,90),(250,10),
                (700,60),(500,100),(400,0),(900,55),(300,100),(600,0),
                (350,75),(350,25),(800,100),(500,0),
            };
            var cmds = new List<CmdLinear>();
            CmdLinear? prev = null;
            long at = 0;
            foreach (var (millis, val) in pts)
            {
                at += millis;
                var c = new CmdLinear { Prev = prev, AbsoluteTime = at, Millis = millis, Value = val };
                if (prev != null) prev.Next = c;
                cmds.Add(c);
                prev = c;
            }
            return new FunscriptGallery { Name = "Test pattern (demo)", Duration = (int)at, Loop = true, Commands = cmds };
        }

        // dMin/dMax = the device's live output range (set by the Intensity/Vibration bars). Each script
        // value 0-100 is mapped into [dMin,dMax] so the line shows the toy's actual stroke depth.
        private void RebuildLine(FunscriptGallery g, double w, double h, int dMin = 0, int dMax = 100, double vib = 0)
        {
            linePoly.Points.Clear();
            var cmds = g.Commands;
            if (cmds == null || cmds.Count == 0 || g.Duration <= 0) return;

            double Scale(double v) => dMin + (dMax - dMin) * (Math.Clamp(v, 0, 100) / 100.0);

            if (vib <= 0.5)
            {
                // Clean funscript curve (plot the command points).
                linePoly.Points.Add(new Point(0, (1 - Scale(cmds[0].InitialValue) / 100.0) * h));
                foreach (var cmd in cmds)
                {
                    double x = Math.Clamp(cmd.AbsoluteTime / (double)g.Duration, 0, 1) * w;
                    double y = (1 - Scale(cmd.Value) / 100.0) * h;
                    linePoly.Points.Add(new Point(x, y));
                }
                return;
            }

            // Vibration overlay: ride the funscript curve but add a high-frequency ripple whose amplitude
            // grows with the Vibration bar, so the "added vibration" is visible right on the line.
            double rippleAmp = (vib / 100.0) * 13.0;   // in 0-100 value units
            const double cyclePx = 9.0;                // one buzz cycle ≈ every 9 px
            int n = Math.Max(48, (int)(w / 3));        // ≈ one sample every 3 px
            for (int i = 0; i <= n; i++)
            {
                double frac = (double)i / n;
                double x = frac * w;
                double baseV = Scale(ValueAt(g, (int)(frac * g.Duration)));
                double ripple = rippleAmp * Math.Sin(x / cyclePx * (2 * Math.PI));
                double v = Math.Clamp(baseV + ripple, 0, 100);
                linePoly.Points.Add(new Point(x, (1 - v / 100.0) * h));
            }
        }

        private void RebuildGrid(double w, double h)
        {
            gridCanvas.Children.Clear();
            var brush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            brush.Freeze();
            foreach (double f in new[] { 0.0, 0.5, 1.0 })
            {
                double yy = f * h;
                gridCanvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = yy, Y2 = yy, Stroke = brush, StrokeThickness = 1 });
            }
        }
    }
}
