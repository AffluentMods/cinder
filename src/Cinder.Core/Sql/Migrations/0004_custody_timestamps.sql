-- RFC 3161 trusted timestamps over attestation signatures. The examiner key binds the chain to
-- the examiner; the TSA token binds the attestation to a third party's clock, so the examiner
-- cannot backdate it either. Token is the DER-encoded CMS the TSA returned, self-contained.

ALTER TABLE custody_attestations ADD COLUMN tsa_token BLOB;
ALTER TABLE custody_attestations ADD COLUMN tsa_url   TEXT;
ALTER TABLE custody_attestations ADD COLUMN tsa_time  TEXT;
