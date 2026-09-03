using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AvaSnap.Services;

/// <summary>Depth Anything V2 Small(fp16 ONNX)による単眼深度推定。合成結果(背景 +
/// アバター)を入力に、被写界深度ぼかし用の相対深度マップを返す。ONNX Runtime +
/// DirectML EP で、アプリが既に要求している DX12 GPU 上で走る(EP 生成に失敗したら
/// CPU にフォールバック)。モデルの入出力は float32(重みのみ fp16、内部で Cast)。
/// 前処理は preprocessor_config.json に合わせる: 1/255 → ImageNet 正規化、
/// アスペクト維持で 14 の倍数へリサイズ(既定 518、高精度 1036)、NCHW。
/// 出力(逆深度・大きいほど手前)は min-max 正規化し、8bit グレーとして元解像度へ
/// 拡大してから float 0..1 で返す(1 = 最も手前)。</summary>
public sealed class DepthEstimator : IDisposable
{
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };
    private const int PatchMultiple = 14;

    private InferenceSession? _session;
    private string _inputName = "pixel_values";
    private string _outputName = "predicted_depth";
    public bool UsingGpu { get; private set; }

    /// <summary>セッションを用意する。モデル未取得なら false + 説明。ORT 初期化に
    /// 失敗しても false(呼び出し側が機能を無効化してメッセージ表示する想定)。</summary>
    public bool TryInitialize(out string? error)
    {
        error = null;
        if (_session is not null) return true;

        if (!DepthModel.IsAvailable())
        {
            error = "深度推定モデルが未取得です。";
            return false;
        }

        try
        {
            var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                // DirectML EP の要件
                EnableMemoryPattern = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };
            try
            {
                opts.AppendExecutionProvider_DML(0);
                UsingGpu = true;
            }
            catch (Exception)
            {
                UsingGpu = false; // 既定(CPU)EP のまま
            }

            _session = new InferenceSession(DepthModel.ModelPath, opts);
            _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? _inputName;
            _outputName = _session.OutputMetadata.Keys.FirstOrDefault() ?? _outputName;
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or BadImageFormatException or IOException)
        {
            error = "深度推定エンジンを初期化できませんでした: " + ex.Message;
            _session = null;
            return false;
        }
    }

    /// <summary>合成 BGRA(stride = <paramref name="width"/> * 4)から相対深度マップを推定する。
    /// 戻りは長さ width*height、値 0..1(1 = 最も手前)。失敗時は null。</summary>
    public float[]? Estimate(byte[] bgra, int width, int height, bool highPrecision)
    {
        if (_session is null && !TryInitialize(out _)) return null;
        if (_session is null) return null;
        if (width <= 0 || height <= 0 || bgra.Length < width * 4 * height) return null;

        try
        {
            int target = highPrecision ? 1036 : 518;
            var (rw, rh) = FitToPatchGrid(width, height, target);

            var src = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            var scaled = new TransformedBitmap(src, new ScaleTransform(rw / (double)width, rh / (double)height));
            int sw = scaled.PixelWidth, sh = scaled.PixelHeight;
            var scaledPixels = new byte[sw * 4 * sh];
            scaled.CopyPixels(scaledPixels, sw * 4, 0);

            var input = new DenseTensor<float>(new[] { 1, 3, sh, sw });
            for (int y = 0; y < sh; y++)
            {
                int row = y * sw * 4;
                for (int x = 0; x < sw; x++)
                {
                    int i = row + x * 4;
                    float b = scaledPixels[i] / 255f;
                    float g = scaledPixels[i + 1] / 255f;
                    float r = scaledPixels[i + 2] / 255f;
                    input[0, 0, y, x] = (r - Mean[0]) / Std[0];
                    input[0, 1, y, x] = (g - Mean[1]) / Std[1];
                    input[0, 2, y, x] = (b - Mean[2]) / Std[2];
                }
            }

            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
            var outTensor = results.First(v => v.Name == _outputName).AsTensor<float>();

            // 出力は [1,H,W] か [1,1,H,W]。末尾2次元を H,W とみなす。
            var dims = outTensor.Dimensions;
            int oh = dims[^2], ow = dims[^1];
            var flat = outTensor.ToArray();

            float min = float.MaxValue, max = float.MinValue;
            foreach (var v in flat) { if (v < min) min = v; if (v > max) max = v; }
            float range = max - min;
            if (range < 1e-6f) range = 1f;

            // 8bit グレーへ正規化 → WPF の縮小フィルタで元解像度へ拡大 → float 0..1
            var gray = new byte[ow * oh];
            for (int k = 0; k < gray.Length && k < flat.Length; k++)
                gray[k] = (byte)Math.Clamp((flat[k] - min) / range * 255f, 0, 255);

            var depthSmall = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Gray8, null, gray, ow);
            var depthFull = new TransformedBitmap(depthSmall, new ScaleTransform(width / (double)ow, height / (double)oh));
            int dw = depthFull.PixelWidth, dh = depthFull.PixelHeight;
            var depthBytes = new byte[dw * dh];
            depthFull.CopyPixels(depthBytes, dw, 0);

            var result = new float[width * height];
            for (int y = 0; y < height; y++)
            {
                int sy = Math.Min(y, dh - 1);
                for (int x = 0; x < width; x++)
                {
                    int sx = Math.Min(x, dw - 1);
                    result[y * width + x] = depthBytes[sy * dw + sx] / 255f;
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>アスペクト維持で target 内に収め、各辺を <see cref="PatchMultiple"/> の倍数に丸める。</summary>
    private static (int W, int H) FitToPatchGrid(int w, int h, int target)
    {
        double scale = Math.Min(target / (double)w, target / (double)h);
        int rw = (int)Math.Round(w * scale / PatchMultiple) * PatchMultiple;
        int rh = (int)Math.Round(h * scale / PatchMultiple) * PatchMultiple;
        rw = Math.Clamp(rw, PatchMultiple, target);
        rh = Math.Clamp(rh, PatchMultiple, target);
        return (rw, rh);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
