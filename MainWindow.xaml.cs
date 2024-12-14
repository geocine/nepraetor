﻿using System.Windows;
using NHotkey;
using NHotkey.Wpf;
using System.Drawing;
using System.Windows.Input;

namespace Nepraetor
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            RegisterHotkey();
        }

        private void RegisterHotkey()
        {
            try
            {
                HotkeyManager.Current.AddOrReplace("CaptureRegion", Key.S, ModifierKeys.Alt, (sender, e) =>
                {
                    if (OverlayWindow.Instance != null)
                    {
                        var (x, y, width, height) = OverlayWindow.Instance.GetBounds();
                        CaptureScreenshot(x, y, width, height);
                    }
                    else
                    {
                        var selectionWindow = new SelectionWindow();
                        selectionWindow.Show();
                    }
                    e.Handled = true;
                });
            }
            catch (HotkeyAlreadyRegisteredException)
            {
                MessageBox.Show("Failed to register hotkey Alt + S. It may be in use by another application.", 
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CaptureScreenshot(int x, int y, int width, int height)
        {
            using (var bitmap = new Bitmap(width, height))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
                }

                // Save the screenshot or handle it as needed
                // For now, we'll just save it to the desktop
                string desktopPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
                string fileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string filePath = System.IO.Path.Combine(desktopPath, fileName);
                bitmap.Save(filePath);
            }
        }
    }
}
