import { useEffect, useState } from "react";
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid,
  Tooltip, ResponsiveContainer, ReferenceLine,
} from "recharts";
import { api } from "../api";
import type { Leitura } from "../api";
import "./DataChart.css";

const TIPOS = ["TEMP", "HUM", "CO2", "RUIDO", "LUMIN", "PART", "NO2", "O3", "WIND"];
const UNIDADES: Record<string, string> = {
  TEMP: "°C", HUM: "%", CO2: "ppm", RUIDO: "dB", LUMIN: "lux", PART: "µg/m³",
  NO2: "µg/m³", O3: "ppb", WIND: "km/h",
};

interface Ponto { ts: string; valor: number; alarme: boolean; qualidade: number; }

function qualColor(q: number): string {
  if (q >= 0.9) return "var(--success)";
  if (q >= 0.6) return "var(--warning)";
  return "var(--danger)";
}

export default function DataChart() {
  const [zona,    setZona]    = useState("");
  const [tipo,    setTipo]    = useState("TEMP");
  const [sensor,  setSensor]  = useState("");
  const [limite,  setLimite]  = useState(150);
  const [dados,   setDados]   = useState<Ponto[]>([]);
  const [zonas,   setZonas]   = useState<string[]>([]);
  const [sensores,setSensores]= useState<string[]>([]);
  const [erro,    setErro]    = useState("");

  const carregar = () =>
    api.dados(zona, tipo, sensor, limite)
      .then((raw: Leitura[]) => {
        const pts = [...raw].reverse().map(r => ({
          ts:       r.timestamp.slice(5, 16).replace("T", " "),
          valor:    parseFloat(r.valor),
          alarme:   r.isAlarm,
          qualidade: r.qualidade ?? 1,
        }));
        setDados(pts);
        setErro("");
      })
      .catch(() => setErro("Sem dados ou servidor indisponível."));

  useEffect(() => {
    api.sensores().then(ss => {
      setZonas([...new Set(ss.map(s => s.zona))]);
      setSensores(ss.map(s => s.sensorId));
    }).catch(() => {});
  }, []);

  useEffect(() => {
    carregar();
    const id = setInterval(carregar, 5000);
    return () => clearInterval(id);
  }, [zona, tipo, sensor, limite]);

  const un         = UNIDADES[tipo] ?? "";
  const alarmePts  = dados.filter(d => d.alarme);
  const valores    = dados.map(d => d.valor);
  const media      = valores.length ? valores.reduce((a, b) => a + b, 0) / valores.length : 0;
  const qualMedia  = dados.length   ? dados.reduce((a, b) => a + b.qualidade, 0) / dados.length : 1;

  return (
    <div className="dc-wrap">
      {/* Filters */}
      <div className="filters-bar">
        <select value={zona}   onChange={e => setZona(e.target.value)}>
          <option value="">Todas as zonas</option>
          {zonas.map(z => <option key={z}>{z}</option>)}
        </select>
        <select value={tipo}   onChange={e => setTipo(e.target.value)}>
          {TIPOS.map(t => <option key={t}>{t}</option>)}
        </select>
        <select value={sensor} onChange={e => setSensor(e.target.value)}>
          <option value="">Todos os sensores</option>
          {sensores.map(s => <option key={s}>{s}</option>)}
        </select>
        <select value={limite} onChange={e => setLimite(+e.target.value)}>
          {[50, 100, 150, 300, 500].map(l => <option key={l} value={l}>{l} leituras</option>)}
        </select>
        <button className="btn" onClick={carregar}>Atualizar</button>
      </div>

      {erro && <p className="erro">{erro}</p>}

      {dados.length > 0 && (
        <>
          {/* Stats strip */}
          <div className="dc-stats card">
            <div className="dc-stat">
              <span className="dc-stat-label">Leituras</span>
              <span className="dc-stat-value">{dados.length}</span>
            </div>
            <div className="dc-stat-divider" />
            <div className="dc-stat">
              <span className="dc-stat-label">Mínimo</span>
              <span className="dc-stat-value">{Math.min(...valores).toFixed(1)}{un}</span>
            </div>
            <div className="dc-stat-divider" />
            <div className="dc-stat">
              <span className="dc-stat-label">Máximo</span>
              <span className="dc-stat-value">{Math.max(...valores).toFixed(1)}{un}</span>
            </div>
            <div className="dc-stat-divider" />
            <div className="dc-stat">
              <span className="dc-stat-label">Média</span>
              <span className="dc-stat-value">{media.toFixed(1)}{un}</span>
            </div>
            <div className="dc-stat-divider" />
            <div className="dc-stat">
              <span className="dc-stat-label">Qualidade</span>
              <span className="dc-stat-value" style={{ color: qualColor(qualMedia) }}>
                {Math.round(qualMedia * 100)}%
              </span>
            </div>
            {alarmePts.length > 0 && (
              <>
                <div className="dc-stat-divider" />
                <div className="dc-stat">
                  <span className="dc-stat-label">Alarmes</span>
                  <span className="dc-stat-value" style={{ color: "var(--danger)" }}>
                    ⚠ {alarmePts.length}
                  </span>
                </div>
              </>
            )}
          </div>

          {/* Chart */}
          <div className="card dc-chart-card">
            <div className="dc-chart-header">
              <span className="dc-chart-title">{tipo} <span className="dc-chart-unit">({un})</span></span>
              {zona   && <span className="badge badge-blue">{zona}</span>}
              {sensor && <span className="badge badge-purple">{sensor}</span>}
            </div>
            <ResponsiveContainer width="100%" height={300}>
              <LineChart data={dados} margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
                <XAxis
                  dataKey="ts"
                  tick={{ fill: "var(--text-3)", fontSize: 11 }}
                  interval={Math.max(1, Math.floor(dados.length / 8))}
                />
                <YAxis
                  tick={{ fill: "var(--text-3)", fontSize: 11 }}
                  unit={un}
                  width={62}
                />
                <Tooltip
                  contentStyle={{
                    background: "var(--surface)",
                    border: "1px solid var(--border-2)",
                    borderRadius: "8px",
                    color: "var(--text)",
                    fontSize: "0.85rem",
                  }}
                  formatter={(v, _, entry) => [
                    `${Number(v).toFixed(2)}${un}  (qual. ${Math.round(((entry.payload as Ponto)?.qualidade ?? 1) * 100)}%)`,
                    tipo,
                  ]}
                />
                {alarmePts.map((p, i) => (
                  <ReferenceLine key={i} x={p.ts} stroke="var(--danger)" strokeDasharray="4 2" strokeOpacity={0.6} />
                ))}
                <Line
                  type="monotone"
                  dataKey="valor"
                  stroke="var(--accent)"
                  strokeWidth={2}
                  dot={false}
                  isAnimationActive={false}
                />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </>
      )}

      {dados.length === 0 && !erro && (
        <p className="vazio">Sem leituras para os filtros seleccionados.</p>
      )}
    </div>
  );
}
