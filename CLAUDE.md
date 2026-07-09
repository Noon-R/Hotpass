# Hotpass — GPU ボトルネック簡易トリアージツール

「フレームのどこに時間が溶けていて、何律速か」のアタリを付ける D3D12 向けトリアージツール。
コンセプト: **"triage first, debug in PIX"** — 深掘りはしない。PIX / Nsight へ導線を張る。

一次資料: `docs/design.md`(設計ドキュメント)、`mock/hotpass-mock.html`(UI 構造リファレンス。ビジュアルは WPF で再設計済みのため構造・挙動の参照専用)。

## 技術スタック

- **WPF / .NET 10**(`net10.0-windows`)、MVVM は CommunityToolkit.Mvvm。WebView は使わない(ユーザ決定)
- ストレージ: SQLite(Microsoft.Data.Sqlite)
- テスト: xUnit
- テーマ: **ダーク既定**(ライトは後日)

## ソリューション構成

| プロジェクト | 役割 |
|---|---|
| `src/Hotpass.Core` | 正規化スキーマ(パス単位モデル)・派生値計算・SQLite ストア。UI 非依存 |
| `src/Hotpass.Adapters.Pix` | pixtool.exe 検出/実行、save-event-list CSV パース、画像抽出(recapture-region + save-resource) |
| `src/Hotpass.App` | WPF UI(キャプチャレール / Single / Compare / Timeline フレームグラフ) |
| `tests/*` | 上記のユニットテスト(アダプタはフィクスチャ CSV 使用) |

## コマンド

```
dotnet build                 # 全体ビルド
dotnet test                  # 全テスト
dotnet run --project src/Hotpass.App   # アプリ起動
```

## ドメイン規約(docs/design.md §3 準拠)

- **取得不能なデータは nullable。UI では「—」表示**。ソースにより可用性が違う(PIX: occupancy+limiter、Nsight: occupancy+SOL)
- 生カウンタは画面に出さない。派生値に丸めてから表示
- `bottleneck_category` は 7 分類 enum: `raster / texture / memory / compute / geometry / sync / unknown`
- **ビジュアルは「Instrument(計測器)」スキン(ユーザ選定・中密度)**: 角丸なし・影なし・ヘアライン・
  アンバー `#EFA13C` は「予算線/選択/アクティブモード/COMPARE役割」だけの1点使い。定義は `src/Hotpass.App/Themes/Dark.xaml`
- カテゴリ色(全 UI で一貫。アンバーと衝突しないよう調整済み):
  - raster `#5B8DEF` / texture `#9D7BE0` / memory `#E14B4B` / compute `#C9A227` / geometry `#2FB98B` / sync `#7E8794` / unknown `#5E6672`
- 数値はモノスペース(Cascadia Mono / Consolas)+タブラーで桁揃え。マイクロラベルは大文字表記
- フレーム予算は 16.6 ms(60fps)
- Compare の規約: **Δ = compare − base、緑▼=速い、赤▲=遅い**。クロスツール比較では limiter / SOL は diff しない

## 外部ツール連携

- pixtool.exe: `C:\Program Files\Microsoft PIX\<バージョン>\pixtool.exe`(最新バージョンディレクトリを検出)
- 独自バイナリ(.wpix)は直接パースしない。公式の口(pixtool CLI の CSV / PNG)のみ使う
- 画像抽出は 2 段構え: `recapture-region` で GID 範囲切り出し → `save-resource` で PNG(パス直後の状態を得るため)
- CSV 列構成は PIX バージョン依存 → パーサは列名ベースで防御的に

## スコープ外(design.md §6)

シェーダデバッグ / リソース中身閲覧 / Vulkan / Nsight 側の画像抽出。Nsight アダプタは未実装(スキーマは受け口あり)。
