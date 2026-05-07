-- Cinder case database — initial schema.
-- Append-only by convention. Mutations to existing rows are forbidden by app code.

CREATE TABLE cases (
    id              TEXT    PRIMARY KEY,
    name            TEXT    NOT NULL,
    examiner        TEXT    NOT NULL,
    description     TEXT    NULL,
    created_utc     TEXT    NOT NULL,
    schema_version  INTEGER NOT NULL
);

CREATE TABLE custody_entries (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    case_id         TEXT    NOT NULL REFERENCES cases(id),
    sequence        INTEGER NOT NULL,
    timestamp_utc   TEXT    NOT NULL,
    examiner        TEXT    NOT NULL,
    action          TEXT    NOT NULL,
    details_json    TEXT    NOT NULL,
    prev_hash       TEXT    NOT NULL,
    entry_hash      TEXT    NOT NULL UNIQUE
);

CREATE INDEX ix_custody_case_seq ON custody_entries(case_id, sequence);

CREATE TABLE hashes (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    case_id         TEXT    NOT NULL REFERENCES cases(id),
    target_path     TEXT    NOT NULL,
    target_size     INTEGER NOT NULL,
    md5             TEXT    NULL,
    sha1            TEXT    NULL,
    sha256          TEXT    NULL,
    blake3          TEXT    NULL,
    computed_utc    TEXT    NOT NULL
);

CREATE INDEX ix_hashes_case ON hashes(case_id);
CREATE INDEX ix_hashes_sha256 ON hashes(sha256);
