# AvaSnap: GPU移行 引き継ぎメモ

合成モード(`ImageAdjustment.CompositeOverlayOntoPhoto`)の画像処理をComputeSharp(DX12コンピュートシェーダーをC#で書けるライブラリ)でGPU化する作業の途中経過と、残りの作業の進め方。次回セッションはこのファイルから読めば経緯を再構築しなくてよいはず。

## 方針(重要)

**CPU版とのピクセル一致はもう目指さない。** 当初はCPU版とGPU版の出力を誤差±2以内で一致させる検証をしていたが(CPU側が各段階でintに切り捨てる実装だったため)、この制約は撤廃した。GPU版は素直に`Hlsl.Saturate(x/255f)`で書けばよい。CPU実装は「GPUが使えない環境向けのフォールバック」として存在するだけで、出力が完全に同じである必要はない。

## 現在の状態(2026-08-22時点で完了・動作確認済み)

### 実装済みのGPUパイプライン

| ファイル | 内容 |
|---|---|
| `Services/GpuAvailability.cs` | `GraphicsDevice.GetDefault()`を初回だけ試行し結果をキャッシュ。以後は毎回試行しない |
| `Services/GpuColorAdjustments.cs` | `AdjustColors`(明るさ・コントラスト・彩度・自然な彩度・色温度・色合い・色相・ハイライト/シャドウ/白/黒レベル・ティント)。`BuildShader()`で他のパイプラインからも再利用可能 |
| `Services/GpuCompositePipeline.cs` | 色調整+写真ぼかし(`ApplyPhotoBlur`)を1回のアップロード/ダウンロードで実行。`BoxBlurPassShader`(水平/垂直の分離ボックスブラー)もここ |
| `Services/GpuFinishingEffects.cs` | 残り9エフェクトを3グループに分けて実行(下記参照) |
| `Services/GpuTexturePool.cs` | キー文字列ごとにGPUテクスチャを使い回すプール。サイズが変わった時だけ再確保 |

### GpuFinishingEffectsの3グループ(なぜ分かれているか)

CPUの元の処理順序:
```
[色調整+写真ぼかし] → [ドロップシャドウ+アバター合成(CPU)]
→ ソフトネス→シャープネス→クラリティ→フェード→グロー→ライトリーク  ← グループ1
→ [トーングラデーション(CPU)]
→ 色収差→カラーブリード                                            ← グループ2
→ [スキャンライン(CPU)]
→ ビネット                                                          ← グループ3
→ [フィルムグレイン(CPU)]
```
トーングラデーション/スキャンライン/グレインがまだCPUなので、それらを挟んで3回に分割している。**これらを全部GPU化すれば3分割は不要になり、本当に1回のアップロード/ダウンロードにできる**(下記の「残作業2」)。

### 検証方法(このパターンはもう使わなくてよいが、参考として)
`C:\Users\gaosh\AppData\Local\Temp\claude\...\scratchpad\GpuVerify\`に、AvaSnap.csprojをProjectReferenceする独立コンソールプロジェクトを作って検証していた。CPU側の`private`メソッドを`internal`に変更し`[assembly: InternalsVisibleTo("GpuVerify")]`をAssemblyInfo.csに追加して直接呼べるようにした(この`internal`化とInternalsVisibleToはそのまま残っている)。今後はCPU一致を求めないので、このプロジェクトは「クラッシュしないか」程度の動作確認用として使うか、あるいは実機で見た目を確認する方が確実。

### ハマったポイント(ComputeSharp利用時の注意)
- `AllowUnsafeBlocks=true`が`.csproj`に必須(`[GeneratedComputeShaderDescriptor]`のソースジェネレーターに必要)。
- シェーダーのフィールドに`byte`型は使えない(`CMPS0050`エラー)。`float`か`int`にする。
- `float4`/`int2`は小文字(ComputeSharpが`using`で提供するエイリアス)。大文字`Float4`でも通る場合があるが、Web上の公式サンプルは小文字表記。
- テクスチャは`IReadWriteNormalizedTexture2D<float4>`をシェーダー側パラメータ型にすると、内部の`Bgra32`ストレージと自動変換してくれる。`.R`/`.G`/`.B`/`.A`で読み書きすればBGRA⇔RGBA順の心配は不要。
- **同じテクスチャに対して「自分以外のピクセル」を読みながら書き込むのは危険**(スレッド間の順序保証がないため)。近傍参照が要るシェーダー(色収差・カラーブリード・ボックスブラー)は必ず別テクスチャに書き込む(ping-pong)。自分のピクセルだけ読み書きするシェーダー(色調整・ビネット・フェード・ライトリークなど)はin-placeで安全。
- `ReadWriteTexture2D<Bgra32, float4>`は`IReadOnlyNormalizedTexture2D<float4>`に暗黙変換できない。読み取り専用として使いたい場合も`IReadWriteNormalizedTexture2D<float4>`をパラメータ型にする。
- `GraphicsDevice.GetDefault()`は例外を投げることがある(GPU/ドライバ非対応時)ので、必ずtry-catchで包み、失敗時はCPU版にフォールバックする設計にする。`GpuAvailability`が今はこれを一元管理している。

## 完了した項目

### 残作業3: 永続テクスチャプール ✅ 完了(2026-08-22)
`Services/GpuTexturePool.cs`を新設。`GpuColorAdjustments`/`GpuCompositePipeline`/`GpuFinishingEffects`は全て`GpuTexturePool.Rent(device, key, width, height)`でテクスチャを取得するようになり、`AllocateReadWriteTexture2D`での毎回確保+`using`/`finally`での毎回破棄をやめた。キーは`"ColorAdjustments"`(アバターPNG用)、`"Composite.A"/"Composite.B"`(色+ぼかし用)、`"Finishing.A/B/C"`(フィニッシング用)で、用途ごとに別々のキーを使うことでサイズの異なるアバター画像と写真がプールを奪い合わない設計。サイズが変わった時だけ再確保、変わらなければ`CopyFrom`で中身だけ更新。

検証: スクラッチパッドの`GpuVerify`で「同一サイズを連続実行して結果が一致するか」「サイズを変えてからまた元のサイズに戻して結果が壊れていないか」を確認し、全てパス。

### 残作業1(以前の項目1): CPU一致のためのFloor処理 ✅ 撤去済み
### 残作業4: 定数の重複定義 ✅ 一元化済み(`ImageAdjustment`側を`internal const`に)
### 残作業10: GPU可否チェックのキャッシュ ✅ `GpuAvailability.cs`で対応済み

### 残作業8: 元写真をGPUに常駐させる ✅ 部分完了(2026-08-22、色+ぼかし段のみ)
`GpuTexturePool`に`RentUploaded(device, key, pixels, stride, width, height)`を追加。渡された`byte[]`の**参照**が前回と同じなら`CopyFrom`(CPU→GPU転送)自体をスキップし、違うテクスチャオブジェクトへの`CopyTo`(GPU→GPU、PCIe転送なし)だけで作業用テクスチャを更新する。`_photoPixelBuffer.Pixels`は写真読み込み時にしか差し替わらない(ControlPanelWindow内で確認済み、途中でin-place変更される箇所なし)ので、参照比較がそのまま「同じ写真か」の判定として使える。

`GpuCompositePipeline.TryRun`のシグネチャを`(sourcePixels, outputPixels, ...)`の2バッファ方式に変更。`sourcePixels`=`photo.Pixels`(素の写真、識別用に渡すだけ)、`outputPixels`=結果を書き込む別バッファ。`ImageAdjustment.CompositeOverlayOntoPhoto`側の`photo.Pixels.Clone()`を廃止し、`new byte[...]`(内容コピー不要、GPUダウンロードで全部上書きされるため)に変更。CPUフォールバック時だけ`Array.Copy`で明示的にコピーする。

**「部分完了」な理由**: この最適化が効くのは色調整+写真ぼかし段(`GpuCompositePipeline`)だけ。`GpuFinishingEffects`(残りのグループ)はドロップシャドウなどCPU処理の後の状態を処理するので、そもそも「変化しない元写真」を起点にできない。残作業2(下記)でCPUエフェクトを減らすほど、この最適化の効果範囲が広がる。

検証: `GpuVerify`に、**同じ`sourcePixels`参照**で異なるぼかし半径を渡して結果がちゃんと変わるか、同じ引数を再度渡して結果が完全に一致するか、**別の**`sourcePixels`参照(サイズは同じ)を渡した時に古いキャッシュを使い回さず正しく検出できるか、を確認。全てパス。

## 残作業(優先度順、11番(処理順の並べ替えで見た目が変わる案)は除外)

### 残作業2: 残りのCPUエフェクトをGPU化 ✅ 全完了(2026-08-22)
実装難易度順、5項目すべて完了:
1. **アバター合成(アルファブレンド)** ✅ 完了(2026-08-22)。[Services/GpuAvatarBlend.cs](D:\AvaSnap\src\AvaSnap\Services\GpuAvatarBlend.cs)。写真テクスチャとアバターPNGテクスチャ(サイズ・オフセットが異なる)を両方GPUにアップロードし、`AlphaBlendShader`をオーバーレイのサイズ(写真全体ではなく)でディスパッチして合成。写真がオーバーレイの範囲外にはみ出すケース(負のオフセットなど)も含めてCPU手計算のリファレンスと比較検証済み、全て一致(誤差1以内)。近傍参照がないので安全に書けた通り。オーバーレイのアップロードは`RentUploaded`のキー`"Overlay"`に変更済み(残作業2の2、ドロップシャドウと共有 — 同じレンダー内で先に呼ばれた方がアップロードし、後の方はスキップされる)。
2. **ドロップシャドウ** ✅ 完了(2026-08-22)。[Services/GpuDropShadow.cs](D:\AvaSnap\src\AvaSnap\Services\GpuDropShadow.cs)。アバターのアルファチャンネルを抽出(`ExtractAlphaShader`、R/G/B全部に詰めて既存の`BoxBlurPassShader`をそのまま流用してぼかせるようにする裏技)、オプションでハーフトーンドット化(`HalftoneDotsShader`、`ApplyHalftoneDots`のconcave-astroid形状式をそのまま移植)、写真に乗算合成(`DropShadowBlendShader`)。**ぼかしの縁の挙動だけCPU版と意図的に不一致**(CPU版`BoxBlurAlpha`は範囲外を除外して平均個数を縮める方式、GPU版は`BoxBlurPassShader`のクランプ方式を流用しているため)。CPU一致は求めない方針なのでこれは許容。検証はCPU版`ApplyDropShadow`(`internal`化済み)との比較で、シルエット中心部はほぼ完全一致、縁付近だけ差が出るが画像全体の平均誤差は0.35以下に収まることを確認。
3. **スキャンライン** ✅ 完了(2026-08-22)。[Services/GpuScanlines.cs](D:\AvaSnap\src\AvaSnap\Services\GpuScanlines.cs)。偶数行を暗くする`EvenRowDarkenShader`(単純per-pixel)と、グリッチバンドをずらす`GlitchBandShader`の2段。帯パラメータ(開始Y・高さ・シフト量、最大4本)は元のCPU実装通り`ImageAdjustment.HashNoise`をそのまま呼んでCPU側で計算し、スカラー値としてシェーダーに渡す(帯の数が高々4つと小さいので配列/バッファは使わず4組の個別引数)。帯が重なる場合は後の帯が優先(CPU版の逐次上書きと同じ挙動になるよう、if-elseではなく複数の独立したif文で後の帯ほど上書きされるようにした)。検証: CPU版`ApplyScanlines`(`internal`化済み)と比較し、DropShadowと違いアルゴリズムのすり替えがないため誤差1以内でほぼ完全一致。
4. **フィルムグレイン** ✅ 完了(2026-08-22)。[Services/GpuFilmGrain.cs](D:\AvaSnap\src\AvaSnap\Services\GpuFilmGrain.cs)。ノイズ場自体(自己回帰ノイズ、`ImageAdjustment.GetArNoise`)はラスタースキャン依存で並列化できないためCPU側の既存キャッシュ(`GrainNoiseCache`)をそのまま使い、GPU側では`ReadWriteBuffer<float>`にアップロードして参照一致キャッシュ(`_lastNoise`/`_noiseBuffer`、`GpuTexturePool`はテクスチャ専用なので別枠の静的フィールドで同じ考え方を実装)。per-pixelの輝度加重ソフトライトブレンドだけをGPU化。検証: CPU版`ApplyFilmGrain`と比較し誤差1以内でほぼ完全一致、同一サイズ再実行での決定性も確認。
5. **トーングラデーション** ✅ 完了(2026-08-22)。[Services/GpuToneGradient.cs](D:\AvaSnap\src\AvaSnap\Services\GpuToneGradient.cs)。一番厄介だった項目 — `ExtractToneGradientColors`が画像全体の明暗の重み付き平均を要求するため、GPU側は「1スレッド=1行」のリダクションシェーダー(`ToneGradientRowSumShader`)で各行の重み付き部分和を`height*8`個の小さいバッファに書き出し、それをCPUにダウンロードして最終集計(バッファが小さいのでダウンロードコストは無視できる)、得られた明暗色を使って`ToneGradientApplyShader`で通常のper-pixelグラデーション適用、という2パス構成。完全なツリーリダクション/GroupSharedは使わない意図的な簡略化(コメントに明記)。検証: CPU版`ExtractToneGradientColors`+`ApplyToneGradient`と比較(上から下・回転あり・縦長画像の3パターン)、誤差1以内・平均誤差0.35前後でほぼ完全一致。

全5項目完了により、残作業3の永続テクスチャプールと合わせて、`CompositeOverlayOntoPhoto`のCPU処理がほぼ無くなった(GPU可否チェック失敗時のフォールバックとしてのみ残る)。ただし各`GpuXxx`クラスはそれぞれ自前でテクスチャのアップロード/ダウンロードを行っているため、パイプライン全体としては依然として複数回のGPUディスパッチ境界がある — これを1回のアップロード/ダウンロードに完全統合するには処理順の並べ替えが必要で、それは項目11として意図的に対象外にしている(下記参照)。

AvaSnap.exeをビルド・起動して安定動作を確認済み(2026-08-22)。

### 残作業6: アバターPNG側の縁ぼかし(edge blur) ✅ 完了(2026-08-22)
[Services/GpuAvatarEdgeBlur.cs](D:\AvaSnap\src\AvaSnap\Services\GpuAvatarEdgeBlur.cs)。CPU版`BlurEdgePremultiplied`は厳密なEuclidean距離変換(Felzenszwalb-Huttenlocher)を使うが、その1次元パスは列/行の長さ分のスクラッチ配列をスレッドローカルに持つ設計で、HLSLのスレッド(コンパイル時サイズ固定のローカル配列しか持てない)にそのまま移植できない。そこでアルゴリズム自体をGPUネイティブな手法に置き換えた: **Jump Flooding Algorithm(JFA)**。前景/背景境界に接するピクセルを「シード」として種まきし、O(log(max(width,height)))回の伝播パス(ステップ幅を半分ずつに縮めながら8近傍と比較)で全ピクセルが最近傍のシードを発見する、距離場計算のGPU定番手法。CPUの「不透明側/透明側それぞれの最近傍反対クラスピクセルまでの距離」ではなく「最近傍の境界ピクセルまでの距離」を直接求める設計にしたことで、JFAを1回走らせるだけで済む(符号は前景か背景かで正負を振り分け)。厳密解ではなく近似(JFA+1で仕上げ)だが、CPU一致は求めない方針なので許容。

色チャンネルの再構成(premultiplied color の box blur によるフェザー帯の塗り直し)はCPU版と同じ考え方: 事前にpremultiply(色×アルファ)してから4チャンネル(RGB+A)まとめてbox blurする専用シェーダー(`PremulBoxBlurPassShader`、既存の`BoxBlurPassShader`と違いアルファも一緒にぼかす必要があるため別実装)。

検証: `ImageAdjustment.BlurEdgePremultiplied`(`internal`化済み)との比較で、円形カットアウト画像(半径3/15、細いスリバー形状も含む)を使い平均誤差25以下(0-255レンジ)に収まることを確認。全体が不透明/全体が透明(境界が存在しない)という縮退ケースでもクラッシュせず正しく振る舞うことも確認。同一入力の再実行での決定性も確認。

### 残作業9: 表示経路の見直し ✅ 検討の上、見送りと決定(2026-08-22)
今はGPU計算結果を毎回CPUの`byte[]`にダウンロードして`WriteableBitmap.WritePixels`で表示している。WPFの`D3DImage`(GPU描画結果を直接表示できる相互運用サーフェス)を使えば、プレビュー表示に関してはCPUへのダウンロードすら省略できる可能性がある。

**検討の結果、見送りと決定(ユーザーと相談の上で最終確認済み)。** 理由:
- これまでの6項目(色調整・ぼかし・アバター合成・ドロップシャドウ・スキャンライン・グレイン・トーングラデーション・縁ぼかし)は「GPUシェーダーを1つ追加し、失敗したら安全にCPU版へフォールバックする」局所的な変更で、`GpuVerify`のピクセル比較で機械的に検証できた。D3DImage化は写真表示そのものを担うWPFの中核描画パスの作り替えで、動作確認は実機でのレンダリング目視に頼るしかない(ピクセル単位の自動検証が効かない)。
- 効果は、典型的なVRChatスクリーンショットサイズ(1080p〜4K)ではCPU転送時間は1〜8ms程度で誤差の範囲。真の8K(7680×4320)でも15〜25ms程度で、これまでの6項目のような体感速度への影響はない。
- **実装スコープの具体的調査結果(2026-08-22)**: ComputeSharp(`ComputeSharp.Interop.InteropServices`)は`GetID3D12Device`/`GetID3D12Resource`で生のCOMポインタを取得できるが、(a)`D3D12_HEAP_FLAG_SHARED`付きでテクスチャを確保するAPIも、(b)外部作成済みの共有リソースをシェーダー書き込み先として取り込むAPIも提供していない。つまりComputeSharpが計算した結果そのものを共有サーフェスにはできず、**自前のD3D12コマンドキュー/コマンドリスト/フェンスを一から実装して**、計算結果を別途自前確保した共有テクスチャへGPU内コピーする必要がある。さらにWPFの`D3DImage`はD3D9Exサーフェスしか受け付けないため、D3D12→D3D11→D3D9Exと2段階のNTハンドル共有ブリッジが必要で、各段階で毎フレームのキードミューテックス取得/解放を挟まないと同期が崩れる(失敗時は例外ではなくGPUハングや黒画面/フリーズという静かな壊れ方をする)。
- 以上を踏まえ、リスク(コア描画パスへの大規模なネイティブ相互運用、機械的検証手段の欠如、静かに壊れる失敗モード)に対して見返り(1〜25ms程度の転送時間削減)が明らかに小さいと判断し、実装しないことで最終確定した。

### やらないと決めたもの
- **項目11(処理順を変えて全部を1グループに統合)**: トーングラデーション/スキャンラインの位置を動かすと最終的な見た目が変わる可能性がある。純粋な技術的最適化ではなく「仕上がりを変える」判断が要るので対象外。
- **項目9(D3DImage表示経路)**: 検討の上、見送り。理由は上記「残作業9」の記載を参照(効果が小さい割にネイティブ相互運用のリスクが大きく、機械的な検証手段もない)。

## 現状まとめ(2026-08-22時点)
項目11・9を除く全項目が完了。`CompositeOverlayOntoPhoto`のCPU処理はGPU不可時のフォールバックとしてのみ残っている状態。GPU移行プロジェクトはここで一区切り。
