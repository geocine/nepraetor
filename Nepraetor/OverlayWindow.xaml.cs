using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Controls.Primitives;
using System.Diagnostics;

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
            
            // Set explicit window properties to ensure consistent behavior
            this.WindowStyle = WindowStyle.None;
            this.ResizeMode = ResizeMode.NoResize;
            this.AllowsTransparency = true;
            this.Background = null;
            
            // Adjust for the 7-pixel offset in both directions
            const int WINDOW_OFFSET = 7;
            
            // Position the window with offset correction
            this.Left = x - WINDOW_OFFSET;
            this.Top = y - WINDOW_OFFSET;
            this.Width = width;
            this.Height = height;
            
            Debug.WriteLine($"Requested Position: ({x}, {y}), Size: {width}x{height}");
            Debug.WriteLine($"Actual Window - Position: ({this.Left}, {this.Top}), Size: {this.Width}x{this.Height}");
            
            // Set up drag handle events
            DragHandle.PreviewMouseLeftButtonDown += OnDragHandleMouseDown;
            DragHandle.PreviewMouseLeftButtonUp += OnDragHandleMouseUp;
            DragHandle.PreviewMouseMove += OnDragHandleMouseMove;

            // Close any existing overlay window
            if (instance != null)
            {
                instance.Close();
            }
            instance = this;
        }

        private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs e)
        {
            isDragging = true;
            lastPosition = e.GetPosition(this);
            DragHandle.CaptureMouse();
            e.Handled = true;
        }

        private void OnDragHandleMouseUp(object sender, MouseButtonEventArgs e)
        {
            isDragging = false;
            DragHandle.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void OnDragHandleMouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging)
            {
                var currentPosition = e.GetPosition(this);
                var offset = currentPosition - lastPosition;
                this.Left += offset.X;
                this.Top += offset.Y;
                Debug.WriteLine($"Window Dragged - Position: ({this.Left}, {this.Top}), Size: {this.Width}x{this.Height}");
                e.Handled = true;
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
                    case "BottomLeftHandle":
                        var newWidth = Math.Max(50, originalWidth - deltaX);
                        var widthDelta = originalWidth - newWidth;
                        this.Left = originalLeft + widthDelta;
                        this.Width = newWidth;
                        this.Height = Math.Max(50, originalHeight + deltaY);
                        break;

                    case "BottomRightHandle":
                        this.Width = Math.Max(50, originalWidth + deltaX);
                        this.Height = Math.Max(50, originalHeight + deltaY);
                        break;
                }

                Debug.WriteLine($"Window Resized - Position: ({this.Left}, {this.Top}), Size: {this.Width}x{this.Height}");
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
            // Return the exact window bounds since they now match the selection
            return (
                (int)Left,
                (int)Top,
                (int)Width,
                (int)Height
            );
        }
    }
}
