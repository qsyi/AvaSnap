using System.Runtime.InteropServices;
using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>ImageAdjustment.BlurPng(アバター切り抜きのフチのフェザー)の GPU 版。
///
/// CPU 版(BlurEdgePremultiplied)は各画素の前景/背景境界までの符号付き距離を厳密な
/// ユークリッド距離変換で求めるが、その 1D パスは列/行長ぶんの可変長スクラッチが
/// 要り GPU スレッドに載らない(HLSL のローカルはコンパイル時サイズが必要)。そこで
/// GPU 定番の Jump Flooding Algorithm(JFA)を使う: 境界画素を自身の seed として記録し、
/// オフセットを半減させながら O(log(max(w,h))) 回の並列伝播で各画素が最近傍 seed を
/// 見つける。厳密解より数画素ずれることがあるが、CPU 一致は求めない方針なので許容。
///
/// 全体構成は BlurEdgePremultiplied と同じ: 前乗算した色+アルファをボックスブラーし、
/// 最終アルファは境界距離のイージングで出す(ブラー結果そのものではない)。</summary>
public static class GpuAvatarEdgeBlur
{
    // ImageAdjustment.EdgeBlurForegroundAlphaThreshold のコピー。ComputeSharp の
    // シェーダ本体は外部定数を参照できず、コンストラクタ渡しの値しか使えないため。
    private const float ForegroundAlphaThreshold = ImageAdjustment.EdgeBlurForegroundAlphaThreshold / 255f;

    /// <summary>DX12 対応 GPU が無ければ false(<paramref name="pixels"/> は不変)。
    /// 呼び出し側は CPU の BlurEdgePremultiplied へフォールバックする。</summary>
    public static bool TryApply(byte[] pixels, int stride, int width, int height, double edgeBlurRadius)
    {
        if (edgeBlurRadius <= 0) return true;
        int radius = (int)Math.Round(edgeBlurRadius);
        if (radius <= 0) return true;
        if (stride != width * 4 || pixels.Length < stride * height) return false;
        if (GpuAvailability.Device is not { } device) return false;

        try
        {
            Span<Bgra32> pixelSpan = MemoryMarshal.Cast<byte, Bgra32>(pixels.AsSpan(0, stride * height));
            ReadWriteTexture2D<Bgra32, float4> source = GpuTexturePool.Rent(device, "EdgeBlur.Source", width, height);
            source.CopyFrom(pixelSpan);

            ReadWriteTexture2D<Bgra32, float4> premulA = GpuTexturePool.Rent(device, "EdgeBlur.PremulA", width, height);
            ReadWriteTexture2D<Bgra32, float4> premulB = GpuTexturePool.Rent(device, "EdgeBlur.PremulB", width, height);
            device.For(width, height, new PremultiplyShader(source, premulA));
            device.For(width, height, new PremulBoxBlurPassShader(premulA, premulB, width, height, radius, horizontal: true));
            device.For(width, height, new PremulBoxBlurPassShader(premulB, premulA, width, height, radius, horizontal: false));
            // premulA はこの時点でぼかし済みの乗算済みカラー + アルファを保持する。

            int count = width * height;
            using ReadWriteBuffer<float> seedX0 = device.AllocateReadWriteBuffer<float>(count);
            using ReadWriteBuffer<float> seedY0 = device.AllocateReadWriteBuffer<float>(count);
            using ReadWriteBuffer<float> seedX1 = device.AllocateReadWriteBuffer<float>(count);
            using ReadWriteBuffer<float> seedY1 = device.AllocateReadWriteBuffer<float>(count);

            device.For(width, height, new EdgeBlurInitSeedShader(source, seedX0, seedY0, width, height, ForegroundAlphaThreshold));

            ReadWriteBuffer<float> curX = seedX0, curY = seedY0, nextX = seedX1, nextY = seedY1;
            int maxDim = Math.Max(width, height);
            int step = 1;
            while (step * 2 < maxDim) step *= 2;
            for (int s = step; s >= 1; s /= 2)
            {
                device.For(width, height, new EdgeBlurJfaStepShader(curX, curY, nextX, nextY, width, height, s));
                (curX, nextX) = (nextX, curX);
                (curY, nextY) = (nextY, curY);
            }
            // 追加の step=1 パス(JFA+1)。基本アルゴリズムが残す off-by-one を拾う定番の改善。
            device.For(width, height, new EdgeBlurJfaStepShader(curX, curY, nextX, nextY, width, height, 1));
            (curX, nextX) = (nextX, curX);
            (curY, nextY) = (nextY, curY);

            device.For(width, height, new EdgeBlurComposeShader(source, premulA, curX, curY, width, radius, ForegroundAlphaThreshold));

            source.CopyTo(pixelSpan);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>色にアルファを前乗算する(後段のボックスブラーで完全透明画素のゴミ RGB が
/// 隣の不透明画素へ滲まないように)。アルファは第4チャンネルとしてそのまま持ち越し、
/// <see cref="PremulBoxBlurPassShader"/> が4チャンネル一括でブラーできる状態にする。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct PremultiplyShader(
    IReadWriteNormalizedTexture2D<float4> source,
    IReadWriteNormalizedTexture2D<float4> destination) : IComputeShader
{
    public void Execute()
    {
        float4 px = source[ThreadIds.XY];
        destination[ThreadIds.XY] = new float4(px.R * px.A, px.G * px.A, px.B * px.A, px.A);
    }
}

/// <summary>4チャンネル全部(アルファ含む。alpha を素通しする
/// GpuCompositePipeline.BoxBlurPassShader とは違う)を分離ボックスブラー。
/// ブラー後の色を前乗算解除する除数として、ブラー済みアルファが要るため。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct PremulBoxBlurPassShader(
    IReadWriteNormalizedTexture2D<float4> source,
    IReadWriteNormalizedTexture2D<float4> destination,
    int width, int height, int radius, bool horizontal) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X, y = ThreadIds.Y;
        float sumB = 0f, sumG = 0f, sumR = 0f, sumA = 0f;
        int windowSize = radius * 2 + 1;

        if (horizontal)
        {
            for (int k = -radius; k <= radius; k++)
            {
                int xx = Hlsl.Clamp(x + k, 0, width - 1);
                float4 px = source[new int2(xx, y)];
                sumB += px.B; sumG += px.G; sumR += px.R; sumA += px.A;
            }
        }
        else
        {
            for (int k = -radius; k <= radius; k++)
            {
                int yy = Hlsl.Clamp(y + k, 0, height - 1);
                float4 px = source[new int2(x, yy)];
                sumB += px.B; sumG += px.G; sumR += px.R; sumA += px.A;
            }
        }

        destination[new int2(x, y)] = new float4(sumR / windowSize, sumG / windowSize, sumB / windowSize, sumA / windowSize);
    }
}

/// <summary>JFA の seed 付け: 前景/背景境界(4近傍のどれかと前景/背景クラスが違う)
/// の画素は自身の座標を seed に、それ以外は無効 (-1,-1) にして伝播パスで埋めさせる。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct EdgeBlurInitSeedShader(
    IReadWriteNormalizedTexture2D<float4> source,
    ReadWriteBuffer<float> seedX, ReadWriteBuffer<float> seedY,
    int width, int height, float foregroundAlphaThreshold) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int idx = pos.Y * width + pos.X;
        bool isFg = source[pos].A >= foregroundAlphaThreshold;

        bool leftDiff = pos.X > 0 && (source[new int2(pos.X - 1, pos.Y)].A >= foregroundAlphaThreshold) != isFg;
        bool rightDiff = pos.X < width - 1 && (source[new int2(pos.X + 1, pos.Y)].A >= foregroundAlphaThreshold) != isFg;
        bool upDiff = pos.Y > 0 && (source[new int2(pos.X, pos.Y - 1)].A >= foregroundAlphaThreshold) != isFg;
        bool downDiff = pos.Y < height - 1 && (source[new int2(pos.X, pos.Y + 1)].A >= foregroundAlphaThreshold) != isFg;
        bool isBoundary = leftDiff || rightDiff || upDiff || downDiff;

        if (isBoundary)
        {
            seedX[idx] = (float)pos.X;
            seedY[idx] = (float)pos.Y;
        }
        else
        {
            seedX[idx] = -1f;
            seedY[idx] = -1f;
        }
    }
}

/// <summary>JFA の伝播1パス: 各画素が現在の最近傍 seed 候補と、<paramref name="stepSize"/>
/// ずらした8近傍の seed を比べ、一番近いものを採る。step を半減させながら回す。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct EdgeBlurJfaStepShader(
    ReadWriteBuffer<float> seedXIn, ReadWriteBuffer<float> seedYIn,
    ReadWriteBuffer<float> seedXOut, ReadWriteBuffer<float> seedYOut,
    int width, int height, int stepSize) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int idx = pos.Y * width + pos.X;

        float bestX = seedXIn[idx];
        float bestY = seedYIn[idx];
        float bestDist = bestX >= 0f ? Distance2(pos, bestX, bestY) : 1e18f;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = pos.X + dx * stepSize;
                int ny = pos.Y + dy * stepSize;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;

                int nidx = ny * width + nx;
                float candX = seedXIn[nidx];
                if (candX < 0f) continue;
                float candY = seedYIn[nidx];
                float d = Distance2(pos, candX, candY);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestX = candX;
                    bestY = candY;
                }
            }
        }

        seedXOut[idx] = bestX;
        seedYOut[idx] = bestY;
    }

    private static float Distance2(int2 pos, float sx, float sy)
    {
        float dx = pos.X - sx;
        float dy = pos.Y - sy;
        return dx * dx + dy * dy;
    }
}

/// <summary>最終合成: JFA で得た境界距離を BlurEdgePremultiplied と同じイージング
/// アルファ減衰にし、フェザー帯の色をブラー済み前乗算テクスチャから再構成する
/// (距離が EDT ではなく JFA な点以外は CPU 版の最終ループと同じ)。各入力から自分の
/// 画素しか読まないので <paramref name="source"/> へ in-place で書いて安全。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct EdgeBlurComposeShader(
    IReadWriteNormalizedTexture2D<float4> source,
    IReadWriteNormalizedTexture2D<float4> blurredPremul,
    ReadWriteBuffer<float> seedX, ReadWriteBuffer<float> seedY,
    int width, int radius, float foregroundAlphaThreshold) : IComputeShader
{
    public void Execute()
    {
        int2 pos = ThreadIds.XY;
        int idx = pos.Y * width + pos.X;

        float sx = seedX[idx];
        float dist;
        if (sx < 0f)
        {
            dist = 1e9f;
        }
        else
        {
            float dx = pos.X - sx;
            float dy = pos.Y - seedY[idx];
            dist = Hlsl.Sqrt(dx * dx + dy * dy);
        }

        bool isFg = source[pos].A >= foregroundAlphaThreshold;
        float signedDist = isFg ? dist : -dist;

        float t = Hlsl.Saturate((signedDist + radius) / (2f * radius));
        float eased = t * t * (3f - 2f * t);

        if (eased >= 0.999f)
        {
            // 半径内に境界なし = 完全不透明。色はそのまま。
            return;
        }

        if (eased <= 0.001f)
        {
            source[pos] = new float4(0f, 0f, 0f, 0f);
            return;
        }

        float4 blurred = blurredPremul[pos];
        float aBox = Hlsl.Max(blurred.A, 1f / 255f);
        float outR = Hlsl.Saturate(blurred.R / aBox);
        float outG = Hlsl.Saturate(blurred.G / aBox);
        float outB = Hlsl.Saturate(blurred.B / aBox);
        source[pos] = new float4(outR, outG, outB, eased);
    }
}
