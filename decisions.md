# 確定仕様メモ

- UI: WPF
- APIポート: `127.0.0.1:18765` 固定
- 認証: `POST /api/v1/auth/bootstrap` + `Authorization: Bearer <token>`
- `created_at`: 正規化失敗時は保存中止（NULL登録しない）
- 画像: `name=orig` 優先
- ブラウザ: Chromeのみ
- タグ: 任意入力
- 検索: LIKE開始（将来FTS5）
- 重複保存: `tweet_id UNIQUE`、重複は拒否（409）
- 動画失敗時: エラー詳細を返し、拡張側で「再試行 / 中止」を選択
