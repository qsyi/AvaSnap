using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvaSnap.Services;

/// <summary>コマンドライン診断: <c>AvaSnap.exe --depth-test in.png out.png</c> で
/// 深度推定だけを走らせ、深度マップをグレースケール PNG に書き出す。UI を持たない
/// 単体検証用(被写界深度エフェクトの土台確認)。</summary>
public static class DepthDiagnostic
{
    public static int Run(string inPath, string outPath)
    {
        try
        {
            if (!DepthModel.IsAvailable())
            {
                Console.WriteLine($"モデル取得中: {DepthModel.DownloadUrl}");
                var ok = DepthModel.DownloadAsync(
                    new Progress<double>(p => Console.Write($"\r  {p * 100,5:F1}%"))).GetAwaiter().GetResult();
                Console.WriteLine();
                if (!ok) { Console.Error.WriteLine("モデルのダウンロード/検証に失敗しました。"); return 2; }
            }

            var decoder = new BitmapImage();
            decoder.BeginInit();
            decoder.CacheOption = BitmapCacheOption.OnLoad;
            decoder.UriSource = new Uri(Path.GetFullPath(inPath));
            decoder.EndInit();
            var bgra = new FormatConvertedBitmap(decoder, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * 4 * h];
            bgra.CopyPixels(pixels, w * 4, 0);

            using var est = new DepthEstimator();
            if (!est.TryInitialize(out var err)) { Console.Error.WriteLine(err); return 3; }

            var sw = Stopwatch.StartNew();
            var depth = est.Estimate(pixels, w, h, highPrecision: false);
            sw.Stop();
            if (depth is null) { Console.Error.WriteLine("推定に失敗しました。"); return 4; }

            Console.WriteLine($"OK  {w}x{h} -> depth {depth.Width}x{depth.Height}  GPU={est.UsingGpu}  {sw.ElapsedMilliseconds} ms");

            var gray = new byte[depth.Width * depth.Height];
            for (int i = 0; i < gray.Length; i++) gray[i] = (byte)Math.Clamp(depth.Data[i] * 255f, 0, 255);
            var small = BitmapSource.Create(depth.Width, depth.Height, 96, 96, PixelFormats.Gray8, null, gray, depth.Width);
            var img = new TransformedBitmap(small, new ScaleTransform(w / (double)depth.Width, h / (double)depth.Height));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using var fs = File.Create(Path.GetFullPath(outPath));
            enc.Save(fs);
            Console.WriteLine($"深度マップ: {Path.GetFullPath(outPath)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
