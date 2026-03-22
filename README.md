# X Post Archive Tool

X の投稿をローカルに保存し、Windows デスクトップアプリで一覧・検索・タグ管理できるツールです。

構成は次の 3 層です。

- Chrome 拡張: X の投稿ページに保存ボタンを表示し、投稿内容を抽出する
- ローカル API: 画像・動画・DB 保存を行う
- WPF デスクトップアプリ: 保存済み投稿の閲覧、検索、タグ管理、削除を行う

## 設計概要

### 役割分担

1. `content.js`
   X のページ上で動く content script です。投稿本文、投稿者、画像、動画候補、引用元情報を抽出し、保存モーダルを表示します。

2. `service_worker.js`
   Chrome 拡張の background script です。ローカル API 認証、保存リクエスト送信、動画候補 URL の最終選定を担当します。

3. `Program.cs`
   ローカル API 本体です。投稿保存、更新、タグ取得、動画ダウンロード、SQLite 更新を担当します。

4. デスクトップアプリ
   `MainWindow.xaml(.cs)` を中心に、投稿一覧・詳細表示、タグ絞り込み、タグ管理、削除、引用元/引用先ジャンプを提供します。

### 保存データ

- 投稿メディア: `XArchive/tweet-<tweet_id>/...`
- SQLite DB: `data/archive.db` または配布版では `%LocalAppData%\XPostArchive\data\archive.db`
- 認証トークン: `data/auth_token.txt` または配布版では `%LocalAppData%\XPostArchive\data\auth_token.txt`

配布版では保存データをインストール先ではなく `%LocalAppData%\XPostArchive\...` に保存します。そのため、通常の上書き更新では保存済みデータが消えにくい構成です。

## ディレクトリ構成

### デスクトップアプリ

- `App.xaml`, `App.xaml.cs`
  アプリ起動定義
- `MainWindow.xaml`, `MainWindow.xaml.cs`
  メイン UI と画面ロジック
- `DesktopArchiveStore.cs`
  デスクトップ側の DB 読み書き
- `LocalApiManager.cs`
  ローカル API の起動・停止・疎通確認
- `TagEditWindow.xaml`, `TagEditWindow.xaml.cs`
  投稿詳細のタグ編集モーダル
- `TagManagementWindow.xaml`, `TagManagementWindow.xaml.cs`
  タグ管理画面
- `TagCatalogStore.cs`
  タグ一覧の読み書き

### ローカル API

- `Program.cs`
  API 本体
- `schema.sql`
  SQLite スキーマ
- `appsettings.json`
  API 設定

### Chrome 拡張

- `manifest.json`
  拡張定義
- `content.js`
  X ページ上で動く content script
- `service_worker.js`
  拡張の background script
- `page_hook.js`
  ページ本体の `fetch/XHR` をフックし、X の API 応答を content script に渡す

### 配布・検証

- `publish.bat`
  desktop / server の publish 実行
- `build-installer.bat`
  Inno Setup を使ったインストーラー生成
- `XPostArchive.iss`
  Inno Setup 定義
- `scripts/playwright-manual-check.cjs`
  手動確認用 Playwright
- `scripts/playwright-save-post-e2e.cjs`
  投稿保存 E2E
- `scripts/playwright-verify-video-save.cjs`
  動画保存検証
- `scripts/playwright-copy-error-e2e.cjs`
  エラー UI 検証

## 主な API

### ヘルスチェック・認証

- `GET /api/v1/health`
- `POST /api/v1/auth/bootstrap`

### 投稿

- `POST /api/v1/posts`
  新規保存
- `PUT /api/v1/posts/{tweetId}`
  既存投稿の更新
- `GET /api/v1/posts/{tweetId}`
  保存済み投稿の取得

### タグ

- `GET /api/v1/tags`
  タグ一覧取得

## 開発用コマンド

### デスクトップアプリ起動

```powershell
dotnet run --project XPostArchive.Desktop.csproj
```

### API 単体起動

```powershell
dotnet run --project XPostArchive.Api.csproj
```

### デスクトップビルド

```powershell
dotnet build XPostArchive.Desktop.csproj
```

### API ビルド

```powershell
dotnet build XPostArchive.Api.csproj
```

### Playwright 確認

```powershell
npm run test:manual:playwright
```

```powershell
npm run test:e2e:save-post
```

## Chrome 拡張の読み込み

1. Chrome で `chrome://extensions` を開く
2. `デベロッパーモード` を ON にする
3. `パッケージ化されていない拡張機能を読み込む` を押す
4. このリポジトリのフォルダを選ぶ

## インストーラーのビルド手順

### 前提

- .NET 8 SDK
- Inno Setup 6
- `third_party/ffmpeg/ffmpeg.exe`

### 手順

リポジトリ直下で次を実行します。

```powershell
build-installer.bat
```

内部では次を行います。

1. `publish.bat` で desktop / server を publish
2. `XPostArchive.iss` を使って Inno Setup でインストーラー化

### 出力先

- `dist\installer\XPostArchive-Setup.exe`

## 共有相手に伝えるインストール手順

### 1. アプリのインストール

1. `XPostArchive-Setup.exe` を実行する
2. 画面の案内に従ってインストールする
3. インストール後、デスクトップアプリを一度起動する

### 2. Chrome 拡張の読み込み

1. Chrome で `chrome://extensions` を開く
2. `デベロッパーモード` を ON にする
3. `パッケージ化されていない拡張機能を読み込む` を押す
4. 既定のインストール先なら、次のフォルダを選ぶ

```text
C:\Program Files\XPostArchive\extension
```

インストール先を変更した場合は、`{インストール先}\extension` を選んでください。

### 3. 使い方

1. X にログインした状態で保存したい投稿を開く
2. 投稿に表示される `保存` または `上書き保存` を押す
3. 必要に応じてタグ・メモを入力して保存する
4. 保存済み投稿はデスクトップアプリ側の一覧に表示される

### 4. 更新方法

- 新しい版を受け取ったら、古い版を削除せずそのまま上書きインストールする
- 保存データは `%LocalAppData%\XPostArchive\...` にあるため、通常の更新では消えない

### 5. 注意点

- 動画付き投稿はバックグラウンド保存になることがある
- 動画保存中にアプリを閉じると中断される
- Chrome 拡張を更新した場合は `chrome://extensions` で再読み込みが必要

## 実行時生成物

次は Git 管理対象外です。

- `XArchive/`
- `data/archive.db`
- `data/auth_token.txt`
- `dist/`
- `bin/`, `obj/`, `obj-api/`

## AI 駆動開発向けメモ

### 読み始める順番

1. `Program.cs`
2. `content.js`
3. `service_worker.js`
4. `DesktopArchiveStore.cs`
5. `MainWindow.xaml.cs`

### 前提として知っておくこと

- 投稿保存の正本は DB
- 動画はバックグラウンドジョブで保存される
- 引用元/引用先は `quoted_tweet_id` / `quoted_post_id` で管理される
- `content.js` は DOM 抽出だけでなく、`page_hook.js` 経由で X の API 応答も利用する
