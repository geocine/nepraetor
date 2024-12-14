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
using System.Threading;
using Florence2;
using System.Linq;

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
            RequiredFrames = 40; // Set initial value
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
                        // Start new session when creating new overlay
                        frameCounter = 1;
                        InitializeSessionFolder();
                        UpdateFrameCounter(0);
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
                    
                    // Increment frame counter
                    frameCounter++;
                    
                    // Update frame counter display
                    UpdateFrameCounter(frameCounter - 1);
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
            // Enable process button if there are any frames captured
            ProcessButton.IsEnabled = count > 0;
        }

        private async void OnProcessClick(object sender, RoutedEventArgs e)
        {
            // Disable UI elements
            Dispatcher.Invoke(() =>
            {
                ProcessButton.IsEnabled = false;
                ResetButton.IsEnabled = false;
                ProcessingProgress.Value = 0;
                ProcessingProgress.Visibility = Visibility.Visible;
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = "Initializing...";
            });
            
            try
            {
                var frameResults = new List<ImageProcessor.ViewResult>();
                string csvPath = "";

                // Get actual number of frames in the folder
                string[] existingFrames = Directory.GetFiles(currentSessionFolder, "frame_*.png").OrderBy(f => f).ToArray();
                if (existingFrames.Length == 0)
                {
                    throw new Exception("No frames found to process.");
                }

                // Use actual frame count instead of RequiredFrames
                int totalFramesToProcess = existingFrames.Length;

                // Create progress handler
                IProgress<(string status, double progress)> progressHandler = new Progress<(string status, double progress)>(update =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingProgress.Value = update.progress;
                        StatusText.Text = update.status;
                    });
                });

                // Initialize Florence2 model once
                await Task.Run(async () =>
                {
                    var modelSource = new FlorenceModelDownloader("./models");
                    await Application.Current.Dispatcher.InvokeAsync(() => progressHandler.Report(("Downloading Florence2 models...", 10)));
                    
                    await modelSource.DownloadModelsAsync(
                        status => Application.Current.Dispatcher.InvokeAsync(() => 
                            progressHandler.Report((status.Message, 20 + status.Progress * 0.2))),
                        null,
                        CancellationToken.None);

                    await Application.Current.Dispatcher.InvokeAsync(() => progressHandler.Report(("Initializing model...", 40)));
                    var model = new Florence2Model(modelSource);
                    
                    // Process frames in batches of 3 (overhead, side, back views)
                    const int batchSize = 3;
                    for (int i = 0; i < totalFramesToProcess; i += batchSize)
                    {
                        var batchStreams = new List<MemoryStream>();
                        var batchPaths = new List<string>();
                        
                        // Collect batch of images
                        for (int j = 0; j < batchSize && (i + j) < totalFramesToProcess; j++)
                        {
                            string framePath = existingFrames[i + j];
                            if (File.Exists(framePath))
                            {
                                batchPaths.Add(framePath);
                                var imageBytes = await File.ReadAllBytesAsync(framePath);
                                batchStreams.Add(new MemoryStream(imageBytes));
                            }
                        }

                        if (batchStreams.Any())
                        {
                            // Get the actual frame numbers from filenames
                            var frameNumbers = batchPaths.Select(path => 
                                int.Parse(Path.GetFileNameWithoutExtension(path).Replace("frame_", ""))).ToList();

                            await Application.Current.Dispatcher.InvokeAsync(() => 
                                progressHandler.Report(($"Processing frames {frameNumbers.First()}-{frameNumbers.Last()}...", 
                                                     40 + (i * 50.0 / totalFramesToProcess))));

                            // Process batch
                            using var processor = new ImageProcessor(currentSessionFolder, progressHandler);
                            for (int idx = 0; idx < batchStreams.Count; idx++)
                            {
                                var stream = batchStreams[idx];
                                var path = batchPaths[idx];
                                var frameNumber = frameNumbers[idx];

                                // Update status for each individual frame
                                await Application.Current.Dispatcher.InvokeAsync(() => 
                                    progressHandler.Report(($"Processing frame {frameNumber}/{totalFramesToProcess}...", 
                                                         40 + (i * 50.0 / totalFramesToProcess))));
                                
                                var result = await processor.ProcessPointCloudImage(path, frameNumber);
                                lock (frameResults)
                                {
                                    frameResults.Add(result);
                                }
                                stream.Dispose();
                            }
                        }
                    }

                    // Export results to CSV
                    await Application.Current.Dispatcher.InvokeAsync(() => 
                        progressHandler.Report(("Exporting results to CSV...", 90)));
                    csvPath = Path.Combine(currentSessionFolder, "results.csv");
                    ExportToCsv(frameResults, csvPath);

                    // Final progress update
                    await Application.Current.Dispatcher.InvokeAsync(() => 
                        progressHandler.Report(("Processing complete!", 100)));
                });

                // Show results only after everything is complete
                var resultMessage = new StringBuilder();
                resultMessage.AppendLine($"Results exported to: {csvPath}");
                resultMessage.AppendLine();
                
                // Simplified output format
                foreach (var result in frameResults.OrderBy(r => r.FrameNumber))
                {
                    resultMessage.AppendLine($"{result.FrameNumber} -> {(result.IsDummy ? "yes" : "no")}");
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(resultMessage.ToString(), "Processing Results", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Error processing images: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ProcessButton.IsEnabled = true;
                    ResetButton.IsEnabled = true;
                    ProcessingProgress.Visibility = Visibility.Collapsed;
                    StatusText.Visibility = Visibility.Collapsed;
                });
            }
        }

        private void ExportToCsv(List<ImageProcessor.ViewResult> results, string csvPath)
        {
            var csv = new StringBuilder();
            
            // Add header
            csv.AppendLine("Frame,IsDummy,Points,OverheadCount,SideCount,BackCount");
            
            // Add data rows
            foreach (var result in results.OrderBy(r => r.FrameNumber))
            {
                csv.AppendLine($"{result.FrameNumber}," +
                              $"{result.IsDummy}," +
                              $"{result.Points}," +
                              $"{result.ViewReferenceCounts["Overhead"]}," +
                              $"{result.ViewReferenceCounts["Side"]}," +
                              $"{result.ViewReferenceCounts["Back"]}");
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
