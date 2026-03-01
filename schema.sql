PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS authors (
  id          INTEGER PRIMARY KEY,
  handle      TEXT    NOT NULL UNIQUE,
  name        TEXT    NOT NULL,
  created_at  TEXT    NOT NULL,
  updated_at  TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS posts (
  id         INTEGER PRIMARY KEY,
  tweet_id   TEXT    NOT NULL UNIQUE,
  url        TEXT    NOT NULL,
  author_id  INTEGER NOT NULL,
  created_at TEXT    NOT NULL,
  text       TEXT    NOT NULL,
  note       TEXT    NULL,
  saved_at   TEXT    NOT NULL,
  dir_path   TEXT    NOT NULL,
  FOREIGN KEY (author_id) REFERENCES authors(id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS media (
  id           INTEGER PRIMARY KEY,
  post_id      INTEGER NOT NULL,
  media_type   TEXT    NOT NULL CHECK (media_type IN ('image','video')),
  original_url TEXT    NULL,
  local_path   TEXT    NOT NULL,
  sort_order   INTEGER NOT NULL DEFAULT 0,
  FOREIGN KEY (post_id) REFERENCES posts(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS tags (
  id         INTEGER PRIMARY KEY,
  name       TEXT    NOT NULL UNIQUE,
  created_at TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS post_tags (
  post_id    INTEGER NOT NULL,
  tag_id     INTEGER NOT NULL,
  created_at TEXT    NOT NULL,
  PRIMARY KEY (post_id, tag_id),
  FOREIGN KEY (post_id) REFERENCES posts(id) ON DELETE CASCADE,
  FOREIGN KEY (tag_id)  REFERENCES tags(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_posts_author_saved
  ON posts(author_id, saved_at);

CREATE INDEX IF NOT EXISTS idx_posts_saved_at
  ON posts(saved_at);

CREATE INDEX IF NOT EXISTS idx_media_post_sort
  ON media(post_id, sort_order);

CREATE INDEX IF NOT EXISTS idx_post_tags_tag_post
  ON post_tags(tag_id, post_id);
