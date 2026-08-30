using ComputeSharp;

namespace AvaSnap.Services;

/// <summary>DX12 対応 GPU が使えるかを一度だけ判定してキャッシュする。各 Gpu* が
/// レンダーのたび(スライダードラッグの各 tick 含む) GraphicsDevice.GetDefault() を
/// 呼び直して例外を握りつぶす、のを避けるため。セッション中に状況は変わらないので
/// 結果はアプリ終了まで信頼する。</summary>
public static class GpuAvailability
{
    private static bool? _isAvailable;
    private static GraphicsDevice? _device;

    /// <summary>既定の GPU デバイス。無ければ null なので、呼び出し側は
    /// テクスチャ確保を試みず即 CPU フォールバックへ抜けること。</summary>
    public static GraphicsDevice? Device
    {
        get
        {
            if (_isAvailable is null)
            {
                try
                {
                    _device = GraphicsDevice.GetDefault();
                    _isAvailable = true;
                }
                catch (Exception)
                {
                    _isAvailable = false;
                }
            }
            return _device;
        }
    }
}
