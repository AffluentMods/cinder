-- Bookmarks: an examiner flags a row in any tool as a finding, with a note. The report
-- builder turns them into exhibits. Append-only like everything else in the case file;
-- a bookmark the examiner withdraws is deleted here and the deletion is a custody entry.

CREATE TABLE bookmarks (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    case_id         TEXT    NOT NULL REFERENCES cases(id),
    created_utc     TEXT    NOT NULL,
    examiner        TEXT    NOT NULL,
    tool            TEXT    NOT NULL,
    evidence_path   TEXT    NULL,
    title           TEXT    NOT NULL,
    note            TEXT    NULL,
    row_json        TEXT    NOT NULL
);

CREATE INDEX ix_bookmarks_case ON bookmarks(case_id, created_utc);
