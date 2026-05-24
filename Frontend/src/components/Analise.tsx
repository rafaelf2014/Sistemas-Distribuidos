import { useEffect, useState } from "react";
import {
  AreaChart, Area, XAxis, YAxis, CartesianGrid,
  Tooltip, ResponsiveContainer,
} from "recharts";
import { api } from "../api";
import type { Analise, Padroes, Previsao } from "../api";
import "./Analise.css";

const TIPOS = ["TEMP", "HUM", "CO2", "RUIDO", "LUMIN", "PART", "NO2", "O3", "WIND"];
const UNIDADES: Record<string, string> = {
  TEMP: "°C", HUM: "%", CO2: "ppm", RUIDO: "dB", LUMIN: "lux", PART: "µg/m³",
  NO2: "µg/m³", O3: "ppb", WIND: "km/h",
};

function RiscoGauge({ valor }: { valor: number }) {
  const pct   = Math.round(valor * 100);
  const color =
    valor >= 0.8 ? "var(--danger)"  :
    valor >= 0.5 ? "var(--warning)" :
    valor >= 0.2 ? "var(--success)" : "var(--text-3)";
  const label =
    valor >= 0.8 ? "ALTO"   :
    valor >= 0.5 ? "MÉDIO"  :
    valor >= 0.2 ? "BAIXO"  : "NORMAL";

  return (
    <div className="gauge-wrap">
      <div className="gauge-track">
        <div className="gauge-fill" style={{ width: `${pct}%`, background: color }} />
      </div>
      <div className="gauge-labels">
        <span className="gauge-pct" style={{ color }}>{pct}%</span>
        <span className="gauge-level" style={{ color }}>{label}</span>
      </div>
    </div>
  );
}

function ConfiancaDot({ v }: { v: number }) {
  const color =
    v >= 0.7 ? "var(--success)" :
    v >= 0.4 ? "var(--warning)" : "var(--danger)";
  return (
    <span className="conf-dot-wrap">
      <span className="conf-dot" style={{ background: color }} />
      <span style={{ color }}>{Math.round(v * 100)}%</span>
    </span>
  );
}

export default function AnaliseView() {
  const [zona,     setZona]     = useState("");
  const [tipo,     setTipo]     = useState("TEMP");
  const [horas,    setHoras]    = useState(6);
  const [zonas,    setZonas]    = useState<string[]>([]);
  const [analise,  setAnalise]  = useState<Analise | null>(null);
  const [padroes,  setPadroes]  = useState<Padroes | null>(null);
  const [previsao, setPrevisao] = useState<Previsao | null>(null);
  const [erro,     setErro]     = useState("");
  const [loading,  setLoading]  = useState(false);

  useEffect(() => {
    api.sensores().then(ss => setZonas([...new Set(ss.map(s => s.zona))])).catch(() => {});
  }, []);

  const carregar = async () => {
    setLoading(true);
    setErro("");
    try {
      const [a, p, pr] = await Promise.all([
        api.analise(zona, tipo),
        api.padroes(zona, tipo),
        api.previsao(zona, tipo, horas),
      ]);
      setAnalise(a); setPadroes(p); setPrevisao(pr);
    } catch {
      setErro("Sem dados ou ServicoAnalise indisponível.");
      setAnalise(null); setPadroes(null); setPrevisao(null);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { carregar(); }, [zona, tipo, horas]);

  const un = UNIDADES[tipo] ?? "";

  const previsaoData = previsao?.valoresPrevistos.map((v, i) => ({
    hora: `+${i + 1}h`,
    valor: parseFloat(v.toFixed(2)),
  })) ?? [];

  return (
    <div className="an-wrap">
      {/* Filters */}
      <div className="filters-bar">
        <select value={zona}  onChange={e => setZona(e.target.value)}>
          <option value="">Todas as zonas</option>
          {zonas.map(z => <option key={z}>{z}</option>)}
        </select>
        <select value={tipo}  onChange={e => setTipo(e.target.value)}>
          {TIPOS.map(t => <option key={t}>{t} — {UNIDADES[t]}</option>)}
        </select>
        <select value={horas} onChange={e => setHoras(+e.target.value)}>
          {[3, 6, 12, 24].map(h => <option key={h} value={h}>Previsão {h}h</option>)}
        </select>
        <button className="btn" onClick={carregar} disabled={loading}>
          {loading ? "A carregar..." : "Analisar"}
        </button>
      </div>

      {erro && <p className="erro">{erro}</p>}

      {analise && (
        <div className="an-grid">

          {/* Stats card */}
          <div className="card an-card an-stats-card">
            <div className="an-card-title">Estatísticas — {tipo}</div>
            <div className="stat-grid">
              <div className="stat-cell">
                <span className="stat-cell-label">Leituras</span>
                <span className="stat-cell-value">{analise.totalLeituras.toLocaleString()}</span>
              </div>
              <div className="stat-cell">
                <span className="stat-cell-label">Alarmes</span>
                <span className="stat-cell-value" style={{ color: analise.totalAlarmes > 0 ? "var(--danger)" : "var(--success)" }}>
                  {analise.totalAlarmes}
                </span>
              </div>
              <div className="stat-cell">
                <span className="stat-cell-label">Média</span>
                <span className="stat-cell-value">{analise.media.toFixed(2)}{un}</span>
              </div>
              <div className="stat-cell">
                <span className="stat-cell-label">Desvio Padrão</span>
                <span className="stat-cell-value">{analise.desvioPadrao.toFixed(2)}{un}</span>
              </div>
              <div className="stat-cell">
                <span className="stat-cell-label">Mínimo</span>
                <span className="stat-cell-value">{analise.minimo.toFixed(2)}{un}</span>
              </div>
              <div className="stat-cell">
                <span className="stat-cell-label">Máximo</span>
                <span className="stat-cell-value">{analise.maximo.toFixed(2)}{un}</span>
              </div>
            </div>
          </div>

          {/* Risk card */}
          {previsao && (
            <div className="card an-card an-risk-card">
              <div className="an-card-title">Risco de Saúde Previsto</div>
              <RiscoGauge valor={previsao.riscoSaude} />
              <p className="an-recomendacao">{previsao.recomendacao}</p>
            </div>
          )}

          {/* Patterns card */}
          {padroes && padroes.padroes.length > 0 && (
            <div className="card an-card an-patterns-card">
              <div className="an-card-title">Padrões Detectados</div>
              <div className="patterns-list">
                {padroes.padroes.map((p, i) => (
                  <div key={i} className="pattern-row">
                    <div className="pattern-left">
                      <ConfiancaDot v={p.confianca} />
                      <span className="pattern-desc">{p.descricao}</span>
                    </div>
                    {p.horaPico && (
                      <span className="badge badge-purple pattern-hora">{p.horaPico}</span>
                    )}
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Forecast chart */}
          {previsaoData.length > 0 && (
            <div className="card an-card an-forecast-card">
              <div className="an-card-title">
                Previsão — próximas {horas}h
                <span className="an-card-sub">{un}</span>
              </div>
              <ResponsiveContainer width="100%" height={220}>
                <AreaChart data={previsaoData} margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
                  <defs>
                    <linearGradient id="areaGrad" x1="0" y1="0" x2="0" y2="1">
                      <stop offset="5%"  stopColor="var(--accent)" stopOpacity={0.2} />
                      <stop offset="95%" stopColor="var(--accent)" stopOpacity={0} />
                    </linearGradient>
                  </defs>
                  <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
                  <XAxis dataKey="hora" tick={{ fill: "var(--text-3)", fontSize: 12 }} />
                  <YAxis tick={{ fill: "var(--text-3)", fontSize: 12 }} unit={un} width={62} />
                  <Tooltip
                    contentStyle={{
                      background: "var(--surface)",
                      border: "1px solid var(--border-2)",
                      borderRadius: "8px",
                      color: "var(--text)",
                      fontSize: "0.85rem",
                    }}
                    formatter={(v) => [`${v}${un}`, "Previsto"]}
                  />
                  <Area
                    type="monotone"
                    dataKey="valor"
                    stroke="var(--accent)"
                    strokeWidth={2}
                    fill="url(#areaGrad)"
                    isAnimationActive={false}
                  />
                </AreaChart>
              </ResponsiveContainer>
            </div>
          )}

        </div>
      )}

      {!analise && !erro && !loading && (
        <p className="vazio">Seleccione uma zona e tipo de dado para iniciar a análise.</p>
      )}
    </div>
  );
}
