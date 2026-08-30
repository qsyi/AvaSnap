using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>ExtractToneGradientColors + ApplyToneGradient の GPU 版。他の仕上げ
/// シェーダと違い per-pixel の前に画像全体の加重平均が要るので、1スレッド1行で
/// 小バッファへ集計し、行間の合計だけ CPU で取る(ツリーリダクションではない。
/// per-row ループがボトルネックになったら見直す)。</summary>
public static class GpuToneGradient
{
    /// <summary>DX12 対応 GPU が無ければ false(<paramref name="pixels"/> は不変)。
    /// lightR/G/B・darkR/G/B は勾配の両端色。毎回の自動計算はしない
    /// (自動判定は <see cref="TryDetectColors"/> を「自動判定」ボタンが明示的に呼ぶ)。</summary>
    public static bool TryApply(byte[] pixels, int stride, int width, int height, double amount, double rotationDegrees,
        byte lightR, byte lightG, byte lightB, byte darkR, byte darkG, byte darkB)
    {
        if (amount <= 0) return true;
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));
            ReadWriteTexture2D<Bgra32, float4> texture = GpuTexturePool.Rent(device, "ToneGradient", width, height);
            texture.CopyFrom(pixelSpan);

            ApplyToTexture(texture, device, width, height, amount, rotationDegrees, lightR, lightG, lightB, darkR, darkG, darkB);

            texture.CopyTo(pixelSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><see cref="TryApply"/> と同じ処理を、既に GPU 上にある
    /// <paramref name="texture"/> へ直接かける(写真のアップロード/ダウンロード無し)。
    /// GpuCompositeChain から使う。</summary>
    internal static bool ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> texture, GraphicsDevice device, int width, int height, double amount, double rotationDegrees,
        byte lightR, byte lightG, byte lightB, byte darkR, byte darkG, byte darkB)
    {
        if (amount <= 0) return true;

        double rad = rotationDegrees * Math.PI / 180.0;
        float dirX = (float)-Math.Sin(rad), dirY = (float)Math.Cos(rad);
        float cx = (float)((width - 1) / 2.0), cy = (float)((height - 1) / 2.0);
        float maxExtent = (float)((Math.Abs(dirX) * width + Math.Abs(dirY) * height) / 2.0);
        if (maxExtent < 1e-6f) maxExtent = 1f;
        float strength = (float)(amount / 100.0);

        device.For(width, height, new ToneGradientApplyShader(texture, dirX, dirY, cx, cy, maxExtent, strength,
            lightB, lightG, lightR, darkB, darkG, darkR));

        return true;
    }

    /// <summary>画像全体の加重平均から明色/暗色を推定する一回きりの処理。以前は毎
    /// レンダー走っていたが、今は「自動判定」ボタンからのみ呼ばれ、結果はユーザーが
    /// 編集できる明色/暗色フィールドになる。GPU が無ければ out は白/黒既定のまま false。</summary>
    public static bool TryDetectColors(byte[] pixels, int stride, int width, int height,
        out byte lightR, out byte lightG, out byte lightB, out byte darkR, out byte darkG, out byte darkB)
    {
        lightR = lightG = lightB = 255;
        darkR = darkG = darkB = 0;
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));
            ReadWriteTexture2D<Bgra32, float4> texture = GpuTexturePool.Rent(device, "ToneGradientDetect", width, height);
            texture.CopyFrom(pixelSpan);

            using ReadWriteBuffer<float> rowSums = device.AllocateReadWriteBuffer<float>(height * 8);
            device.For(height, new ToneGradientRowSumShader(texture, rowSums, width));

            var rowSumsArray = new float[height * 8];
            rowSums.CopyTo(rowSumsArray);

            double brightW = 0, brightB = 0, brightG = 0, brightR = 0;
            double darkW = 0, darkBAcc = 0, darkGAcc = 0, darkRAcc = 0;
            for (int y = 0; y < height; y++)
            {
                int b = y * 8;
                brightW += rowSumsArray[b]; brightB += rowSumsArray[b + 1]; brightG += rowSumsArray[b + 2]; brightR += rowSumsArray[b + 3];
                darkW += rowSumsArray[b + 4]; darkBAcc += rowSumsArray[b + 5]; darkGAcc += rowSumsArray[b + 6]; darkRAcc += rowSumsArray[b + 7];
            }

            lightR = (byte)Math.Clamp(brightW > 1e-6 ? brightR / brightW : 255, 0, 255);
            lightG = (byte)Math.Clamp(brightW > 1e-6 ? brightG / brightW : 255, 0, 255);
            lightB = (byte)Math.Clamp(brightW > 1e-6 ? brightB / brightW : 255, 0, 255);
            darkR = (byte)Math.Clamp(darkW > 1e-6 ? darkRAcc / darkW : 0, 0, 255);
            darkG = (byte)Math.Clamp(darkW > 1e-6 ? darkGAcc / darkW : 0, 0, 255);
            darkB = (byte)Math.Clamp(darkW > 1e-6 ? darkBAcc / darkW : 0, 0, 255);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>1スレッド1行。CPU 版 ExtractToneGradientColors の per-pixel 加重
/// (smoothstep の明/暗ウェイト + 暗ピクセルは HSL 彩度ぶん増幅)をそのまま再現し、
/// 行ごとに 8 float へ蓄積する。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ToneGradientRowSumShader(
    IReadWriteNormalizedTexture2D<float4> texture,
    ReadWriteBuffer<float> rowSums,
    int width) : IComputeShader
{
    public void Execute()
    {
        int y = ThreadIds.X;
        float bwSum = 0f, bwB = 0f, bwG = 0f, bwR = 0f;
        float dwSum = 0f, dwB = 0f, dwG = 0f, dwR = 0f;

        for (int x = 0; x < width; x++)
        {
            float4 px = texture[new int2(x, y)];
            float b = px.B * 255f, g = px.G * 255f, r = px.R * 255f;
            float lum01 = Hlsl.Saturate((0.299f * r + 0.587f * g + 0.114f * b) / 255f);
            float bw = Smoothstep(lum01, 0.6f, 1f);
            float dw = 1f - Smoothstep(lum01, 0f, 0.4f);
            if (dw > 0f)
            {
                float sat = Saturation(r, g, b);
                dw *= 1f + sat * 3f;
            }

            bwSum += bw; bwB += bw * b; bwG += bw * g; bwR += bw * r;
            dwSum += dw; dwB += dw * b; dwG += dw * g; dwR += dw * r;
        }

        int baseIndex = y * 8;
        rowSums[baseIndex] = bwSum;
        rowSums[baseIndex + 1] = bwB;
        rowSums[baseIndex + 2] = bwG;
        rowSums[baseIndex + 3] = bwR;
        rowSums[baseIndex + 4] = dwSum;
        rowSums[baseIndex + 5] = dwB;
        rowSums[baseIndex + 6] = dwG;
        rowSums[baseIndex + 7] = dwR;
    }

    private static float Smoothstep(float x, float edge0, float edge1)
    {
        float t = Hlsl.Saturate((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    private static float Saturation(float r, float g, float b)
    {
        float rn = r / 255f, gn = g / 255f, bn = b / 255f;
        float max = Hlsl.Max(rn, Hlsl.Max(gn, bn));
        float min = Hlsl.Min(rn, Hlsl.Min(gn, bn));
        float delta = max - min;
        if (delta < 1e-6f)
        {
            return 0f;
        }
        float l = (max + min) / 2f;
        return l < 0.5f ? delta / (max + min) : delta / (2f - max - min);
    }
}

/// <summary>ApplyToneGradient の per-pixel スクリーンブレンドを再現。両端色は
/// スカラーで受け取る。(dirX,dirY) は「明」の方向を指す(dark ではない)。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ToneGradientApplyShader(
    IReadWriteNormalizedTexture2D<float4> texture,
    float dirX, float dirY, float cx, float cy, float maxExtent,
    float strength,
    float brightB, float brightG, float brightR,
    float darkB, float darkG, float darkR) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        float dx = pos.X - cx, dy = pos.Y - cy;
        float proj = dx * dirX + dy * dirY;
        float t = Hlsl.Saturate((proj + maxExtent) / (2f * maxExtent));
        t = Hlsl.Pow(t, 0.5f); // ToneGradientDarkBias

        // t≒1 = dot の方向。明/暗を入れ替えて dot が明を指すようにしてある。
        float gB = (darkB + (brightB - darkB) * t) * strength;
        float gG = (darkG + (brightG - darkG) * t) * strength;
        float gR = (darkR + (brightR - darkR) * t) * strength;

        float4 px = texture[pos];
        float b = px.B * 255f, g = px.G * 255f, r = px.R * 255f;
        b = 255f - (255f - b) * (255f - gB) / 255f;
        g = 255f - (255f - g) * (255f - gG) / 255f;
        r = 255f - (255f - r) * (255f - gR) / 255f;
        texture[pos] = new float4(Hlsl.Saturate(r / 255f), Hlsl.Saturate(g / 255f), Hlsl.Saturate(b / 255f), px.A);
    }
}
