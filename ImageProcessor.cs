using OpenCvSharp;
using OCVRect = OpenCvSharp.Rect;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using Tesseract;

namespace Nepraetor
{
    public class ImageProcessor : IDisposable
    {
        private const int POINT_THRESHOLD = 20;
        private readonly string debugFolder;
        private readonly TesseractEngine tesseract;
        
        public ImageProcessor(string outputFolder)
        {
            debugFolder = Path.Combine(outputFolder, "debug");
            Directory.CreateDirectory(debugFolder);

            // Initialize Tesseract
            string tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
            if (!Directory.Exists(tessDataPath))
            {
                Directory.CreateDirectory(tessDataPath);
                // Extract embedded tessdata
                ExtractTessData(tessDataPath);
            }
            tesseract = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
            tesseract.SetVariable("tessedit_char_whitelist", "×0123456789"); // Only allow these characters
        }

        private void ExtractTessData(string tessDataPath)
        {
            // Embed eng.traineddata as a resource in your project and extract it here
            string resourceName = "Nepraetor.tessdata.eng.traineddata";
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            using (var fileStream = File.Create(Path.Combine(tessDataPath, "eng.traineddata")))
            {
                stream.CopyTo(fileStream);
            }
        }

        public class ViewResult
        {
            public int FrameNumber { get; set; }
            public bool IsDummy { get; set; }
            public int Points { get; set; }  // Actual counted points
            public Dictionary<string, int> ViewReferenceCounts { get; set; } = new Dictionary<string, int>();  // OCR'd numbers
        }

        public ViewResult ProcessPointCloudImage(string imagePath, int frameNumber)
        {
            using var img = Cv2.ImRead(imagePath);
            int sectionHeight = img.Height / 3;
            var debugInfo = new Dictionary<string, Mat>();

            // Save original with section divisions
            using var sectionDebug = img.Clone();
            Cv2.Line(sectionDebug, new Point(0, sectionHeight), new Point(img.Width, sectionHeight), new Scalar(0, 255, 0), 1);
            Cv2.Line(sectionDebug, new Point(0, sectionHeight * 2), new Point(img.Width, sectionHeight * 2), new Scalar(0, 255, 0), 1);
            SaveDebugImage(sectionDebug, frameNumber, "1_sections");

            // Extract and process each section separately
            var sections = new Dictionary<string, (int offset, string name)>
            {
                ["Overhead"] = (0, "overhead"),
                ["Side"] = (sectionHeight, "side"),
                ["Back"] = (sectionHeight * 2, "back")
            };

            var viewCounts = new Dictionary<string, int>();
            foreach (var section in sections)
            {
                // Extract section
                using var sectionMat = new Mat(img, new OCVRect(0, section.Value.offset, img.Width, sectionHeight));
                SaveDebugImage(sectionMat, frameNumber, $"2_{section.Value.name}_original");

                // Process section
                viewCounts[section.Key] = ProcessSection(sectionMat, frameNumber, section.Value.name, debugInfo);
            }

            // Save all debug images
            foreach (var kvp in debugInfo)
            {
                SaveDebugImage(kvp.Value, frameNumber, kvp.Key);
                kvp.Value.Dispose();
            }

            int pointCount = DeterminePointCount(viewCounts);
            bool isDummy = IsDummyData(pointCount, 1368, viewCounts);

            return new ViewResult
            {
                FrameNumber = frameNumber,
                Points = pointCount,
                IsDummy = isDummy,
                ViewReferenceCounts = new Dictionary<string, int>
                {
                    ["Overhead"] = ExtractReferenceNumber(img, 0, "overhead", frameNumber),
                    ["Side"] = ExtractReferenceNumber(img, 1, "side", frameNumber),
                    ["Back"] = ExtractReferenceNumber(img, 2, "back", frameNumber)
                }
            };
        }

        private int ProcessSection(Mat sectionMat, int frameNumber, string section, Dictionary<string, Mat> debugInfo)
        {
            // Convert to grayscale and threshold
            using var gray = sectionMat.CvtColor(ColorConversionCodes.BGR2GRAY);
            using var binary = gray.Threshold(200, 255, ThresholdTypes.Binary);
            debugInfo[$"3_{section}_binary"] = binary.Clone();

            // Find white rectangle
            Point[][] contours;
            HierarchyIndex[] hierarchy;
            Cv2.FindContours(binary, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

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
                var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(1, 1));
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
                Cv2.PutText(countDebug, $"Points: {pointCount}", new Point(10, 30), 
                    HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 255, 0), 2);
                debugInfo[$"8_{section}_count"] = countDebug.Clone();

                return pointCount;
            }

            return 0;
        }

        private int ExtractReferenceNumber(Mat img, int section, string sectionName, int frameNumber)
        {
            int sectionHeight = img.Height / 3;
            
            var sectionMat = new Mat(img, new OCVRect(0, section * sectionHeight, img.Width, sectionHeight));
            
            // Extract bottom-left region where ×1368 appears
            var bottomLeft = new Mat(sectionMat, new OCVRect(0, sectionHeight - 25, 60, 25));
            
            // Prepare image for OCR
            using var gray = bottomLeft.CvtColor(ColorConversionCodes.BGR2GRAY);
            using var binary = gray.Threshold(128, 255, ThresholdTypes.Binary);
            
            // Scale up for better OCR
            using var scaled = new Mat();
            Cv2.Resize(binary, scaled, new Size(binary.Width * 2, binary.Height * 2), 0, 0, InterpolationFlags.Cubic);
            
            SaveDebugImage(scaled, frameNumber, $"6_reference_number_{sectionName}");

            // Convert OpenCV Mat to Bitmap then to Pix for Tesseract
            using var bitmap = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(scaled);
            using var pix = Pix.LoadFromMemory(ImageToByte(bitmap));
            using (var page = tesseract.Process(pix))
            {
                string text = page.GetText().Trim();
                Debug.WriteLine($"OCR Result for section {sectionName}: {text}");
                
                // Try to extract number from text (e.g., "×1368" -> 1368)
                if (text.Contains("×"))
                {
                    string numberPart = text.Substring(text.IndexOf('×') + 1);
                    if (int.TryParse(numberPart, out int number))
                    {
                        return number;
                    }
                }
            }
            
            return 1368;  // Default if OCR fails
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

        public void Dispose()
        {
            tesseract?.Dispose();
        }
    }
} 