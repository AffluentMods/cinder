-- Signed statements about the custody chain's tip. The chain hash alone is unkeyed and lives
-- in this file; an attestation binds a (sequence, entry_hash) pair to an examiner key held
-- outside the file, so a rewrite after signing is detectable by anyone with this file alone.

CREATE TABLE custody_attestations (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    case_id         TEXT    NOT NULL REFERENCES cases(id),
    sequence        INTEGER NOT NULL,
    entry_hash      TEXT    NOT NULL,
    signed_utc      TEXT    NOT NULL,
    examiner        TEXT    NOT NULL,
    public_key_spki TEXT    NOT NULL,   -- base64 SubjectPublicKeyInfo, so verification needs only this file
    signature       TEXT    NOT NULL    -- base64 ECDSA P-256 / SHA-256 over case_id|sequence|entry_hash|signed_utc
);

CREATE INDEX ix_attestations_case ON custody_attestations(case_id, sequence);
