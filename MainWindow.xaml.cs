﻿using System.Windows;
using NHotkey;
using NHotkey.Wpf;
using System.Drawing;
using System.Windows.Input;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Nepraetor
{
    public partial class MainWindow : Window
    {
        public static readonly DependencyProperty RequiredFramesProperty =
            DependencyProperty.Register(nameof(RequiredFrames), typeof(int), typeof(MainWindow), new PropertyMetadata(1));

        public int RequiredFrames
        {
            get { return (int)GetValue(RequiredFramesProperty); }
            set { SetValue(RequiredFramesProperty, value); }
        }

        private int frameCounter = 1;
        private string currentSessionFolder = "";

        public MainWindow()
        {
            InitializeComponent();
            RequiredFrames = 1; // Set initial value
            RegisterHotkey();
            InitializeSessionFolder();
            UpdateFrameCounter(0); // Initialize counter display
        }

        private void InitializeSessionFolder()
        {
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string screenshotsDir = Path.Combine(appDirectory, "Screenshots");
            string dateTimeFolder = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            currentSessionFolder = Path.Combine(screenshotsDir, dateTimeFolder);
            
            // Create directories if they don't exist
            Directory.CreateDirectory(currentSessionFolder);
            
            // Initialize frame counter by checking existing files
            string[] existingFiles = Directory.GetFiles(currentSessionFolder, "frame_*.png");
            if (existingFiles.Length > 0)
            {
                int maxFrame = existingFiles
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .Select(f => f.Replace("frame_", ""))
                    .Where(f => int.TryParse(f, out _))
                    .Select(f => int.Parse(f))
                    .Max();
                frameCounter = maxFrame + 1;
            }
            
            Debug.WriteLine($"Session folder: {currentSessionFolder}");
            Debug.WriteLine($"Starting with frame: {frameCounter}");
        }

        protected override void OnClosed(EventArgs e)
        {
            // Close any open overlay window
            if (OverlayWindow.Instance != null)
            {
                OverlayWindow.Instance.Close();
            }

            // Unregister hotkey before closing
            try
            {
                HotkeyManager.Current.Remove("CaptureRegion");
            }
            catch { /* Ignore any errors during cleanup */ }

            // Shutdown the application
            Application.Current.Shutdown();

            base.OnClosed(e);
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
            // Hide overlay temporarily
            OverlayWindow.Instance?.Hide();
            System.Threading.Thread.Sleep(100); // Give time for overlay to hide

            try
            {
                using (var bitmap = new Bitmap(width, height))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
                    }

                    // Create filename with frame number
                    string fileName = $"frame_{frameCounter:D3}.png"; // This will format as 001, 002, etc.
                    string filePath = Path.Combine(currentSessionFolder, fileName);
                    
                    Debug.WriteLine($"Saving screenshot to: {filePath}");
                    Debug.WriteLine($"Capture area: x={x}, y={y}, width={width}, height={height}");
                    
                    bitmap.Save(filePath);
                    
                    // Update frame counter display
                    UpdateFrameCounter(frameCounter);
                    
                    // Reset counter if we reach the required number of frames
                    if (frameCounter > RequiredFrames)
                    {
                        frameCounter = 1;
                        string timeStamp = DateTime.Now.ToString("HH-mm-ss");
                        currentSessionFolder = Path.Combine(
                            Path.GetDirectoryName(currentSessionFolder)!,
                            $"{DateTime.Now:yyyy-MM-dd}_{timeStamp}");
                        Directory.CreateDirectory(currentSessionFolder);
                        UpdateFrameCounter(0);
                    }
                }
            }
            finally
            {
                // Show the overlay again after capturing
                OverlayWindow.Instance?.Show();
            }
        }

        private void UpdateFrameCounter(int count)
        {
            FrameCounter.Text = $"Frames: {count}/{RequiredFrames}";
            ProcessButton.IsEnabled = count >= RequiredFrames;
        }

        private async void OnProcessClick(object sender, RoutedEventArgs e)
        {
            ProcessButton.IsEnabled = false;
            ProcessingProgress.Value = 0;
            ProcessingProgress.Visibility = Visibility.Visible;
            
            try
            {
                using var processor = new ImageProcessor(currentSessionFolder);
                var results = new List<ImageProcessor.ViewResult>();
                
                // Process frames
                for (int i = 1; i <= RequiredFrames; i++)
                {
                    string framePath = Path.Combine(currentSessionFolder, $"frame_{i:D3}.png");
                    if (File.Exists(framePath))
                    {
                        var frameResult = await Task.Run(() => processor.ProcessPointCloudImage(framePath, i));
                        results.Add(frameResult);
                        
                        // Update progress
                        ProcessingProgress.Value = i;
                    }
                }

                // Export to CSV
                string csvPath = Path.Combine(currentSessionFolder, "results.csv");
                await Task.Run(() => ExportToCsv(results, csvPath));

                // Show results
                var resultMessage = new StringBuilder();
                resultMessage.AppendLine($"Processing complete for {currentSessionFolder}");
                resultMessage.AppendLine($"Results exported to: {csvPath}");
                resultMessage.AppendLine();
                
                // Show average counts for each view
                foreach (var viewName in new[] { "Overhead", "Side", "Back" })
                {
                    resultMessage.AppendLine($"{viewName}:");
                    resultMessage.AppendLine($"Average point count: {results.Average(r => r.ViewReferenceCounts[viewName]):F0}");
                    resultMessage.AppendLine();
                }

                MessageBox.Show(resultMessage.ToString(), "Processing Results", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing images: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ProcessButton.IsEnabled = true;
                ProcessingProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void ExportToCsv(List<ImageProcessor.ViewResult> results, string csvPath)
        {
            var csv = new StringBuilder();
            
            // Add header
            csv.AppendLine("Frame,IsDummy,Points,OverheadCount,SideCount,BackCount,Consistency");
            
            // Add data rows
            foreach (var result in results.OrderBy(r => r.FrameNumber))
            {
                // Calculate consistency measure
                double mean = result.ViewReferenceCounts.Values.Average();
                double stdDev = Math.Sqrt(result.ViewReferenceCounts.Values.Select(x => Math.Pow(x - mean, 2)).Average());
                double consistency = mean > 0 ? stdDev / mean : 0;

                csv.AppendLine($"{result.FrameNumber}," +
                              $"{result.IsDummy}," +
                              $"{result.Points}," +
                              $"{result.ViewReferenceCounts["Overhead"]}," +
                              $"{result.ViewReferenceCounts["Side"]}," +
                              $"{result.ViewReferenceCounts["Back"]}," +
                              $"{consistency:F3}");
            }
            
            File.WriteAllText(csvPath, csv.ToString());
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will start a new session. Continue?",
                "New Session",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // Close overlay if open
                OverlayWindow.Instance?.Close();

                // Create new session (old one remains untouched)
                frameCounter = 1;
                InitializeSessionFolder(); // This will create a new timestamped folder
                UpdateFrameCounter(0);

                Debug.WriteLine($"Started new session in: {currentSessionFolder}");
            }
        }
    }
}
