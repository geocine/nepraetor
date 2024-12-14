using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Drawing;
using System.Windows.Interop;
using System.Windows.Controls;

namespace Nepraetor
{
    public partial class SelectionWindow : Window
    {
        private System.Windows.Point startPoint;
        private bool isSelecting;

        public SelectionWindow()
        {
            InitializeComponent();
            Cursor = Cursors.Cross;
            this.Focusable = true;
            this.Focus();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.Close();
            }
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            startPoint = e.GetPosition(SelectionCanvas);
            isSelecting = true;

            SelectionRectangle.Width = 0;
            SelectionRectangle.Height = 0;
            Canvas.SetLeft(SelectionRectangle, startPoint.X);
            Canvas.SetTop(SelectionRectangle, startPoint.Y);
            SelectionRectangle.Visibility = Visibility.Visible;
            SizeDisplay.Visibility = Visibility.Visible;

            SelectionCanvas.CaptureMouse();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!isSelecting) return;

            var currentPoint = e.GetPosition(SelectionCanvas);

            var x = Math.Min(currentPoint.X, startPoint.X);
            var y = Math.Min(currentPoint.Y, startPoint.Y);
            var width = Math.Abs(currentPoint.X - startPoint.X);
            var height = Math.Abs(currentPoint.Y - startPoint.Y);

            Canvas.SetLeft(SelectionRectangle, x);
            Canvas.SetTop(SelectionRectangle, y);
            SelectionRectangle.Width = width;
            SelectionRectangle.Height = height;

            SizeDisplay.Text = $"{(int)width} × {(int)height}";
            Canvas.SetLeft(SizeDisplay, x);
            Canvas.SetTop(SizeDisplay, y - 25);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!isSelecting) return;

            SelectionCanvas.ReleaseMouseCapture();
            isSelecting = false;

            var x = (int)Canvas.GetLeft(SelectionRectangle);
            var y = (int)Canvas.GetTop(SelectionRectangle);
            var width = (int)SelectionRectangle.Width;
            var height = (int)SelectionRectangle.Height;

            if (width > 10 && height > 10)
            {
                CreateOverlay(x, y, width, height);
            }

            this.Close();
        }

        private void CreateOverlay(int x, int y, int width, int height)
        {
            // Close any existing overlay window
            if (OverlayWindow.Instance != null)
            {
                OverlayWindow.Instance.Close();
            }

            // Create new overlay window at exact selection coordinates
            // No need to adjust for margin since OverlayWindow constructor handles that
            var overlayWindow = new OverlayWindow(x, y, width, height);
            overlayWindow.Show();
        }
    }
}
