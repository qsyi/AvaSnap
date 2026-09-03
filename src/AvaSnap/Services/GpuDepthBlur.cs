using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>被写界深度(深度依存ぼかし)。<see cref="DepthEstimator"/> が出した相対深度
/// マップと、ユーザー指定のピント深度から錯乱円(CoC)を求め、合成結果(背景 + アバター)を
/// 段階ぼかしピラミッドの補間でぼかす ── ゲームエンジン定番の安価な DoF。
///
/// ピント面(CoC≈0)の画素はフル解像度の元テクスチャをそのまま使うので芯は鮮鋭。
/// ぼけた段(L1〜L3)は作業解像度(長辺 <see cref="WorkLongEdge"/>)のボックスブラーで
/// 作り、compose 時に双線形で拡大サンプルする(ぼけは低周波なので粗くて足りる)。</summary>
public static class GpuDepthBlur
{
    private const int WorkLongEdge = 1280;

    /// <summary>GPU 上の合成テクスチャ <paramref name="texture"/> に in-place で適用する。
    /// <paramref name="depth"/> は 0..1(1 = 手前)。<paramref name="focus"/> も 0..1。
    /// <paramref name="strength"/> は 0..100(内部で /20 して CoC の傾き)。
    /// <paramref name="maxRadius"/> は最大ぼかし半径(作業解像度でのピクセル、~2..30)。</summary>
    internal static bool ApplyToTexture(ReadWriteTexture2D<Bgra32, float4> texture, GraphicsDevice device,
        int width, int height, DepthMap depth, double focus, double strength, double maxRadius)
    {
        if (strength <= 0 || maxRadius <= 0 || depth.Data.Length < depth.Width * depth.Height) return true;

        try
        {
            double scale = Math.Max(1, Math.Max(width, height) / (double)WorkLongEdge);
            int ww = Math.Max(8, (int)Math.Round(width / scale));
            int wh = Math.Max(8, (int)Math.Round(height / scale));

            using ReadWriteBuffer<float> depthBuf = device.AllocateReadWriteBuffer(depth.Data);

            var full = GpuTexturePool.Rent(device, "DepthBlur.Full", width, height); // compose の鮮鋭段(L0)
            texture.CopyTo(full);

            var down = GpuTexturePool.Rent(device, "DepthBlur.Down", ww, wh);
            var l1 = GpuTexturePool.Rent(device, "DepthBlur.L1", ww, wh);
            var l2 = GpuTexturePool.Rent(device, "DepthBlur.L2", ww, wh);
            var l3 = GpuTexturePool.Rent(device, "DepthBlur.L3", ww, wh);
            var scratch = GpuTexturePool.Rent(device, "DepthBlur.Scratch", ww, wh);

            device.For(ww, wh, new DepthBlurDownsampleShader(full, down, width, height, ww, wh));

            int r1 = Math.Max(1, (int)Math.Round(maxRadius * 0.30));
            int r2 = Math.Max(1, (int)Math.Round(maxRadius * 0.60));
            int r3 = Math.Max(1, (int)Math.Round(maxRadius));
            BuildLevel(device, down, l1, scratch, ww, wh, r1);
            BuildLevel(device, down, l2, scratch, ww, wh, r2);
            BuildLevel(device, down, l3, scratch, ww, wh, r3);

            float slope = (float)(strength / 20.0);
            device.For(width, height, new DepthBlurComposeShader(
                texture, full, l1, l2, l3, depthBuf,
                depth.Width, depth.Height, width, height, ww, wh,
                (float)Math.Clamp(focus, 0, 1), slope));

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // level = box²(三角)ブラー(down, radius)。2 パス対 ×2 で箱っぽさを軽減。
    private static void BuildLevel(GraphicsDevice device,
        ReadWriteTexture2D<Bgra32, float4> down, ReadWriteTexture2D<Bgra32, float4> level,
        ReadWriteTexture2D<Bgra32, float4> scratch, int w, int h, int radius)
    {
        down.CopyTo(level);
        for (int pass = 0; pass < 2; pass++)
        {
            device.For(w, h, new BoxBlurPassShader(level, scratch, w, h, radius, horizontal: true));
            device.For(w, h, new BoxBlurPassShader(scratch, level, w, h, radius, horizontal: false));
        }
    }
}

/// <summary>フル解像度テクスチャを作業解像度へ双線形縮小(色のみ、α は素通し)。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct DepthBlurDownsampleShader(
    IReadWriteNormalizedTexture2D<float4> src,
    IReadWriteNormalizedTexture2D<float4> dst,
    int srcW, int srcH, int dstW, int dstH) : IComputeShader
{
    public void Execute()
    {
        int2 p = ThreadIds.XY;
        float u = (p.X + 0.5f) / dstW;
        float v = (p.Y + 0.5f) / dstH;
        float fx = u * srcW - 0.5f, fy = v * srcH - 0.5f;
        float flx = Hlsl.Floor(fx), fly = Hlsl.Floor(fy);
        int x0 = Hlsl.Clamp((int)flx, 0, srcW - 1), y0 = Hlsl.Clamp((int)fly, 0, srcH - 1);
        int x1 = Hlsl.Clamp((int)flx + 1, 0, srcW - 1), y1 = Hlsl.Clamp((int)fly + 1, 0, srcH - 1);
        float tx = Hlsl.Saturate(fx - flx), ty = Hlsl.Saturate(fy - fly);
        float4 a = src[new int2(x0, y0)], b = src[new int2(x1, y0)];
        float4 c = src[new int2(x0, y1)], d = src[new int2(x1, y1)];
        dst[p] = Hlsl.Lerp(Hlsl.Lerp(a, b, tx), Hlsl.Lerp(c, d, tx), ty);
    }
}

/// <summary>各フル解像度画素で CoC を求め、[鮮鋭, L1, L2, L3] の4段を補間する。
/// 段0はフル解像度の <paramref name="full"/> をそのまま(芯を鮮鋭に保つ)、
/// 段1〜3は作業解像度のぼかしを双線形で拡大サンプル。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct DepthBlurComposeShader(
    IReadWriteNormalizedTexture2D<float4> outTex,
    IReadWriteNormalizedTexture2D<float4> full,
    IReadWriteNormalizedTexture2D<float4> l1,
    IReadWriteNormalizedTexture2D<float4> l2,
    IReadWriteNormalizedTexture2D<float4> l3,
    ReadWriteBuffer<float> depth,
    int depthW, int depthH, int width, int height, int workW, int workH,
    float focus, float slope) : IComputeShader
{
    public void Execute()
    {
        int2 p = ThreadIds.XY;
        float u = (p.X + 0.5f) / width;
        float v = (p.Y + 0.5f) / height;

        // --- 深度を双線形サンプル(ReadWriteBuffer なので手動)---
        float dfx = Hlsl.Saturate(u) * (depthW - 1);
        float dfy = Hlsl.Saturate(v) * (depthH - 1);
        int dx0 = (int)Hlsl.Floor(dfx), dy0 = (int)Hlsl.Floor(dfy);
        int dx1 = Hlsl.Min(dx0 + 1, depthW - 1), dy1 = Hlsl.Min(dy0 + 1, depthH - 1);
        float dtx = dfx - dx0, dty = dfy - dy0;
        float d = Hlsl.Lerp(
            Hlsl.Lerp(depth[dy0 * depthW + dx0], depth[dy0 * depthW + dx1], dtx),
            Hlsl.Lerp(depth[dy1 * depthW + dx0], depth[dy1 * depthW + dx1], dtx), dty);

        float coc = Hlsl.Saturate(Hlsl.Abs(d - focus) * slope); // 0..1

        float4 sharp = full[p];
        if (coc <= 0.001f) { outTex[p] = sharp; return; }

        // --- 作業解像度のぼかし段を双線形サンプル(l1/l2/l3 共通の座標・重み)---
        float fx = u * workW - 0.5f, fy = v * workH - 0.5f;
        float flx = Hlsl.Floor(fx), fly = Hlsl.Floor(fy);
        int x0 = Hlsl.Clamp((int)flx, 0, workW - 1), y0 = Hlsl.Clamp((int)fly, 0, workH - 1);
        int x1 = Hlsl.Clamp((int)flx + 1, 0, workW - 1), y1 = Hlsl.Clamp((int)fly + 1, 0, workH - 1);
        float tx = Hlsl.Saturate(fx - flx), ty = Hlsl.Saturate(fy - fly);
        int2 q00 = new int2(x0, y0), q10 = new int2(x1, y0), q01 = new int2(x0, y1), q11 = new int2(x1, y1);

        float4 c1 = Hlsl.Lerp(Hlsl.Lerp(l1[q00], l1[q10], tx), Hlsl.Lerp(l1[q01], l1[q11], tx), ty);
        float4 c2 = Hlsl.Lerp(Hlsl.Lerp(l2[q00], l2[q10], tx), Hlsl.Lerp(l2[q01], l2[q11], tx), ty);
        float4 c3 = Hlsl.Lerp(Hlsl.Lerp(l3[q00], l3[q10], tx), Hlsl.Lerp(l3[q01], l3[q11], tx), ty);

        float t = coc * 3f; // [0..3] -> {sharp, c1, c2, c3}
        float4 col;
        if (t < 1f) col = Hlsl.Lerp(sharp, c1, t);
        else if (t < 2f) col = Hlsl.Lerp(c1, c2, t - 1f);
        else col = Hlsl.Lerp(c2, c3, Hlsl.Saturate(t - 2f));

        outTex[p] = new float4(col.R, col.G, col.B, sharp.A);
    }
}
