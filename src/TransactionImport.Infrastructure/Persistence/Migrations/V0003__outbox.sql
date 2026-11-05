-- Transactional outbox: messages are written in the same transaction as the state change that produced
-- them and relayed to Redis afterwards, so "import created but never queued" cannot happen.
CREATE TABLE outbox_messages (
    id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    topic         text        NOT NULL,
    payload       jsonb       NOT NULL,
    created_at    timestamptz NOT NULL,
    available_at  timestamptz NOT NULL,
    published_at  timestamptz NULL,
    attempts      integer     NOT NULL DEFAULT 0,
    last_error    text        NULL
);

CREATE INDEX outbox_unpublished_idx ON outbox_messages (available_at, id) WHERE published_at IS NULL;
CREATE INDEX outbox_published_idx ON outbox_messages (published_at) WHERE published_at IS NOT NULL;
