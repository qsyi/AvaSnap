using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>ApplyFilmGrain の GPU 版。ノイズ場そのもの(GenerateArNoise)は
/// 自己回帰でラスタ順依存なので並列化できないが、(width, height, seed) ごとに
/// 一度だけ計算してキャッシュ(GetArNoise)されるので GPU へは移さない。移すのは
/// per-pixel のブレンド(輝度加重のソフトライト)だけ。ノイズ場の GPU への
/// アップロードは参照一致でキャッシュするので、写真サイズが変わらないレンダー
/// (色スライダードラッグ等)では再アップロードしない。</summary>
public static class GpuFilmGrain
{
    private static double[]? _lastNoise;
    private static ReadWriteBuffer<float>? _noiseBuffer;

    /// <summary>DX12 対応 GPU が無ければ false(<paramref name="pixels"/> は不変)。
    /// 呼び出し側は CPU の ApplyFilmGrain へフォールバックする。</summary>
    public static bool TryApply(byte[] pixels, int stride, int width, int height, double amount)
    {
        if (amount <= 0) return true;
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));
            ReadWriteTexture2D<Bgra32, float4> texture = GpuTexturePool.Rent(device, "Grain", width, height);
            texture.CopyFrom(pixelSpan);

            ApplyToTexture(texture, device, width, height, amount);

            texture.CopyTo(pixelSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><see cref="TryApply"/> と同じ処理を、既に GPU 上にある
    /// <paramref name="texture"/> へ直接かける(それ自体のアップロード/ダウンロード無し。
    /// ノイズバッファのキャッシュは呼び出し側に依らず同じ)。GpuCompositeChain から使う。</summary>
    internal static bool ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> texture, GraphicsDevice device, int width, int height, double amount)
    {
        if (amount <= 0) return true;

        double[] noise = ImageAdjustment.GetArNoise(width, height, ImageAdjustment.GrainSeed);
        float strength = (float)(amount / 100.0 * 0.5);

        if (!ReferenceEquals(_lastNoise, noise) || _noiseBuffer is null || _noiseBuffer.Length != noise.Length)
        {
            var noiseFloats = new float[noise.Length];
            for (int i = 0; i < noise.Length; i++)
            {
                noiseFloats[i] = (float)noise[i];
            }
            _noiseBuffer?.Dispose();
            _noiseBuffer = device.AllocateReadWriteBuffer(noiseFloats);
            _lastNoise = noise;
        }

        device.For(width, height, new FilmGrainShader(texture, _noiseBuffer, width, strength));

        return true;
    }
}

/// <summary>ApplyFilmGrain の per-pixel ブレンドの再現(輝度加重ソフトライト。
/// 理由は ImageAdjustment.cs 側の doc)。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct FilmGrainShader(
    IReadWriteNormalizedTexture2D<float4> texture,
    ReadWriteBuffer<float> noise,
    int width, float strength) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int idx = pos.Y * width + pos.X;

        float4 px = texture[pos];
        float b0 = px.B, g0 = px.G, r0 = px.R;

        float luminance = 0.299f * r0 + 0.587f * g0 + 0.114f * b0;
        float lumaFactor = LuminanceFactor(luminance);
        float blend = Hlsl.Saturate(0.5f + noise[idx] * strength * lumaFactor);

        float b = SoftLight(b0, blend);
        float g = SoftLight(g0, blend);
        float r = SoftLight(r0, blend);

        texture[pos] = new float4(Hlsl.Saturate(r), Hlsl.Saturate(g), Hlsl.Saturate(b), px.A);
    }

    private static float LuminanceFactor(float luminance)
    {
        const float fadeStart = 0.5f;
        const float floorFactor = 0.15f;
        float t = Hlsl.Saturate((luminance - fadeStart) / (1f - fadeStart));
        float eased = t * t * (3f - 2f * t);
        return floorFactor + (1f - floorFactor) * (1f - eased);
    }

    private static float SoftLight(float baseF, float blendF)
    {
        if (blendF < 0.5f)
        {
            return 2f * baseF * blendF + baseF * baseF * (1f - 2f * blendF);
        }
        return Hlsl.Sqrt(baseF) * (2f * blendF - 1f) + 2f * baseF * (1f - blendF);
    }
}
