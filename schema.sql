-- ONE HEALTH — PostgreSQL Schema
-- Reset + recreate: psql -U postgres -d one_health -f schema.sql

DROP TABLE IF EXISTS leituras;
DROP TABLE IF EXISTS sensores;

-- Registered sensors and their current state
CREATE TABLE IF NOT EXISTS sensores (
    sensor_id    VARCHAR(20)  PRIMARY KEY,
    gateway_id   VARCHAR(50)  NOT NULL,
    zona         VARCHAR(50)  NOT NULL,
    tipos        TEXT,
    video_stream BOOLEAN      DEFAULT FALSE,
    status       VARCHAR(20)  DEFAULT 'desconhecido',
    ultima_sync  TIMESTAMP,
    registado_em TIMESTAMP    DEFAULT NOW()
);

-- Individual sensor readings (raw, validated by preprocessing)
CREATE TABLE IF NOT EXISTS leituras (
    id         BIGSERIAL      PRIMARY KEY,
    sensor_id  VARCHAR(20),
    gateway_id VARCHAR(50),
    zona       VARCHAR(50),
    tipo_dado  VARCHAR(10),
    valor      NUMERIC(10, 3),
    timestamp  TIMESTAMPTZ,
    is_alarm      BOOLEAN        DEFAULT FALSE,
    qualidade     REAL           DEFAULT 1.0,
    anomaly_score REAL           DEFAULT NULL
);

CREATE INDEX IF NOT EXISTS idx_leituras_zona_tipo_ts ON leituras (zona, tipo_dado, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_leituras_sensor_ts    ON leituras (sensor_id, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_leituras_alarmes      ON leituras (is_alarm) WHERE is_alarm = TRUE;
CREATE INDEX IF NOT EXISTS idx_leituras_anomaly      ON leituras (anomaly_score DESC) WHERE anomaly_score IS NOT NULL;
