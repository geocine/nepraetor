using OpenCvSharp;
using OCVRect = OpenCvSharp.Rect;
using OCVPoint = OpenCvSharp.Point;
using OCVSize = OpenCvSharp.Size;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using Florence2;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Windows;

namespace Nepraetor
{
    public class ImageProcessor : IDisposable
    {
        private const int POINT_THRESHOLD = 20;
        private readonly string debugFolder;
        private readonly IProgress<(string status, double progress)> progress;
        
        public ImageProcessor(string outputFolder, IProgress<(string status, double progress)> progress = null)
        {
            this.progress = progress;
            debugFolder = Path.Combine(outputFolder, "debug");
            Directory.CreateDirectory(debugFolder);
        }

        public class ViewResult
        {
            public int FrameNumber { get; set; }
            public bool IsDummy { get; set; }
            public int Points { get; set; }  // Actual counted points
            public Dictionary<string, int> ViewReferenceCounts { get; set; } = new Dictionary<string, int>();  // OCR'd numbers
        }

        public async Task<ViewResult> ProcessPointCloudImage(string imagePath, int frameNumber)
        {
            using var img = Cv2.ImRead(imagePath);
            int sectionHeight = img.Height / 3;
            var debugInfo = new Dictionary<string, Mat>();

            progress?.Report(("Initializing OCR processing...", 0));

            // Get raw OCR'd numbers first
            var rawReferenceNumbers = new Dictionary<string, int>();
            var sections = new[] { ("Overhead", 0), ("Side", 1), ("Back", 2) };
            
            for (int i = 0; i < sections.Length; i++)
            {
                var (sectionName, sectionIndex) = sections[i];
                progress?.Report(($"Processing {sectionName} view...", (i * 100.0) / sections.Length));
                
                rawReferenceNumbers[sectionName] = await ExtractReferenceNumber(img, sectionIndex, sectionName.ToLower(), frameNumber);
            }

            progress?.Report(("Validating results...", 90));

            // Get validated reference number using digit-by-digit comparison
            int validatedReferenceNumber = GetMostCommonValue(rawReferenceNumbers.Values);

            // Use the same validated number for all views
            var referenceNumbers = new Dictionary<string, int>
            {
                ["Overhead"] = validatedReferenceNumber,
                ["Side"] = validatedReferenceNumber,
                ["Back"] = validatedReferenceNumber
            };

            // If reference count > threshold, skip point counting
            bool isDummy;
            int pointCount;

            if (validatedReferenceNumber > POINT_THRESHOLD)
            {
                isDummy = false;  // Automatically not dummy if over threshold
                pointCount = validatedReferenceNumber;  // Use validated reference count as point count
            }
            else
            {
                // Only count points if reference count is small
                var viewCounts = new Dictionary<string, int>();
                foreach (var section in new[] { ("Overhead", 0), ("Side", sectionHeight), ("Back", sectionHeight * 2) })
                {
                    using var sectionMat = new Mat(img, new OCVRect(0, section.Item2, img.Width, sectionHeight));
                    viewCounts[section.Item1] = ProcessSection(sectionMat, frameNumber, section.Item1.ToLower(), debugInfo);
                }

                pointCount = DeterminePointCount(viewCounts);
                
                // Check if counted points match reference count
                double difference = Math.Abs(pointCount - validatedReferenceNumber) / (double)validatedReferenceNumber;
                isDummy = difference > 0.1; // 10% tolerance
            }

            // Save debug images
            foreach (var kvp in debugInfo)
            {
                SaveDebugImage(kvp.Value, frameNumber, kvp.Key);
                kvp.Value.Dispose();
            }

            return new ViewResult
            {
                FrameNumber = frameNumber,
                Points = pointCount,
                IsDummy = isDummy,
                ViewReferenceCounts = referenceNumbers  // Use the validated numbers for all views
            };
        }

        private int ProcessSection(Mat sectionMat, int frameNumber, string section, Dictionary<string, Mat> debugInfo)
        {
            // Convert to grayscale and threshold
            using var gray = sectionMat.CvtColor(ColorConversionCodes.BGR2GRAY);
            using var binary = gray.Threshold(200, 255, ThresholdTypes.Binary);
            debugInfo[$"3_{section}_binary"] = binary.Clone();

            // Find white rectangle
            OCVPoint[][] contours;
            HierarchyIndex[] hierarchy;
            binary.FindContours(out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            var whiteRect = contours
                .Select(c => Cv2.BoundingRect(c))
                .Where(r => r.Width > sectionMat.Width / 4 && r.Height > sectionMat.Height / 4)
                .OrderByDescending(r => r.Width * r.Height)
                .FirstOrDefault();

            if (whiteRect != default)
            {
                // Draw detected rectangle
                using var rectDebug = sectionMat.Clone();
                Cv2.Rectangle(rectDebug, whiteRect, new Scalar(0, 255, 0), 1);
                debugInfo[$"4_{section}_rect"] = rectDebug.Clone();

                // Extract ROI and detect red points
                using var roi = new Mat(sectionMat, whiteRect);
                debugInfo[$"5_{section}_roi"] = roi.Clone();

                // Convert to HSV for better color detection
                using var hsv = roi.CvtColor(ColorConversionCodes.BGR2HSV);
                using var mask1 = new Mat();
                using var mask2 = new Mat();
                using var combinedMask = new Mat();

                // Pink/Salmon color in HSV
                // First range (reddish-pink)
                Cv2.InRange(hsv, 
                    new Scalar(0, 10, 100),    // Lower: very low saturation, medium brightness
                    new Scalar(30, 255, 255),  // Upper: include more orange-ish tones
                    mask1);

                // Second range (pink-purple)
                Cv2.InRange(hsv, 
                    new Scalar(150, 10, 100),  // Lower: very low saturation, medium brightness
                    new Scalar(180, 255, 255), // Upper: include purplish tones
                    mask2);

                // Combine both ranges
                Cv2.BitwiseOr(mask1, mask2, combinedMask);

                // Minimal noise cleanup to keep small points
                var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OCVSize(1, 1));
                Cv2.MorphologyEx(combinedMask, combinedMask, MorphTypes.Open, kernel);

                debugInfo[$"6_{section}_mask"] = combinedMask.Clone();

                // Visualize detected points
                using var pointsVisualization = roi.Clone();
                pointsVisualization.SetTo(new Scalar(0, 255, 0), combinedMask);
                debugInfo[$"7_{section}_points"] = pointsVisualization.Clone();

                // Count points using connected components
                using var labels = new Mat();
                using var stats = new Mat();
                using var centroids = new Mat();
                int nLabels = Cv2.ConnectedComponentsWithStats(combinedMask, labels, stats, centroids, 
                    PixelConnectivity.Connectivity8); // Use 8-connectivity to better connect points

                // Filter components by size
                int pointCount = 0;
                for (int i = 1; i < nLabels; i++) // Start from 1 to skip background
                {
                    double area = stats.At<int>(i, 4); // 4 is the area column in stats matrix
                    if (area >= 1 && area <= 25) // Reduced max area to better match point size
                    {
                        pointCount++;
                    }
                }

                // Draw point count on debug image
                using var countDebug = pointsVisualization.Clone();
                Cv2.PutText(countDebug, $"Points: {pointCount}", new OCVPoint(10, 30), 
                    HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 255, 0), 2);
                debugInfo[$"8_{section}_count"] = countDebug.Clone();

                return pointCount;
            }

            return 0;
        }

        private async Task<int> ExtractReferenceNumber(Mat img, int section, string sectionName, int frameNumber)
        {
            int sectionHeight = img.Height / 3;
            var sectionMat = new Mat(img, new OCVRect(0, section * sectionHeight, img.Width, sectionHeight));
            
            // Only use bottom_left ROI as it's the most accurate
            using var roi = new Mat(sectionMat, new OCVRect(10, sectionHeight - 30, 100, 25));

            // Initialize Florence2 once
            var modelSource = new FlorenceModelDownloader("./models");
            var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ImageProcessor>();
            
            progress?.Report(($"Initializing Florence2 for {sectionName}...", 0));
            await modelSource.DownloadModelsAsync(
                status => {
                    Debug.WriteLine($"Download status: {status}");
                    progress?.Report(($"Loading models for {sectionName}: {status}", 10));
                },
                logger,
                CancellationToken.None);
            var model = new Florence2Model(modelSource);

            progress?.Report(($"Processing {sectionName} bottom left region...", 50));
            SaveDebugImage(roi, frameNumber, $"ocr_{sectionName}_bottom_left");

            // Convert OpenCV Mat to byte array for Florence2
            using var memStream = new MemoryStream();
            using var bitmap = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(roi);
            bitmap.Save(memStream, System.Drawing.Imaging.ImageFormat.Png);
            var imageBytes = memStream.ToArray();

            var results = model.Run(TaskTypes.OCR, new[] { new MemoryStream(imageBytes) }, null, CancellationToken.None);
            var text = results.FirstOrDefault()?.PureText?.Trim() ?? "";
            
            progress?.Report(($"Analyzing {sectionName} results...", 90));

            // Extract digits from text
            var digits = new string(text.Where(c => char.IsDigit(c)).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, out int number))
            {
                Debug.WriteLine($"\nOCR Results for section {sectionName}:");
                Debug.WriteLine($"  Region: bottom_left  Number: {number,-5} Raw text: '{text}'");
                Debug.WriteLine($"Selected number {number} from region bottom_left for section {sectionName}");
                progress?.Report(($"Found number {number} in {sectionName}", 100));
                return number;
            }

            Debug.WriteLine($"No numbers found in bottom_left region for section {sectionName}");
            progress?.Report(($"No numbers found in {sectionName}", 100));
            return 0;
        }

        private byte[] ImageToByte(System.Drawing.Bitmap bitmap)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
            return stream.ToArray();
        }

        private void SaveDebugImage(Mat img, int frameNumber, string stage)
        {
            string filename = Path.Combine(debugFolder, $"frame_{frameNumber:D3}_{stage}.png");
            img.SaveImage(filename);
        }

        private int GetMostConsistentCount(IEnumerable<int> counts)
        {
            var countList = counts.OrderBy(c => c).ToList();
            // Return the median count
            return countList[countList.Count / 2];
        }

        private int DeterminePointCount(Dictionary<string, int> viewCounts)
        {
            // Get all counts
            var counts = new[] { viewCounts["Overhead"], viewCounts["Side"], viewCounts["Back"] };
            
            // Calculate statistics
            double mean = counts.Average();
            double stdDev = Math.Sqrt(counts.Select(x => Math.Pow(x - mean, 2)).Average());
            
            // Filter out outliers (counts that are too far from mean)
            var validCounts = counts.Where(c => Math.Abs(c - mean) <= stdDev).ToList();
            
            if (!validCounts.Any())
                return counts.Max(); // If all counts are very different, take the highest
            
            return (int)validCounts.Average();
        }

        private bool IsDummyData(int pointCount, int referenceCount, Dictionary<string, int> viewCounts)
        {
            if (pointCount >= POINT_THRESHOLD)
                return false;

            // Check consistency across views
            double mean = viewCounts.Values.Average();
            double stdDev = Math.Sqrt(viewCounts.Values.Select(x => Math.Pow(x - mean, 2)).Average());
            double coefficientOfVariation = stdDev / mean;

            // If counts are very inconsistent across views, likely dummy
            if (coefficientOfVariation > 0.3) // 30% variation threshold
                return true;

            // Check against reference count
            double tolerance = 0.1; // 10% tolerance
            double difference = Math.Abs(pointCount - referenceCount) / (double)referenceCount;
            
            return difference > tolerance;
        }

        private int GetMostCommonValue(IEnumerable<int> values)
        {
            var validValues = values.Where(x => x > 0).ToList();
            if (!validValues.Any())
                return 0;

            // Convert numbers to strings with leading zeros to align digits
            int maxLength = validValues.Max(x => x.ToString().Length);
            var paddedNumbers = validValues.Select(x => x.ToString().PadLeft(maxLength, '0')).ToList();

            // Build result digit by digit
            var resultDigits = new List<char>();
            
            // Process each digit position from right to left
            for (int pos = maxLength - 1; pos >= 0; pos--)
            {
                var digitsAtPosition = paddedNumbers
                    .Where(n => pos < n.Length)  // Only consider numbers long enough
                    .Select(n => n[pos])
                    .Where(c => c != '0' || resultDigits.Any())  // Ignore leading zeros unless we have digits
                    .ToList();

                if (!digitsAtPosition.Any())
                    continue;

                // Get most common digit at this position
                var mostCommonDigit = digitsAtPosition
                    .GroupBy(d => d)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)  // If tie, take smaller digit
                    .First()
                    .Key;

                resultDigits.Insert(0, mostCommonDigit);
            }

            // Convert back to number
            if (!resultDigits.Any())
                return 0;

            return int.Parse(new string(resultDigits.ToArray()));
        }

        public void Dispose()
        {
            // No resources to dispose
        }
    }
} 