using System.Runtime.CompilerServices;
using System.Windows;

// scratchpad の GpuVerify/GpuProfile ハーネスから Gpu*.cs サービスの internal な
// texture-in/texture-out メソッド(ApplyToTexture、BlendIntoTexture 等)を直接
// 呼べるようにする。GPU エフェクトパイプラインの回帰/等価テストと性能計測用。
// どちらのハーネスプロジェクトもこのリポジトリ/ビルドには含まれない。
[assembly: InternalsVisibleTo("GpuVerify")]
[assembly: InternalsVisibleTo("GpuProfile")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            // テーマ固有のリソースディクショナリの場所
                                                // (ページやアプリのリソースに見つからない場合に使用)
    ResourceDictionaryLocation.SourceAssembly   // 汎用リソースディクショナリの場所
                                                // (ページ・アプリ・テーマ固有のどれにも見つからない場合に使用)
)]
