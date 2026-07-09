# Hotpass — GPU ボトルネック簡易トリアージツール 設計ドキュメント

作成日: 2026-07-09
ステータス: 構想確定・UIモック試作済み → Claude Code + claude design で本実装へ引き継ぎ

---

## 1. 目的とポジショニング

- **一言で**: 「フレームのどこに時間が溶けていて、何律速か」のアタリを付けるための簡易トリアージツール。
- コンセプトフレーズ: **"triage first, debug in PIX"**
- 深掘り(シェーダ・リソース・ピクセルレベルのデバッグ)はやらない。PIX / Nsight へ導線を張って任せる。
- 想定ユーザ: GPU プロファイラの専門家でなくても処理負荷の当たりが付けられること(ユーザフレンドリー重視)。
- 対象 API: **D3D12 中心**。Vulkan はスコープ外。
- 方針: **取得不能なデータは諦める(nullable)**。取れる範囲で degrade gracefully に表示する。

## 2. 前提となる調査結果(データ取得経路)

### 2.1 ツールとレイヤーの整理

| ツール | レイヤー | 主用途 |
|---|---|---|
| PIX GPU Capture (.wpix) | API/フレームレベル | 論理デバッグ(APIコール・リソース中身・シェーダ) |
| PIX Timing Capture | CPU/GPU タイムライン | 性能計測 |
| Nsight Graphics Frame Debugger | API/フレームレベル | PIX GPU Capture と同格 |
| Nsight Graphics GPU Trace | HWユニットレベル | SOL・占有率などNVIDIA HW性能 |
| Nsight Systems (.nsys-rep) | システム全体 | CPU-GPU相関・同期 |

本ツールは **性能トリアージ** が目的なので、主データ源は PIX(Timing/GPU Capture)+ Nsight 系。

### 2.2 ファイルフォーマットの実態と抽出戦略

**基本方針: 独自バイナリを直接パースしない。公式の取り出し口(SQLite / CLI / CSV)に乗る。**

| ソース | 実態 | 抽出経路 |
|---|---|---|
| PIX Timing Capture | 実体は **SQLite**。一部圧縮データあり | PIX 同梱の SQLite 拡張 `PixStorage.dll` をロードし仮想テーブル経由でクエリ。`PixGpuExecution` / `PixCpuExecution` テーブルの begin/end から duration 算出。**一番きれいな口** |
| PIX GPU Capture (.wpix) | 非公開フォーマット。直接パース非現実的(バージョン非互換) | **`pixtool.exe`**(スクリプタブルCLI)。`save-event-list events.csv --counter-groups=D3D*` でイベント別CSV、`recapture-region` でGlobal ID範囲切り出し、`save-resource out.png` でRT/バックバッファをPNG化 |
| Nsight Systems (.nsys-rep) | 実体は **SQLite** | `nsys export --type sqlite` で丸ごとDB化、`nsys stats` でCSVレポート。スクリプト容易 |
| Nsight Graphics GPU Trace | ほぼ閉じている。GUI前提 | **warp データの CSV エクスポートのみ**機械的に取得可。SOL等のリッチな解析はUIロック |

補足:
- pixtool の `save-resource` はデフォルトで「最後まで再生後」の状態を保存。**途中経過(パス直後のGバッファ等)が欲しい場合は `recapture-region` で範囲を切ってから save-resource** の2段構え。
- 画像抽出パイプライン(RT/バックバッファのPNG事前生成)は **PIX 側のみ成立**。Nsight に対等な CLI 口はない → 画像レイヤーは PIX 主導・数値レイヤーはクロスベンダ、という**非対称を前提**に設計する。
- 既存 OSS **`pix-mcp`** が pixtool をラップし CSV を SQLite 化する近い構成を持つ。車輪の再発明を避けるため実装前に確認し、差分(Nsight対応・クロスベンダ正規化・自動診断)に注力する。

### 2.3 共通戦略

各ソースを**アダプタ層**で「共通スキーマの SQLite(または Parquet)」に正規化し、その上に解析・可視化を載せる。

- PIX Timing → SQLite 直クエリ
- PIX GPU Capture → pixtool CSV を自前 DB 化
- Nsight Systems → SQLite 直クエリ
- Nsight GPU Trace → warp CSV のみ取り込み

## 3. データモデル(正規化スキーマ)

### 3.1 設計原則

- 生カウンタは画面に出さない。ベンダごとに名前も意味も違うため、**少数の派生値に丸めてから**表示する。
- 取れない項目は **nullable**。UI では「—」表示。

### 3.2 パス単位スキーマ(コア)

| フィールド | 型 | 可用性 |
|---|---|---|
| `pass_name` | string | 必須(マーカー名) |
| `event_id` | int | 必須(PIXへの導線キー) |
| `gpu_duration_ns` | int | **必須・全ソース共通**(トリアージの主役) |
| `pct_of_frame` | float | duration から算出 |
| `bottleneck_category` | enum | nullable。`compute / memory / texture / raster / geometry / sync-idle / unknown` |
| `occupancy_pct` | float | nullable(PIX・Nsight 両方で取得可) |
| `occupancy_limiter` | string | nullable(**PIX 系のみ**。例: VGPR limited) |
| `sol_top_unit` | string | nullable(**Nsight 系のみ**。例: TEX 82%) |
| `start_ns` / `end_ns` | int | タイムライン表示用 |
| `depth` | int | マーカーのネスト深さ |
| `parent_id` | int? | ネスト親 |
| `queue` | enum | graphics / async compute / copy |

### 3.3 フレーム単位サマリ

- 総GPU時間、予算(例 16.6ms)との差
- 支配的な律速カテゴリ(カテゴリ別 ms 合計の最大)
- 最重パス
- async オーバーラップ率
- キュー同期のアイドル時間(sync gaps)

### 3.4 ソース別に取れるもの(可用性マトリクス)

| 項目 | PIX 系 | Nsight 系 |
|---|---|---|
| パス別 duration | ○ | ○ |
| occupancy | ○ | ○ |
| occupancy limiter | ○ | ×(—表示) |
| SOL(ユニット別) | × | △(warp CSV 経由の範囲) |
| RT/バックバッファ画像 | ○(pixtool save-resource) | ×(諦める) |

## 4. UI 設計(モックで確定した仕様)

モックファイル: `hotpass-mock.html`(単一HTML・依存なし・ダミーデータ内蔵)

### 4.1 ワークスペース構造

- **タブでソースを切り替えない。読み込んだファイルが表示内容を決める。**
- 上部に「キャプチャレール」: 開いているキャプチャがチップ(カード)で並ぶ。
  - チップに表示: ファイル名、ソースバッジ(PIX / NSIGHT)、フレーム番号、総GPU時間と予算差、**そのファイルが提供できる項目**(例: occupancy · limiter)
  - 「＋ Add capture」で追加、× で閉じる。**複数同時に開ける。**
- モード切替: **Single / Compare** の2モード。

### 4.2 Single view

- ヒーロー: フレームGPU時間の大きな数字 + 予算超過/以内ピル
- **予算バー**: パスを時間比で並べたスタックバー。0/5/10/15/20ms のルーラー目盛、16.6ms 予算マーカー、超過分はハッチング
- 計器クラスタ(4項目): 支配的律速 / 最重パス / async オーバーラップ / sync gaps
- **Pass detail** に **Breakdown / Timeline** のサブ切り替え:
  - **Breakdown**: 重い順ソートの表。カテゴリ色チップ、%バー、duration、occupancy。行クリックで詳細ドロワー(Duration / Limited by / Occupancy+limiter / SOL、出典ファイル注記、**「Open event #### in PIX」ボタン**)
  - **Timeline**: フレームグラフ(flame chart)。横軸=実時間、Graphics キューのレーンに各パスを開始〜終了位置で配置。**ネストしたマーカーは行を下に掘って積む**(親の直下に子)。深い階層ほど親色を薄くしたトーン。狭いスパンはラベル省略しホバーで正確な時間。**Async compute レーンを分離**して重なりを可視化
- ソースにより取れない項目は「—」(例: Nsight ファイルでは limiter が —、PIX では SOL が —)

### 4.3 Compare view

- 各チップに Base / Compare ボタン。**どちらがベースかを徹底明示**:
  - 選択チップに BASE(黒)/ COMPARE(青)のタグと枠色
  - バナーに `BASE file → COMPARE file` の方向表記
  - 規約を常時表示: **Δ = compare − base、緑▼=速い、赤▲=遅い**
  - `⇄ swap` で入れ替え
- サマリ: base→compare のフレーム時間、Net change、Biggest mover
- **予算バー2本を同一目盛で上下に並べる**(base 上・compare 下)
- **Per-pass change 表**: 変化量の大きい順ソート。base値 / compare値 / Δ / 中央線からの発散バー(緑=改善・赤=悪化)。片方にしかないパスは NEW / GONE タグ
- **クロスツール比較の正直さ**: PIX vs Nsight の場合、注記で「duration と occupancy は比較可、limiter と SOL はツール固有なので diff しない」と明示

### 4.4 デザイン方針とオープン課題

- 経緯: ダーク+虹色カード(AI生成っぽい) → ライト計測器風に転換 → **コントラスト不足で見づらい** → 白面主体・文字大きめ・高コントラストに再調整 → **それでも見づらいとの評価。ビジュアルデザインは claude design で仕切り直す。**
- 引き継ぐべきデザイン要件:
  - 情報の階層: ①どこが重い ②何律速 ③GPUを遊ばせてないか、が専門家でなくても読める
  - 数値はタブラー数字で桁揃え
  - 律速カテゴリの色は7分類(raster/texture/memory/compute/geometry/sync/unknown)で一貫
  - base/compare の役割色は本文カテゴリ色と衝突しないこと
- 未解決の論点:
  - **ネスト深さの扱い**: 実データでは5〜6段になり得る。深さ制限+「+N more」で畳むか、全展開スクロールか
  - タイムラインのズーム/パン(現モックは固定20msスケール)
  - ダーク/ライトのどちらを既定にするか

## 5. アーキテクチャ(実装フェーズの想定)

```
[.wpix] ──pixtool CLI──▶ CSV ─┐
[PIX Timing] ─PixStorage.dll─▶├─▶ アダプタ層 ─▶ 共通スキーマ SQLite ─▶ 解析/派生値 ─▶ UI (Web)
[.nsys-rep] ──nsys export───▶ SQLite ─┘                                   │
[GPU Trace] ──warp CSV──────────────────┘                                 └─▶ (PIX画像パイプライン: recapture-region + save-resource → PNG + マニフェスト)
```

- 取り込みは**バッチ前処理**(インポート時に正規化・派生値計算・(PIXのみ)RT画像のPNG事前生成)。ビューアは前処理済みデータを読むだけ。
- `bottleneck_category` の判定ロジック: PIX はカウンタグループ、Nsight は warp CSV から。判定不能は unknown。

## 6. スコープ外(明示)

- シェーダデバッグ、リソース中身の閲覧、ピクセルヒストリ → PIX に導線
- Vulkan / 非D3D12
- Nsight GPU Trace の SOL 詳細(warp CSV で取れる範囲以外)
- Nsight 側の画像抽出

## 7. 次のステップ

1. claude design で UI を仕切り直し(§4 の構造・要件は維持、ビジュアルを再設計)
2. `pix-mcp` の実装調査 → 再利用/差分の判断
3. アダプタ層の PoC: pixtool CSV → 共通スキーマ SQLite(まず duration と マーカー階層)
4. Timeline のネスト折りたたみ・ズーム仕様の確定
5. 実キャプチャでの `bottleneck_category` 判定ルールの検証
