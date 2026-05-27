using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Edi.Forms
{
    // Crops a cover image to a fixed aspect ratio (the game card's shape). The image is shown
    // at fit-size; a locked-aspect frame is dragged/resized over it, then the framed region is
    // saved as a PNG under <OutputDir>\covers and returned via CroppedPath.
    public partial class CropDialog : Window
    {
        public string? CroppedPath { get; private set; }

        private readonly double _aspect;          // target width / height
        private readonly BitmapSource _source;
        private double _dispW, _dispH;            // displayed image size (also the overlay size)
        private double _maxFrameW;                // frame width when the slider is at 1.0
        private bool _laidOut;

        private bool _dragging;
        private Point _dragStart;
        private double _startLeft, _startTop;

        public CropDialog(string imagePath, double aspect)
        {
            DarkTitleBar.Apply(this);
            InitializeComponent();

            _aspect = aspect > 0.05 ? aspect : 1.0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            _source = bmp;
            img.Source = _source;
        }

        private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutImage();

        private void LayoutImage()
        {
            double aw = stage.ActualWidth, ah = stage.ActualHeight;
            if (aw < 20 || ah < 20 || _source.PixelWidth <= 0) return;

            double iw = _source.PixelWidth, ih = _source.PixelHeight;
            double s = Math.Min(aw / iw, ah / ih);
            _dispW = iw * s;
            _dispH = ih * s;

            img.Width = _dispW; img.Height = _dispH;
            overlay.Width = _dispW; overlay.Height = _dispH;

            // Largest frame of the locked aspect that fits inside the displayed image.
            _maxFrameW = Math.Min(_dispW, _dispH * _aspect);

            bool first = !_laidOut;
            _laidOut = true;
            UpdateFrame(recenter: first);
        }

        private void UpdateFrame(bool recenter)
        {
            if (_dispW <= 0) return;

            double fw = Math.Max(24, _maxFrameW * sizeSlider.Value);
            double fh = fw / _aspect;
            if (fh > _dispH) { fh = _dispH; fw = fh * _aspect; }

            cropFrame.Width = fw;
            cropFrame.Height = fh;

            double left = recenter || double.IsNaN(Canvas.GetLeft(cropFrame)) ? (_dispW - fw) / 2 : Canvas.GetLeft(cropFrame);
            double top  = recenter || double.IsNaN(Canvas.GetTop(cropFrame))  ? (_dispH - fh) / 2 : Canvas.GetTop(cropFrame);
            Place(left, top, fw, fh);
        }

        private void Place(double left, double top, double fw, double fh)
        {
            left = Math.Max(0, Math.Min(left, _dispW - fw));
            top  = Math.Max(0, Math.Min(top, _dispH - fh));
            Canvas.SetLeft(cropFrame, left);
            Canvas.SetTop(cropFrame, top);

            SetRect(dimTop,    0, 0, _dispW, top);
            SetRect(dimBottom, 0, top + fh, _dispW, _dispH - (top + fh));
            SetRect(dimLeft,   0, top, left, fh);
            SetRect(dimRight,  left + fw, top, _dispW - (left + fw), fh);
        }

        private static void SetRect(Rectangle r, double x, double y, double w, double h)
        {
            r.Width = Math.Max(0, w);
            r.Height = Math.Max(0, h);
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
        }

        private void SizeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_laidOut) return;
            UpdateFrame(recenter: false);
        }

        private void Frame_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragStart = e.GetPosition(overlay);
            _startLeft = Canvas.GetLeft(cropFrame);
            _startTop = Canvas.GetTop(cropFrame);
            cropFrame.CaptureMouse();
        }

        private void Frame_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(overlay);
            Place(_startLeft + (p.X - _dragStart.X), _startTop + (p.Y - _dragStart.Y), cropFrame.Width, cropFrame.Height);
        }

        private void Frame_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            cropFrame.ReleaseMouseCapture();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        private void Crop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                double scale = _source.PixelWidth / _dispW;
                int x = (int)Math.Round(Canvas.GetLeft(cropFrame) * scale);
                int y = (int)Math.Round(Canvas.GetTop(cropFrame) * scale);
                int w = (int)Math.Round(cropFrame.Width * scale);
                int h = (int)Math.Round(cropFrame.Height * scale);

                x = Math.Max(0, Math.Min(x, _source.PixelWidth - 1));
                y = Math.Max(0, Math.Min(y, _source.PixelHeight - 1));
                w = Math.Max(1, Math.Min(w, _source.PixelWidth - x));
                h = Math.Max(1, Math.Min(h, _source.PixelHeight - y));

                var cropped = new CroppedBitmap(_source, new Int32Rect(x, y, w, h));

                var dir = System.IO.Path.Combine(Edi.Core.Edi.OutputDir, "covers");
                Directory.CreateDirectory(dir);
                var file = System.IO.Path.Combine(dir, "crop_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                using (var fs = new FileStream(file, FileMode.Create))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(cropped));
                    enc.Save(fs);
                }

                CroppedPath = file;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Crop failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
