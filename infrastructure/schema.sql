-- ClipSync Production PostgreSQL Schema

CREATE TABLE IF NOT EXISTS devices (
    device_id VARCHAR(64) PRIMARY KEY,
    token_hash VARCHAR(64) NOT NULL,
    label VARCHAR(128) NOT NULL,
    platform VARCHAR(32) NOT NULL,
    group_id VARCHAR(128) NOT NULL,
    sync_salt VARCHAR(64) NOT NULL,
    public_key TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_revoked BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE INDEX IF NOT EXISTS idx_devices_group_id ON devices (group_id);
CREATE INDEX IF NOT EXISTS idx_devices_token_hash ON devices (token_hash);

CREATE TABLE IF NOT EXISTS pairing_sessions (
    code VARCHAR(32) PRIMARY KEY,
    session_id VARCHAR(64) NOT NULL,
    group_id VARCHAR(128) NOT NULL,
    sync_salt VARCHAR(64) NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    claimed BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_pairing_sessions_code_exp ON pairing_sessions (code, expires_at);

CREATE TABLE IF NOT EXISTS clipboard_items (
    item_id VARCHAR(64) PRIMARY KEY,
    group_id VARCHAR(128) NOT NULL,
    source_device_id VARCHAR(64) NOT NULL,
    type VARCHAR(32) NOT NULL,
    version INT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    payload TEXT NOT NULL,
    metadata JSONB,
    storage_ref TEXT,
    sequence_number BIGSERIAL NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_clipboard_items_group_seq 
    ON clipboard_items (group_id, sequence_number);
CREATE INDEX IF NOT EXISTS idx_clipboard_items_created_at 
    ON clipboard_items (created_at);
