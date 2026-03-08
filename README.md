# MarkdownViewer

WPF + WebView2 で動作する、シンプルな Markdown ビューアです。  
`marked` / `Prism` / `Mermaid` を使って Markdown を表示します。  
必要なフロントエンド資産はアプリへ同梱しており、オフラインでも表示できます。

## 主な機能

- Markdown ファイル表示
- Mermaid コードブロック描画（```mermaid```）
- 履歴一覧（新しく開いた順）
- 履歴ペインの表示/非表示トグル
- 履歴項目の右クリックメニュー
  - 別ウインドウで開く
  - エクスプローラで開く
  - パスをコピー
  - 履歴から削除
- 再読み込み
- HTMLサニタイズ（既定で有効）
- HTMLサニタイズ無効化トグル（Unsafe）
- ファイル選択ダイアログからのオープン
- コマンドライン引数からのファイルオープン
- シングルインスタンス起動
- 既存インスタンスへのファイル受け渡し
- 履歴の JSON 永続化

## 必要環境

- Windows
- .NET SDK（プロジェクトは `net10.0-windows`）
- Microsoft Edge WebView2 Runtime

## ビルド

```bash
dotnet build MarkdownViewer.slnx
```

## Publish（軽量・single-file寄り）

```bash
dotnet publish MarkdownViewer/MarkdownViewer.csproj -c Release -r win-x64
```

- `Release`では `PublishSingleFile=true` を有効化
- `SelfContained=false` なので、サイズは小さめ（実行には対応 .NET Runtime が必要）
- `DebugType=none` / `DebugSymbols=false` で PDB は出力しない設定

### リリース用スクリプト

PowerShellスクリプトで、`publish` / `zip作成` / `SHA256SUMS.txt` 生成を一括実行できます。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\release.ps1
```

主な出力先:

- `artifacts/publish/...` : publish結果
- `artifacts/package/*.zip` : 配布zip
- `artifacts/package/SHA256SUMS.txt` : zipとexeのSHA-256

## 実行

### 1. ファイル指定で起動

```bash
MarkdownViewer.exe C:\path\to\sample.md
```

### 2. ファイル未指定で起動

```bash
MarkdownViewer.exe
```

引数なしで起動した場合は、履歴があれば最新履歴のファイル読み込みを試み、履歴が空の場合は起動時にファイル選択ダイアログを開きます。  
また、ヘッダーの「ファイル選択」アイコンからもファイルを開けます。

既に起動中のインスタンスがある場合は、そのウインドウへファイルを引き渡して前面表示します。

## UI

- 左上: 履歴表示トグル
- 左上（トグル右隣）: ファイル選択
- 中央: 現在表示中のファイル名
- 右上: 再読み込み
- 右上（再読み込み右隣）: HTMLサニタイズ無効化トグル
- 左ペイン: 履歴一覧
  - 同名ファイルが複数ある場合のみ、区別用のフォルダ suffix を補助表示
  - 右クリックで履歴操作メニュー
- 右ペイン: Markdown 表示領域（WebView2）

## 履歴保存

履歴は実行ディレクトリの `settings.json` に保存されます。  
実行ディレクトリに書き込めない場合は `%LocalAppData%\\MarkdownViewer\\settings.json` へ保存します。

- 形式: JSON
- 保存タイミング: 履歴追加/削除、履歴ペイン表示状態変更時
- 複数ウインドウ時: 保存後に同一プロセス内の各ウインドウへ再反映

`settings.json` の例:

```json
{
  "History": [
    "C:\\docs\\a.md",
    "C:\\docs\\b.md"
  ],
  "IsHistoryPaneVisible": true
}
```

## 補足

- 相対画像・相対リンクは、開いた Markdown ファイルのディレクトリ基準で解決されます。
- `marked` / `DOMPurify` / `Prism` / `Mermaid` / `github-markdown-css` はアプリ同梱で、外部 CDN には依存しません。
- 通常は Markdown 内の生 HTML をサニタイズします。`Unsafe` トグルを有効にすると、そのセッション中だけサニタイズを無効化します。

## 同梱ライブラリ

- `marked`
- `DOMPurify`
- `Prism`
- `@mermaid-js/tiny`
- `github-markdown-css`

## 参考

- [なるべくコードを書かずに「.md」ファイルを HTML に変換 ／ Marked.js + Prism.js + Mermaid.js](https://zenn.dev/k586/articles/ae95ec2aac4c97)

## ライセンス

このプロジェクトは [MIT License](./LICENSE) で公開しています。

同梱しているサードパーティ資産については [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md) を参照してください。
