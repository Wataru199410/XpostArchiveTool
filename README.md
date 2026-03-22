# X Post Archive Tool

X の投稿をローカルへ保存し、WPF デスクトップアプリで一覧・検索・タグ管理できるツールです。

- デスクトップ: `WPF (.NET 8)`
- ローカル API: `ASP.NET Core`
- ブラウザ拡張: `Chrome Extension (Manifest V3)`
- 保存先: `XArchive/tweet-<tweet_id>/...`
- DB: `SQLite`
- API: `http://127.0.0.1:18765`

この README は、今後の保守と AI 駆動開発で構造を把握しやすくするために、現状実装ベースで更新しています。

## 設計概要

全体は次の 3 層です。

1. Chrome 拡張
   X の投稿ページへ保存ボタンを差し込み、投稿本文・画像・動画候補・引用元情報・タグ・メモを収集してローカル API に送ります。
2. ローカル API
   投稿情報を検証し、画像保存、動画ダウンロード、SQLite 更新、引用関係更新を行います。
3. デスクトップアプリ
   保存済み投稿の一覧表示、詳細表示、検索、タグ絞り込み、削除、タグ管理を行います。

### 主要な保存フロー

1. `content.js` が X 上の投稿から保存対象データを抽出
2. `service_worker.js` がローカル API へ送信
3. `Program.cs` が `posts / media / tags / post_tags` を更新
4. 画像は即保存、動画はバックグラウンドジョブで保存
5. デスクトップアプリが `archive.db` を読み、一覧と詳細を表示

### データの持ち方

- 正本は SQLite です
- メディア本体はファイルとして `XArchive/` 配下に保存します
- `meta.json` は廃止済みで、ランタイムでは使用していません

## ディレクトリ構成

### アプリ本体

- [App.xaml](/C:/Users/tsuba/Desktop/x-post/App.xaml)
  WPF アプリ定義
- [App.xaml.cs](/C:/Users/tsuba/Desktop/x-post/App.xaml.cs)
  アプリ起動処理
- [MainWindow.xaml](/C:/Users/tsuba/Desktop/x-post/MainWindow.xaml)
  メイン画面 UI
- [MainWindow.xaml.cs](/C:/Users/tsuba/Desktop/x-post/MainWindow.xaml.cs)
  一覧、詳細、検索、タグ絞り込み、削除、引用遷移のロジック
- [DesktopArchiveStore.cs](/C:/Users/tsuba/Desktop/x-post/DesktopArchiveStore.cs)
  デスクトップ側の DB 読み書き
- [LocalApiManager.cs](/C:/Users/tsuba/Desktop/x-post/LocalApiManager.cs)
  ローカル API の起動・停止管理
- [TagEditWindow.xaml](/C:/Users/tsuba/Desktop/x-post/TagEditWindow.xaml)
  投稿詳細から開くタグ編集モーダル UI
- [TagEditWindow.xaml.cs](/C:/Users/tsuba/Desktop/x-post/TagEditWindow.xaml.cs)
  タグ追加・削除・候補表示ロジック
- [TagManagementWindow.xaml](/C:/Users/tsuba/Desktop/x-post/TagManagementWindow.xaml)
  タグ管理画面 UI
- [TagManagementWindow.xaml.cs](/C:/Users/tsuba/Desktop/x-post/TagManagementWindow.xaml.cs)
  タグ新規追加・削除ロジック
- [TagCatalogStore.cs](/C:/Users/tsuba/Desktop/x-post/TagCatalogStore.cs)
  タグマスタ操作の共通処理

### ローカル API

- [Program.cs](/C:/Users/tsuba/Desktop/x-post/Program.cs)
  API 本体。保存、更新、取得、動画ジョブ、認証、DB 更新の中心
- [schema.sql](/C:/Users/tsuba/Desktop/x-post/schema.sql)
  SQLite スキーマ
- [appsettings.json](/C:/Users/tsuba/Desktop/x-post/appsettings.json)
  ホスト、ポート、保存先、DB パス、ffmpeg パス設定

### Chrome 拡張

- [manifest.json](/C:/Users/tsuba/Desktop/x-post/manifest.json)
  拡張定義
- [content.js](/C:/Users/tsuba/Desktop/x-post/content.js)
  X ページで動く content script
- [service_worker.js](/C:/Users/tsuba/Desktop/x-post/service_worker.js)
  API 通信と保存フローを扱う background script
- [page_hook.js](/C:/Users/tsuba/Desktop/x-post/page_hook.js)
  X ページ本体の `fetch / XHR` をフックして API 応答を content script に渡す

### 配布・テスト

- [publish.bat](/C:/Users/tsuba/Desktop/x-post/publish.bat)
  desktop/server の publish
- [build-installer.bat](/C:/Users/tsuba/Desktop/x-post/build-installer.bat)
  Inno Setup でインストーラー生成
- [XPostArchive.iss](/C:/Users/tsuba/Desktop/x-post/XPostArchive.iss)
  インストーラー定義
- [scripts/playwright-manual-check.cjs](/C:/Users/tsuba/Desktop/x-post/scripts/playwright-manual-check.cjs)
  保存動作の手動確認
- [scripts/playwright-save-post-e2e.cjs](/C:/Users/tsuba/Desktop/x-post/scripts/playwright-save-post-e2e.cjs)
  投稿保存の E2E
- [scripts/playwright-verify-video-save.cjs](/C:/Users/tsuba/Desktop/x-post/scripts/playwright-verify-video-save.cjs)
  動画保存の検証
- [scripts/playwright-copy-error-e2e.cjs](/C:/Users/tsuba/Desktop/x-post/scripts/playwright-copy-error-e2e.cjs)
  エラー UI の確認

### 実行時に生成されるもの

次は Git 管理対象ではありません。

- `XArchive/`
  保存済み投稿本体
- `data/archive.db`
  SQLite DB
- `data/auth_token.txt`
  API トークン
- `dist/`
  publish / installer 生成物
- `bin/`, `obj/`, `obj-api/`
  ビルド生成物

## 主な API

### 認証・状態確認

- `GET /api/v1/health`
- `POST /api/v1/auth/bootstrap`

### 投稿

- `POST /api/v1/posts`
  新規保存
- `PUT /api/v1/posts/{tweetId}`
  上書き保存
- `GET /api/v1/posts/{tweetId}`
  保存済み確認

### タグ

- `GET /api/v1/tags`
  タグ候補一覧

## 開発時の基本コマンド

### デスクトップ起動

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

1. `chrome://extensions` を開く
2. デベロッパーモードを ON
3. `パッケージ化されていない拡張機能を読み込む`
4. このリポジトリ直下を選択

拡張更新後は、次も必要です。

- 拡張を再読み込み
- 対象の X タブを開き直す

## 配布

### publish

```powershell
publish.bat
```

出力先:

- `dist/publish/desktop`
- `dist/publish/server`

### インストーラー生成

前提:

- .NET 8 SDK
- Inno Setup 6
- `third_party/ffmpeg/ffmpeg.exe`

```powershell
build-installer.bat
```

出力先:

- `dist/installer/XPostArchive-Setup.exe`

## AI 駆動開発向けメモ

### まず読むべきファイル

優先順位は次の順が把握しやすいです。

1. [Program.cs](/C:/Users/tsuba/Desktop/x-post/Program.cs)
2. [content.js](/C:/Users/tsuba/Desktop/x-post/content.js)
3. [service_worker.js](/C:/Users/tsuba/Desktop/x-post/service_worker.js)
4. [DesktopArchiveStore.cs](/C:/Users/tsuba/Desktop/x-post/DesktopArchiveStore.cs)
5. [MainWindow.xaml.cs](/C:/Users/tsuba/Desktop/x-post/MainWindow.xaml.cs)

### 実装上の前提

- 動画はバックグラウンド保存です
- デスクトップアプリ終了時、保存中動画は中断されます
- 引用元投稿は `quoted_tweet_id` / `quoted_post_id` で管理します
- `content.js` は DOM だけでなく X の API 応答キャッシュも参照します
- 画像・動画の実体はファイル、メタ情報は DB に分離されています

### 変更時に注意する点

- `content.js` 更新後は、拡張の再読み込みだけでなく X タブの開き直しが必要なことがあります
- `Program.cs` は保存・更新・動画ジョブ・認証が集中しているため、副作用確認が重要です
- 投稿削除と更新は DB とファイルの整合性を崩しやすいので、レビュー優先度を高くしてください
- 動画保存まわりは Playwright と実ファイル確認の両方で見るのが安全です

## 既知の性質

- API は `127.0.0.1` bind です
- 認証 bootstrap は localhost 前提の簡易設計です
- 動画保存は X 側配信方式の変化に影響されやすいです
- Playwright での確認結果と、ログイン済みブラウザ表示が完全一致しないケースがあります
