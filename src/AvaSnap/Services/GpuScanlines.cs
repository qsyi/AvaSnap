using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>ApplyScanlines の GPU 版。CPU 版と同じ2パス: 偶数行を暗くする →
/// 最大 MaxGlitchBands 本の横ずれグリッチバンドを当てる。バンドのパラメータ
/// (対象行・ずらし量)は HashNoise 数回で安く出せるので CPU で計算し、シェーダへは
/// スカラーで渡す。</summary>
public static class GpuScanlines
{
    /// <summary>DX12 対応 GPU が無ければ false(<paramref name="pixels"/> は不変)。
    /// 呼び出し側は CPU の ApplyScanlines へフォールバックする。</summary>
    public static bool TryApply(byte[] pixels, int stride, int width, int height, double amount, double scale = 1.0)
    {
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));

            ReadWriteTexture2D<Bgra32, float4> texture = GpuTexturePool.Rent(device, "Scanlines.A", width, height);
            texture.CopyFrom(pixelSpan);

            ApplyToTexture(texture, device, width, height, amount, scale);

            texture.CopyTo(pixelSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><see cref="TryApply"/> と同じ処理を、既に GPU 上にある
    /// <paramref name="texture"/> へ直接かける(アップロード/ダウンロード無し)。
    /// GpuCompositeChain から使う。</summary>
    internal static bool ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> texture, GraphicsDevice device, int width, int height, double amount, double scale = 1.0)
    {
        double strength = amount / 100.0;
        float darkenFactor = (float)(1.0 - strength * 0.35);

        int bandCount = Math.Max(1, (int)Math.Round(strength * ImageAdjustment.MaxGlitchBands));
        Span<int> bandStart = stackalloc int[4];
        Span<int> bandEnd = stackalloc int[4];
        Span<int> bandShift = stackalloc int[4];
        for (int band = 0; band < 4; band++)
        {
            if (band >= bandCount)
            {
                bandStart[band] = 0;
                bandEnd[band] = 0;
                bandShift[band] = 0;
                continue;
            }

            int bandY = (int)((ImageAdjustment.HashNoise(band, 0, ImageAdjustment.VhsGlitchSeed) + 1) / 2 * height);
            int bandHeight = 2 + (int)((ImageAdjustment.HashNoise(band, 1, ImageAdjustment.VhsGlitchSeed) + 1) / 2 * 4);
            int shift = (int)Math.Round(ImageAdjustment.HashNoise(band, 2, ImageAdjustment.VhsGlitchSeed) * ImageAdjustment.MaxGlitchShift * strength * scale);

            bandStart[band] = bandY;
            bandEnd[band] = Math.Min(bandY + bandHeight, height);
            bandShift[band] = shift;
        }

        device.For(width, height, new EvenRowDarkenShader(texture, darkenFactor));

        // 暗くした後のスナップショット(CPU 版の順序と同じ)。グリッチバンドシェーダは
        // これを読んで texture へ書き戻す ── ずれた行は隣接 x を読むので GPU 同士の
        // ping-pong にする(CPU 往復ではない)。
        ReadWriteTexture2D<Bgra32, float4> original = GpuTexturePool.Rent(device, "Scanlines.B", width, height);
        texture.CopyTo(original);

        device.For(width, height, new GlitchBandShader(original, texture, width,
            bandStart[0], bandEnd[0], bandShift[0],
            bandStart[1], bandEnd[1], bandShift[1],
            bandStart[2], bandEnd[2], bandShift[2],
            bandStart[3], bandEnd[3], bandShift[3]));

        return true;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct EvenRowDarkenShader(
    IReadWriteNormalizedTexture2D<float4> texture, float darkenFactor) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        if (pos.Y % 2 != 0)
        {
            return;
        }

        float4 px = texture[pos];
        texture[pos] = new float4(px.R * darkenFactor, px.G * darkenFactor, px.B * darkenFactor, px.A);
    }
}

/// <summary>各画素で、その行が最大4本のどのグリッチバンドに入るか調べる。重なった
/// 場合は後(添字が大)のバンドが勝つ(CPU 版の逐次 for と同じ)。どのバンドにも
/// 入らない(または shift=0 の)行は <paramref name="original"/> をそのまま素通し。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct GlitchBandShader(
    IReadWriteNormalizedTexture2D<float4> original,
    IReadWriteNormalizedTexture2D<float4> destination,
    int width,
    int band0Start, int band0End, int band0Shift,
    int band1Start, int band1End, int band1Shift,
    int band2Start, int band2End, int band2Shift,
    int band3Start, int band3End, int band3Shift) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int y = pos.Y;
        int shift = 0;
        if (y >= band0Start && y < band0End) shift = band0Shift;
        if (y >= band1Start && y < band1End) shift = band1Shift;
        if (y >= band2Start && y < band2End) shift = band2Shift;
        if (y >= band3Start && y < band3End) shift = band3Shift;

        int srcX = Hlsl.Clamp(pos.X - shift, 0, width - 1);
        destination[pos] = original[new int2(srcX, y)];
    }
}
