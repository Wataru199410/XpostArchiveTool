# Xポスト手動保存ツール（ローカル完結）

設計確定内容に合わせた初期実装です。

- バックエンド: `C# (.NET 8) + ASP.NET Core + SQLite`
- 拡張機能: `Chrome Extension (Manifest V3)`
- 保存先: `XArchive/tweet-<tweet_id>/...`
- API: `http://127.0.0.1:18765`

## 実装済み（初期版）

- `GET /api/v1/health`
- `POST /api/v1/auth/bootstrap`
- `POST /api/v1/posts`
- Bearerトークン認証
- `tweet_id` 重複時の `409 Conflict`
- `created_at` ISO8601厳格チェック（不正時は保存中止）
- 画像保存（`name=orig` 優先）
- 動画保存（ffmpeg、内部リトライ）
- 失敗時の詳細エラー返却（拡張側で再試行可）
- WPF起動時にローカルAPIを自動起動（PowerShell操作不要の方向）

## ディレクトリ

- `server/`: localhost API + SQLite保存処理
- `extension/`: Chrome拡張（保存ボタン、スクショ、API送信）
- `docs/`: 設計メモ

## ユーザー向け利用手順（Windows）

1. インストーラーでアプリを入れる
2. 完了画面で `X Post Archive を起動する` にチェックしたまま `完了(OK)` を押す
3. 初回のみ、案内に従って Chrome 拡張を読み込む（`{インストール先}\\extension`）
4. X画面で「このポストを保存」を押す

ユーザー側の事前準備（PowerShell操作、.NET導入、ffmpeg導入）は不要です。

## 開発者向けビルド（Windows）

1. .NET 8 SDK をインストール
2. Inno Setup 6 をインストール
3. `third_party\\ffmpeg\\ffmpeg.exe` を配置
4. コマンド実行

```powershell
cd scripts
build-installer.bat
```

生成物:

- `dist\\installer\\XPostArchive-Setup.exe`

## 注意点

- この開発環境は macOS かつ `dotnet` 未導入のため、ここではビルド検証していません。
- WPFデスクトップUI本体（一覧、検索、詳細、タグ編集）は次フェーズ実装です。
- 配布時は `desktop` と `server` と `extension` を同梱し、`desktop` から `server` 実行ファイルを起動する構成です。
