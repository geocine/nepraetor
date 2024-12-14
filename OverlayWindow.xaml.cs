using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Controls.Primitives;

namespace Nepraetor
{
    public partial class OverlayWindow : Window
    {
        private static OverlayWindow? instance;
        private System.Windows.Point lastPosition;
        private bool isDragging;
        private bool isResizing;
        private Rectangle? activeHandle;
        private double originalWidth, originalHeight;
        private double originalLeft, originalTop;

        public static OverlayWindow? Instance => instance;

        public OverlayWindow(int x, int y, int width, int height)
        {
            InitializeComponent();
            
            // Account for the margin in position and size
            this.Left = x - 12;
            this.Top = y - 12;
            this.Width = width + 24;  // 12 pixels margin on each side
            this.Height = height + 24;
            
            this.MouseLeftButtonDown += OnMouseLeftButtonDown;
            this.MouseLeftButtonUp += OnMouseLeftButtonUp;
            this.MouseMove += OnMouseMove;

            // Close any existing overlay window
            if (instance != null)
            {
                instance.Close();
            }
            instance = this;
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Source == CloseButton) return;
            if (e.Source is Rectangle) return; // Don't start window drag if clicking resize handle
            
            isDragging = true;
            lastPosition = e.GetPosition(this);
            this.CaptureMouse();
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            isDragging = false;
            this.ReleaseMouseCapture();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging)
            {
                var currentPosition = e.GetPosition(this);
                var offset = currentPosition - lastPosition;
                this.Left += offset.X;
                this.Top += offset.Y;
            }
        }

        private void OnResizeHandleMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Rectangle handle)
            {
                isResizing = true;
                activeHandle = handle;
                lastPosition = e.GetPosition(this);
                originalWidth = this.Width;
                originalHeight = this.Height;
                originalLeft = this.Left;
                originalTop = this.Top;
                handle.CaptureMouse();
                e.Handled = true;
            }
        }

        private void OnResizeHandleMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (isResizing && activeHandle is not null)
            {
                isResizing = false;
                activeHandle.ReleaseMouseCapture();
                activeHandle = null;
                e.Handled = true;
            }
        }

        private void OnResizeHandleMouseMove(object sender, MouseEventArgs e)
        {
            if (isResizing && activeHandle is not null)
            {
                var currentPosition = e.GetPosition(this);
                var deltaX = currentPosition.X - lastPosition.X;
                var deltaY = currentPosition.Y - lastPosition.Y;

                switch (activeHandle.Name)
                {
                    case "TopLeftHandle":
                        this.Left = originalLeft + deltaX;
                        this.Top = originalTop + deltaY;
                        this.Width = Math.Max(50, originalWidth - deltaX);
                        this.Height = Math.Max(50, originalHeight - deltaY);
                        break;

                    case "TopRightHandle":
                        this.Top = originalTop + deltaY;
                        this.Width = Math.Max(50, originalWidth + deltaX);
                        this.Height = Math.Max(50, originalHeight - deltaY);
                        break;

                    case "BottomLeftHandle":
                        this.Left = originalLeft + deltaX;
                        this.Width = Math.Max(50, originalWidth - deltaX);
                        this.Height = Math.Max(50, originalHeight + deltaY);
                        break;

                    case "BottomRightHandle":
                        this.Width = Math.Max(50, originalWidth + deltaX);
                        this.Height = Math.Max(50, originalHeight + deltaY);
                        break;
                }

                e.Handled = true;
            }
        }

        private void OnCloseButtonClick(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (instance == this)
            {
                instance = null;
            }
        }

        public (int x, int y, int width, int height) GetBounds()
        {
            // Return the actual content bounds (excluding margin)
            return (
                (int)(Left + 12), 
                (int)(Top + 12), 
                (int)(Width - 24), 
                (int)(Height - 24)
            );
        }
    }
}
