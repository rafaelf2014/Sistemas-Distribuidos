import { useEffect, useState } from "react";
import {
  BarChart, Bar, XAxis, YAxis, CartesianGrid,
  Tooltip, ResponsiveContainer, Cell,
} from "recharts";
import { api } from "../api";
import type { Leitura, Anomalia } from "../api";
import "./Analise.css";

const TIPOS = ["TEMP", "HUM", "CO2", "RUIDO", "LUMIN", "PART", "NO2", "O3", "WIND"];
const UNIDADES: Record<string, string> = {
  TEMP: "°C", HUM: "%", CO2: "ppm", RUIDO: "dB", LUMIN: "lux", PART: "µg/m³",
  NO2: "µg/m³", O3: "ppb", WIND: "km/h",
};

// ── Risk thresholds ───────────────────────────────────────────────────────────
// Weights sum to 1.0 across individual tiers; combos add bonus on top (capped at 1).
// Thresholds use the MAX reading per type (not average) so acute spikes
// like fire or smog are detected immediately instead of being diluted.
const RISK_TIERS: Array<{
  tipo: string; label: string; unit: string;
  warn: number; danger: number; weight: number; invert?: boolean;
}> = [
  { tipo:"TEMP",  label:"Temperatura",   unit:"°C",    warn:25,  danger:35,  weight:0.25 },
  { tipo:"CO2",   label:"CO₂",           unit:"ppm",   warn:600, danger:1200,weight:0.20 },
  { tipo:"PART",  label:"Partículas",    unit:"µg/m³", warn:20,  danger:75,  weight:0.15 },
  { tipo:"NO2",   label:"NO₂",           unit:"µg/m³", warn:50,  danger:200, weight:0.15 },
  { tipo:"RUIDO", label:"Ruído",         unit:"dB",    warn:55,  danger:80,  weight:0.10 },
  { tipo:"O3",    label:"Ozono",         unit:"ppb",   warn:40,  danger:120, weight:0.10 },
  { tipo:"HUM",   label:"Humidade alta", unit:"%",     warn:70,  danger:85,  weight:0.05 },
];

const COMBO_TIERS: Array<{
  label: string;
  check: (m: Record<string, number>) => boolean;
  score: (m: Record<string, number>) => number;
  weight: number;
}> = [
  {
    label: "Stress térmico (Temp + Hum)",
    check: m => (m.TEMP ?? 0) > 26 && (m.HUM ?? 0) > 55,
    score: m => Math.min(1, ((m.TEMP - 26) / 9 + (m.HUM - 55) / 30) / 2),
    weight: 0.15,
  },
  {
    label: "Ar poluído (CO₂ + Partículas)",
    check: m => (m.CO2 ?? 0) > 500 && (m.PART ?? 0) > 15,
    score: m => Math.min(1, ((m.CO2 - 500) / 700 + (m.PART - 15) / 60) / 2),
    weight: 0.10,
  },
];

interface RiscoFator { label: string; score: number; valor: number; unit: string; }

function calcularRisco(dados: Leitura[]): { score: number; fatores: RiscoFator[] } {
  const grupos = new Map<string, number[]>();
  for (const d of dados) {
    const v = parseFloat(d.valor);
    if (isNaN(v)) continue;
    if (!grupos.has(d.tipoDado)) grupos.set(d.tipoDado, []);
    grupos.get(d.tipoDado)!.push(v);
  }

  // Use max per type so acute spikes (fire, smog) are reflected immediately
  // instead of being washed out by pre-event normal readings.
  const medias: Record<string, number> = {};
  for (const [t, vs] of grupos)
    medias[t] = Math.max(...vs);

  let rawScore = 0;
  const fatores: RiscoFator[] = [];

  for (const tier of RISK_TIERS) {
    const v = medias[tier.tipo];
    if (v === undefined) continue;
    const s = tier.invert
      ? Math.max(0, Math.min(1, (tier.warn - v) / (tier.warn - tier.danger)))
      : Math.max(0, Math.min(1, (v - tier.warn) / (tier.danger - tier.warn)));
    rawScore += s * tier.weight;
    if (s > 0.05)
      fatores.push({ label: tier.label, score: s, valor: v, unit: tier.unit });
  }

  for (const combo of COMBO_TIERS) {
    if (!combo.check(medias)) continue;
    const s = combo.score(medias);
    rawScore = Math.min(1, rawScore + s * combo.weight);
    if (s > 0.05)
      fatores.push({ label: combo.label, score: s, valor: 0, unit: "" });
  }

  return {
    score: Math.min(1, rawScore),
    fatores: fatores.sort((a, b) => b.score - a.score),
  };
}

function riskColor(score: number) {
  if (score >= 0.65) return "var(--danger)";
  if (score >= 0.40) return "var(--warning)";
  if (score >= 0.15) return "var(--success)";
  return "var(--text-3)";
}

function riskLabel(score: number) {
  if (score >= 0.80) return "CRÍTICO";
  if (score >= 0.65) return "ALTO";
  if (score >= 0.40) return "MÉDIO";
  if (score >= 0.15) return "BAIXO";
  return "NORMAL";
}

// ── Chart helpers ─────────────────────────────────────────────────────────────

function fmtIso(d: Date) { return d.toISOString().slice(0, 20); } // keep trailing Z → unambiguous UTC

function agruparPorBucket(
  anomalias: Anomalia[], horas: number, agoraMs: number,
): { label: string; count: number }[] {
  const bucketMs =
    horas <= 6 ? 30 * 60 * 1000 :
    horas <= 12 ?      60 * 60 * 1000 :
    2 * 60 * 60 * 1000;
  const inicioMs = agoraMs - horas * 3600 * 1000;

  const buckets = new Map<number, number>();
  for (let t = inicioMs; t < agoraMs; t += bucketMs)
    buckets.set(t, 0);

  for (const a of anomalias) {
    const ts = new Date(a.timestamp.replace(" ", "T")).getTime();
    if (ts < inicioMs || ts > agoraMs) continue;
    const bucket = Math.floor((ts - inicioMs) / bucketMs) * bucketMs + inicioMs;
    buckets.set(bucket, (buckets.get(bucket) ?? 0) + 1);
  }

  return Array.from(buckets.entries())
    .sort((a, b) => a[0] - b[0])
    .map(([ts, count]) => ({
      label: new Date(ts).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }),
      count,
    }));
}

// ── Component ─────────────────────────────────────────────────────────────────

export default function AnaliseView() {
  const [zona,      setZona]      = useState("");
  const [tipo,      setTipo]      = useState("TEMP");
  const [horas,     setHoras]     = useState<6 | 12 | 24>(6);
  const [zonas,     setZonas]     = useState<string[]>([]);

  const [dados,     setDados]     = useState<Leitura[]>([]);
  const [anomalias, setAnomalias] = useState<Anomalia[]>([]);
  const [riskDados, setRiskDados] = useState<Leitura[]>([]);

  const [loading,     setLoading]     = useState(false);
  const [riskLoading, setRiskLoading] = useState(false);
  const [erro,        setErro]        = useState("");

  // Load zones and auto-select first
  useEffect(() => {
    api.sensores().then(ss => {
      const zs = [...new Set(ss.map(s => s.zona))];
      setZonas(zs);
      if (zs.length > 0) setZona(zs[0]);
    }).catch(() => {});
  }, []);

  // Stats + anomaly chart — affected by zona, tipo, horas
  useEffect(() => {
    if (!zona) return;
    let stale = false;
    setLoading(true);
    setErro("");

    const agora  = new Date();
    const inicio = new Date(agora.getTime() - horas * 3600 * 1000);

    Promise.all([
      api.dadosPeriodo(zona, tipo, fmtIso(inicio), fmtIso(agora)),
      api.anomalias(zona, tipo, 0.8, 500),
    ]).then(([d, a]) => {
      if (stale) return;
      setDados(Array.isArray(d) ? d : []);
      setAnomalias(Array.isArray(a) ? a : []);
      setLoading(false);
    }).catch(() => {
      if (stale) return;
      setErro("Erro ao carregar dados.");
      setLoading(false);
    });

    return () => { stale = true; };
  }, [zona, tipo, horas]);

  // Risk analysis — only affected by zona, polls every 10 s for live updates
  useEffect(() => {
    if (!zona) return;
    let canceled = false;

    const buscar = () =>
      api.dados(zona, "", "", 200)
        .then(d => { if (!canceled) setRiskDados(Array.isArray(d) ? d : []); })
        .catch(() => {});

    setRiskLoading(true);
    buscar().finally(() => { if (!canceled) setRiskLoading(false); });

    const id = setInterval(buscar, 10000);
    return () => { canceled = true; clearInterval(id); };
  }, [zona]);

  // ── Stats ──────────────────────────────────────────────────────────────────
  const valores = dados.map(d => parseFloat(d.valor)).filter(v => !isNaN(v));
  const total   = valores.length;
  const media   = total > 0 ? valores.reduce((a, b) => a + b, 0) / total : null;
  const minimo  = total > 0 ? Math.min(...valores) : null;
  const maximo  = total > 0 ? Math.max(...valores) : null;
  const desvio  = total > 0 && media !== null
    ? Math.sqrt(valores.reduce((acc, v) => acc + (v - media) ** 2, 0) / total)
    : null;
  const alarmes = dados.filter(d => d.isAlarm).length;
  const un      = UNIDADES[tipo] ?? "";

  // ── Risk ───────────────────────────────────────────────────────────────────
  const risco = calcularRisco(riskDados);
  const scoreColor = riskColor(risco.score);
  const scoreLabel = riskLabel(risco.score);
  const scorePct   = Math.round(risco.score * 100);

  // ── Anomaly chart ──────────────────────────────────────────────────────────
  const agoraMs   = Date.now();
  const chartData = agruparPorBucket(anomalias, horas, agoraMs);
  const maxCount  = Math.max(...chartData.map(b => b.count), 1);

  return (
    <div className="an-wrap">

      {/* ── Filters ───────────────────────────────────────────────────────── */}
      <div className="filters-bar">
        <select value={zona} onChange={e => setZona(e.target.value)}>
          {zonas.map(z => <option key={z} value={z}>{z}</option>)}
        </select>
        <select value={tipo} onChange={e => setTipo(e.target.value)}>
          {TIPOS.map(t => <option key={t} value={t}>{t} — {UNIDADES[t]}</option>)}
        </select>
        <select value={horas} onChange={e => setHoras(+e.target.value as 6 | 12 | 24)}>
          {([6, 12, 24] as const).map(h => (
            <option key={h} value={h}>Últimas {h}h</option>
          ))}
        </select>
        {loading && <span className="an-loading">A carregar…</span>}
      </div>

      {erro && <p className="erro">{erro}</p>}

      {/* ── Stats + Risk ──────────────────────────────────────────────────── */}
      <div className="an-row">

        {/* Left — Stats */}
        <div className="card an-card an-stats-card">
          <div className="an-card-title">Estatísticas — {tipo} ({un})</div>
          {total === 0
            ? <p className="vazio">{loading ? "A carregar…" : "Sem leituras no período."}</p>
            : (
              <div className="stat-grid">
                <div className="stat-cell">
                  <span className="stat-cell-label">Leituras</span>
                  <span className="stat-cell-value">{total.toLocaleString()}</span>
                </div>
                <div className="stat-cell">
                  <span className="stat-cell-label">Média</span>
                  <span className="stat-cell-value">{media!.toFixed(2)}{un}</span>
                </div>
                <div className="stat-cell">
                  <span className="stat-cell-label">Mínimo</span>
                  <span className="stat-cell-value">{minimo!.toFixed(2)}{un}</span>
                </div>
                <div className="stat-cell">
                  <span className="stat-cell-label">Máximo</span>
                  <span className="stat-cell-value">{maximo!.toFixed(2)}{un}</span>
                </div>
                <div className="stat-cell">
                  <span className="stat-cell-label">Desvio Padrão</span>
                  <span className="stat-cell-value">{desvio!.toFixed(2)}{un}</span>
                </div>
                <div className="stat-cell">
                  <span className="stat-cell-label">Amplitude</span>
                  <span className="stat-cell-value">{(maximo! - minimo!).toFixed(2)}{un}</span>
                </div>
                <div className="stat-cell">
                  <span className="stat-cell-label">Alarmes</span>
                  <span className="stat-cell-value"
                    style={{ color: alarmes > 0 ? "var(--danger)" : "var(--success)" }}>
                    {alarmes}
                  </span>
                </div>
              </div>
            )}
        </div>

        {/* Right — Risk */}
        <div className="card an-card an-risk-card">
          <div className="an-card-title">
            Risco de Saúde
            <span className="an-card-sub">últimas 200 leituras • {zona}</span>
          </div>

          {riskLoading && riskDados.length === 0
            ? <p className="vazio">A calcular…</p>
            : (
              <>
                {/* Score gauge */}
                <div className="risk-score-row">
                  <span className="risk-pct" style={{ color: scoreColor }}>
                    {scorePct}%
                  </span>
                  <span className="risk-badge" style={{ color: scoreColor, borderColor: scoreColor }}>
                    {scoreLabel}
                  </span>
                </div>
                <div className="risk-track">
                  <div className="risk-fill"
                    style={{ width: `${scorePct}%`, background: scoreColor }} />
                </div>

                {/* Factors */}
                {risco.fatores.length > 0 && (
                  <div className="risk-factors">
                    {risco.fatores.map((f, i) => (
                      <div key={i} className="risk-factor-row">
                        <span className="risk-factor-label">{f.label}</span>
                        <span className="risk-factor-val">
                          {f.unit ? `${f.valor.toFixed(1)}${f.unit}` : ""}
                        </span>
                        <div className="risk-factor-bar-track">
                          <div className="risk-factor-bar-fill"
                            style={{
                              width: `${Math.round(f.score * 100)}%`,
                              background: riskColor(f.score),
                            }} />
                        </div>
                        <span className="risk-factor-pct"
                          style={{ color: riskColor(f.score) }}>
                          {Math.round(f.score * 100)}%
                        </span>
                      </div>
                    ))}
                  </div>
                )}
                {risco.fatores.length === 0 && (
                  <p className="vazio" style={{ marginTop: "0.5rem" }}>
                    Sem factores de risco detectados.
                  </p>
                )}
              </>
            )}
        </div>

      </div>

      {/* ── Anomaly bar chart ────────────────────────────────────────────── */}
      <div className="card an-card an-chart-card">
        <div className="an-card-title">
          Anomalias críticas (score ≥ 80%) — últimas {horas}h
          <span className="an-card-sub">
            {chartData.reduce((s, b) => s + b.count, 0)} total
          </span>
        </div>

        {chartData.every(b => b.count === 0)
          ? <p className="vazio">{loading ? "A carregar…" : "Sem anomalias críticas no período."}</p>
          : (
            <ResponsiveContainer width="100%" height={220}>
              <BarChart data={chartData} margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
                <XAxis
                  dataKey="label"
                  tick={{ fill: "var(--text-3)", fontSize: 11 }}
                  interval={Math.max(0, Math.floor(chartData.length / 8) - 1)}
                />
                <YAxis allowDecimals={false} tick={{ fill: "var(--text-3)", fontSize: 11 }} width={28} />
                <Tooltip
                  contentStyle={{
                    background: "var(--surface)", border: "1px solid var(--border-2)",
                    borderRadius: "8px", color: "var(--text)", fontSize: "0.85rem",
                  }}
                  formatter={(v: number) => [v, "Anomalias"]}
                />
                <Bar dataKey="count" radius={[4, 4, 0, 0]}>
                  {chartData.map((entry, i) => (
                    <Cell key={i}
                      fill={
                        entry.count === 0             ? "var(--border)"   :
                        entry.count >= maxCount * 0.7 ? "var(--danger)"   :
                                                        "var(--warning)"
                      }
                    />
                  ))}
                </Bar>
              </BarChart>
            </ResponsiveContainer>
          )}
      </div>

    </div>
  );
}
