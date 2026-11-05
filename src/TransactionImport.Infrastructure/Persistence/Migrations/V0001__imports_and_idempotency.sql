-- Imports: one row per uploaded file. Also acts as the durable job record (lease, attempts, checkpoint);
-- the Redis stream only carries "work available" notifications.
CREATE TABLE imports (
    id                      uuid        PRIMARY KEY,
    provider_id             text        NOT NULL CHECK (length(provider_id) BETWEEN 1 AND 64),
    status                  text        NOT NULL CHECK (status IN ('pending', 'processing', 'completed', 'failed', 'cancelling', 'cancelled')),
    file_name               text        NOT NULL CHECK (length(file_name) BETWEEN 1 AND 255),
    storage_key             text        NOT NULL,
    file_size_bytes         bigint      NOT NULL CHECK (file_size_bytes >= 0),
    file_sha256             text        NOT NULL CHECK (file_sha256 ~ '^[0-9a-f]{64}$'),

    processed_count         bigint      NOT NULL DEFAULT 0,
    accepted_count          bigint      NOT NULL DEFAULT 0,
    rejected_count          bigint      NOT NULL DEFAULT 0,
    duplicate_count         bigint      NOT NULL DEFAULT 0,

    -- Resume point: byte offset and line number just after the last committed batch.
    checkpoint_byte_offset  bigint      NOT NULL DEFAULT 0 CHECK (checkpoint_byte_offset >= 0),
    checkpoint_line_number  bigint      NOT NULL DEFAULT 0 CHECK (checkpoint_line_number >= 0),

    attempt_count           integer     NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    max_attempts            integer     NOT NULL CHECK (max_attempts > 0),
    locked_by               text        NULL,
    lease_token             uuid        NULL,
    lease_expires_at        timestamptz NULL,
    last_enqueued_at        timestamptz NULL,

    failure_code            text        NULL,
    failure_reason          text        NULL,

    created_at              timestamptz NOT NULL,
    started_at              timestamptz NULL,
    completed_at            timestamptz NULL,
    updated_at              timestamptz NOT NULL,

    CONSTRAINT imports_progress_consistent
        CHECK (processed_count = accepted_count + rejected_count AND duplicate_count BETWEEN 0 AND rejected_count AND accepted_count >= 0),
    CONSTRAINT imports_lease_consistent
        CHECK ((lease_token IS NULL) = (lease_expires_at IS NULL) AND (lease_token IS NULL) = (locked_by IS NULL)),
    CONSTRAINT imports_lease_only_while_active
        CHECK (lease_token IS NULL OR status IN ('processing', 'cancelling')),
    CONSTRAINT imports_completed_at_matches_terminal
        CHECK ((status IN ('completed', 'failed', 'cancelled')) = (completed_at IS NOT NULL))
);

-- Stale-lease recovery scans only active imports.
CREATE INDEX imports_active_lease_idx ON imports (lease_expires_at) WHERE status IN ('processing', 'cancelling');
-- Re-enqueue of pending imports whose notification may have been lost.
CREATE INDEX imports_pending_enqueued_idx ON imports (last_enqueued_at) WHERE status = 'pending';
-- Retention of terminal imports.
CREATE INDEX imports_terminal_completed_idx ON imports (completed_at) WHERE status IN ('completed', 'failed', 'cancelled');
CREATE INDEX imports_provider_created_idx ON imports (provider_id, created_at DESC);

-- Idempotency keys are scoped per provider. A row is inserted *before* the upload streams so that
-- concurrent requests with the same key are serialized by the primary key, not by application locks.
CREATE TABLE idempotency_keys (
    provider_id      text        NOT NULL,
    idempotency_key  text        NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    status           text        NOT NULL CHECK (status IN ('in_progress', 'completed')),
    lock_token       uuid        NOT NULL,
    locked_until     timestamptz NOT NULL,
    import_id        uuid        NULL REFERENCES imports (id) ON DELETE CASCADE,
    created_at       timestamptz NOT NULL,
    completed_at     timestamptz NULL,
    PRIMARY KEY (provider_id, idempotency_key),
    CONSTRAINT idempotency_completed_has_import CHECK ((status = 'completed') = (import_id IS NOT NULL))
);

CREATE INDEX idempotency_keys_created_idx ON idempotency_keys (created_at);
