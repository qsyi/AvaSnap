using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>「肌色ホワイトバランス」: スポイトで拾った色を白の基準にして、合成全体を
/// 除算(乗算の逆)で補正する。sample が (240,200,180) のような肌色なら、その色の
/// ピクセルはほぼ白へ、他の色も同じ倍率でスケールされる ── 定番の「グレー点/白点を
/// Divide レイヤーに置く」ホワイトバランス手法を、肌サンプルで使うもの。
/// <paramref name="amount"/> は素の色との線形補間(0 = 無効)。
/// GpuCompositeChain の stage 3(アバター合成後・仕上げ効果の前)から呼ぶ。</summary>
public static class GpuSkinWhiteBalance
{
    internal static bool ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> texture, GraphicsDevice device,
        int width, int height, double amount, byte sampleR, byte sampleG, byte sampleB)
    {
        if (amount <= 0) return true;
        float strength = (float)Math.Clamp(amount / 100.0, 0, 1);
        // チャンネルが小さいと倍率が発散する。暗すぎるサンプルは白点の基準に
        // 向かないので下限でクランプする(実質の安全網)。
        float multR = 255f / Math.Max(16, (int)sampleR);
        float multG = 255f / Math.Max(16, (int)sampleG);
        float multB = 255f / Math.Max(16, (int)sampleB);

        device.For(width, height, new SkinWhiteBalanceShader(texture, multB, multG, multR, strength));
        return true;
    }
}

/// <summary>per-pixel の除算(乗算)。倍率は CPU 側で 255/サンプル として計算済みで、
/// strength で 1.0(無変化)との間を補間する。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct SkinWhiteBalanceShader(
    IReadWriteNormalizedTexture2D<float4> texture,
    float multB, float multG, float multR, float strength) : IComputeShader
{
    public void Execute()
    {
        int2 p = ThreadIds.XY;
        float4 px = texture[p];
        float fr = Hlsl.Lerp(1f, multR, strength);
        float fg = Hlsl.Lerp(1f, multG, strength);
        float fb = Hlsl.Lerp(1f, multB, strength);
        texture[p] = new float4(
            Hlsl.Saturate(px.R * fr),
            Hlsl.Saturate(px.G * fg),
            Hlsl.Saturate(px.B * fb),
            px.A);
    }
}
