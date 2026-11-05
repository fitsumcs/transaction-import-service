-- Accepted transactions. The primary key is the duplicate-detection mechanism: it holds within a file,
-- across files, across concurrent workers and across restarts.
CREATE TABLE transactions (
    provider_id          text          NOT NULL,
    transaction_id       text          NOT NULL CHECK (length(transaction_id) BETWEEN 1 AND 64),
    import_id            uuid          NOT NULL REFERENCES imports (id),
    line_number          bigint        NOT NULL CHECK (line_number > 0),
    account_id           text          NOT NULL,
    merchant_id          text          NOT NULL,
    amount               numeric(19,4) NOT NULL CHECK (amount > 0),
    currency             char(3)       NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
    occurred_at          timestamptz   NOT NULL,
    description          text          NULL CHECK (description IS NULL OR length(description) <= 500),
    fingerprint          bytea         NOT NULL CHECK (length(fingerprint) = 32),
    fingerprint_version  smallint      NOT NULL,
    risk_score           smallint      NOT NULL CHECK (risk_score BETWEEN 0 AND 100),
    risk_level           text          NOT NULL CHECK (risk_level IN ('low', 'medium', 'high')),
    created_at           timestamptz   NOT NULL,
    PRIMARY KEY (provider_id, transaction_id)
);

-- Import-to-transaction relationship (the import that first accepted the transaction); used by summaries.
CREATE INDEX transactions_import_idx ON transactions (import_id);

-- Rejected records. (import_id, line_number) is unique so a replayed batch cannot double-insert, and it is
-- the natural keyset for cursor pagination.
CREATE TABLE rejected_records (
    import_id       uuid        NOT NULL REFERENCES imports (id) ON DELETE CASCADE,
    line_number     bigint      NOT NULL CHECK (line_number > 0),
    reason          text        NOT NULL,
    message         text        NOT NULL CHECK (length(message) <= 300),
    transaction_id  text        NULL CHECK (transaction_id IS NULL OR length(transaction_id) <= 64),
    raw_value       jsonb       NULL CHECK (raw_value IS NULL OR pg_column_size(raw_value) <= 2048),
    created_at      timestamptz NOT NULL,
    PRIMARY KEY (import_id, line_number)
);

-- Summary snapshot materialized once an import reaches a terminal state.
CREATE TABLE import_summaries (
    import_id    uuid        PRIMARY KEY REFERENCES imports (id) ON DELETE CASCADE,
    summary      jsonb       NOT NULL,
    computed_at  timestamptz NOT NULL
);
