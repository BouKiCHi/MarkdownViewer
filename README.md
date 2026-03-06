# MarkdownViewer

WPF + WebView2 で動作する、シンプルな Markdown ビューアです。  
`marked` / `Prism` / `Mermaid` を使って Markdown を表示します。

## 主な機能

- Markdown ファイル表示
- Mermaid コードブロック描画（```mermaid```）
- 履歴一覧（新しく開いた順）
- 履歴ペインの表示/非表示トグル
- 再読み込み
- ファイル選択ダイアログからのオープン
- コマンドライン引数からのファイルオープン
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

## UI

- 左上: 履歴表示トグル
- 左上（トグル右隣）: ファイル選択
- 中央: 現在表示中のファイル名
- 右上: 再読み込み
- 左ペイン: 履歴一覧
- 右ペイン: Markdown 表示領域（WebView2）

## 履歴保存

履歴は実行ディレクトリの `settings.json` に保存されます。  
実行ディレクトリに書き込めない場合は `%LocalAppData%\\MarkdownViewer\\settings.json` へ保存します。

- 形式: JSON
- 保存タイミング: 履歴追加/並び替え時
- 複数インスタンス対策: 保存時に一度 `settings.json` を再読込してマージ

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
- 外部 CDN（cdnjs / jsDelivr）からライブラリを読み込みます。

## 参考

- [なるべくコードを書かずに「.md」ファイルを HTML に変換 ／ Marked.js + Prism.js + Mermaid.js](https://zenn.dev/k586/articles/ae95ec2aac4c97)

## ライセンス

このプロジェクトは [MIT License](./LICENSE) で公開しています。
