using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>色調補正ステージと写真ぼかしステージを1つの GPU パイプラインで走らせる:
/// アップロード1回 → 必要なパスだけ GPU 常駐テクスチャ上で連続ディスパッチ →
/// 最後にダウンロード1回。この2つは CompositeOverlayOntoPhoto で最も重く・最も
/// よく効くので、1回の転送にまとめれば実コストの大半をカバーできる。他の仕上げ
/// エフェクトは別パイプライン(GpuFinishingEffects)。テクスチャ確保は
/// GpuTexturePool がキャッシュする。</summary>
public static class GpuCompositePipeline
{
    /// <summary>DX12 対応 GPU が無ければ false(<paramref name="outputPixels"/> は不変)。
    /// 呼び出し側は CPU の AdjustColors/ApplyPhotoBlur へフォールバックする
    /// (GpuColorAdjustments.TryAdjustColors と同じ契約)。<paramref name="blurRadiusPixels"/>
    /// はスケール済みのピクセル半径で、0 以下ならぼかしパスを丸ごとスキップ。
    ///
    /// <paramref name="sourcePixels"/> と <paramref name="outputPixels"/> は別バッファ。
    /// source は書き換えない pristine な写真バッファ(RentUploaded が参照一致で再
    /// アップロードをスキップできる)、output は結果を書き込む上書き自由なバッファ
    /// (source のコピーで始める必要は無い。最後の GPU ダウンロードで全バイト上書きされる)。</summary>
    public static bool TryRun(byte[] sourcePixels, byte[] outputPixels, int stride, int width, int height,
        ImageAdjustment.ColorAdjustments colorAdj, int blurRadiusPixels)
    {
        bool needsColor = !colorAdj.IsIdentity;
        bool needsBlur = blurRadiusPixels > 0;
        if (stride != width * 4 || sourcePixels.Length < stride * height || outputPixels.Length < stride * height) return false;
        if (!needsColor && !needsBlur)
        {
            // 処理不要でも outputPixels は有効な結果を持つ必要がある(契約は
            // 「outputPixels = 処理後の写真」。output は未初期化=全透明で始まる)。
            // このコピーを忘れると、既定調整のまま読み込んだ写真が透明で合成されていた。
            Array.Copy(sourcePixels, outputPixels, stride * height);
            return true;
        }
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> outputSpan = MemoryMarshal.Cast<byte, Bgra32>(outputPixels.AsSpan(0, stride * height));

            ReadWriteTexture2D<Bgra32, float4> source = GpuTexturePool.RentUploaded(device, "Composite.Source", sourcePixels, stride, width, height);
            ReadWriteTexture2D<Bgra32, float4> textureA = GpuTexturePool.Rent(device, "Composite.A", width, height);
            // GPU 同士の安いコピー。textureA は前回の処理結果(初回は不定)を
            // 持っているので、source のアップロードが省かれた回でも必ず入れ直す。
            source.CopyTo(textureA);

            ApplyToTexture(textureA, device, width, height, colorAdj, blurRadiusPixels);

            textureA.CopyTo(outputSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><see cref="TryRun"/> と同じ色+ぼかしを、既に GPU 上にある
    /// <paramref name="texture"/> へ直接かける(アップロード/ダウンロード無し)。
    /// GpuCompositeChain がエフェクト連鎖の1ステップとして使う。</summary>
    internal static void ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> texture, GraphicsDevice device, int width, int height,
        ImageAdjustment.ColorAdjustments colorAdj, int blurRadiusPixels)
    {
        if (!colorAdj.IsIdentity)
        {
            device.For(width, height, GpuColorAdjustments.BuildShader(texture, colorAdj));
        }

        if (blurRadiusPixels > 0)
        {
            // 近傍参照するので in-place ではなく ping-pong。横パスは texture→scratch、
            // 縦パスは scratch→texture。
            ReadWriteTexture2D<Bgra32, float4> scratch = GpuTexturePool.Rent(device, "Composite.B", width, height);

            device.For(width, height, new BoxBlurPassShader(texture, scratch, width, height, blurRadiusPixels, horizontal: true));
            device.For(width, height, new BoxBlurPassShader(scratch, texture, width, height, blurRadiusPixels, horizontal: false));
        }
    }
}

/// <summary>分離ボックスブラーの1軸(CPU 版は ImageAdjustment.cs の BoxBlur1D。
/// clamp-to-edge、窓サイズ一定除算)。BoxBlur1D のスライディング窓 O(1) ではなく
/// 素朴に 2*radius+1 サンプルを合計する ── 全画素並列なら総当たりでも十分速く
/// (radius は MaxPhotoBlurRadius=40 上限)、シェーダでは正しく書きやすい。
/// アルファは source からそのまま素通し(ApplyPhotoBlur と同じ B/G/R のみ)。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct BoxBlurPassShader(
    IReadWriteNormalizedTexture2D<float4> source,
    IReadWriteNormalizedTexture2D<float4> destination,
    int width, int height, int radius, bool horizontal) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X, y = ThreadIds.Y;
        float sumB = 0f, sumG = 0f, sumR = 0f;
        int windowSize = radius * 2 + 1;

        if (horizontal)
        {
            for (int k = -radius; k <= radius; k++)
            {
                int xx = Hlsl.Clamp(x + k, 0, width - 1);
                float4 px = source[new int2(xx, y)];
                sumB += px.B;
                sumG += px.G;
                sumR += px.R;
            }
        }
        else
        {
            for (int k = -radius; k <= radius; k++)
            {
                int yy = Hlsl.Clamp(y + k, 0, height - 1);
                float4 px = source[new int2(x, yy)];
                sumB += px.B;
                sumG += px.G;
                sumR += px.R;
            }
        }

        float alpha = source[new int2(x, y)].A;
        destination[new int2(x, y)] = new float4(sumR / windowSize, sumG / windowSize, sumB / windowSize, alpha);
    }
}
