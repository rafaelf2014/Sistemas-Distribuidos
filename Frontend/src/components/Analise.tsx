import { useEffect, useState } from "react";
import {
  BarChart, Bar, XAxis, YAxis, CartesianGrid,
  Tooltip, ResponsiveContainer
} from "recharts";
import { api } from "../api";
import type { Analise, Padroes, Previsao } from "../api";
import "./Analise.css";

const TIPOS = ["TEMP", "HUM", "CO2", "RUIDO", "LUMIN", "PART"];
const UNIDADES: Record<string, string> = {
  TEMP: "°C", HUM: "%", CO2: "ppm", RUIDO: "dB", LUMIN: "lux", PART: "µg/m³"
};

function RiscoBar({ valor }: { valor: number }) {
  const pct   = Math.round(valor * 100);
  const color = valor >= 0.8 ? "#ff5252" : valor >= 0.5 ? "#ffb300" : valor >= 0.2 ? "#69f0ae" : "#444";
  return (
    <div className="risco-bar-wrap">
      <div className="risco-bar" style={{ width: `${pct}%`, background: color }} />
      <span className="risco-label" style={{ color }}>{pct}%</span>
    </div>
  );
}

export default function Analise() {
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
    api.sensores().then(ss => {
      setZonas([...new Set(ss.map(s => s.zona))]);
    }).catch(() => {});
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
      setAnalise(a);
      setPadroes(p);
      setPrevisao(pr);
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
    <div className="analise-wrap">
      <div className="analise-filters">
        <select value={zona} onChange={e => setZona(e.target.value)}>
          <option value="">Todas as zonas</option>
          {zonas.map(z => <option key={z}>{z}</option>)}
        </select>
        <select value={tipo} onChange={e => setTipo(e.target.value)}>
          {TIPOS.map(t => <option key={t}>{t}</option>)}
        </select>
        <select value={horas} onChange={e => setHoras(+e.target.value)}>
          {[3, 6, 12, 24].map(h => <option key={h} value={h}>{h}h previsão</option>)}
        </select>
        <button onClick={carregar} disabled={loading}>
          {loading ? "A carregar..." : "Analisar"}
        </button>
      </div>

      {erro && <p className="erro">{erro}</p>}

      {analise && (
        <div className="analise-grid">

          {/* Estatísticas */}
          <div className="card">
            <h3>Estatísticas</h3>
            <div className="stat-row"><span>Leituras</span><b>{analise.totalLeituras}</b></div>
            <div className="stat-row"><span>Alarmes</span>
              <b className={analise.totalAlarmes > 0 ? "alarme" : ""}>{analise.totalAlarmes}</b>
            </div>
            <div className="stat-row"><span>Média</span>   <b>{analise.media.toFixed(2)}{un}</b></div>
            <div className="stat-row"><span>Desvio</span>  <b>{analise.desvioPadrao.toFixed(2)}{un}</b></div>
            <div className="stat-row"><span>Mínimo</span>  <b>{analise.minimo.toFixed(2)}{un}</b></div>
            <div className="stat-row"><span>Máximo</span>  <b>{analise.maximo.toFixed(2)}{un}</b></div>
          </div>

          {/* Risco */}
          {previsao && (
            <div className="card">
              <h3>Risco de Saúde</h3>
              <RiscoBar valor={previsao.riscoSaude} />
              <p className="recomendacao">{previsao.recomendacao}</p>
            </div>
          )}

          {/* Padrões */}
          {padroes && padroes.padroes.length > 0 && (
            <div className="card card-full">
              <h3>Padrões Detectados</h3>
              <div className="padroes-list">
                {padroes.padroes.map((p, i) => (
                  <div key={i} className="padrao-item">
                    <span className="padrao-desc">{p.descricao}</span>
                    <span className="padrao-conf">{Math.round(p.confianca * 100)}%</span>
                    {p.horaPico && <span className="padrao-hora">{p.horaPico}</span>}
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Previsão */}
          {previsaoData.length > 0 && (
            <div className="card card-full">
              <h3>Previsão — próximas {horas}h ({un})</h3>
              <ResponsiveContainer width="100%" height={220}>
                <BarChart data={previsaoData} margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="#2a2a4a" />
                  <XAxis dataKey="hora" tick={{ fill: "#666", fontSize: 12 }} />
                  <YAxis tick={{ fill: "#666", fontSize: 12 }} unit={un} width={60} />
                  <Tooltip
                    contentStyle={{ background: "#1a1a2e", border: "1px solid #2a2a4a", color: "#eee" }}
                    formatter={(v) => [`${v}${un}`, "Previsto"]}
                  />
                  <Bar dataKey="valor" fill="#7c4dff" radius={[4, 4, 0, 0]} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          )}

        </div>
      )}
    </div>
  );
}
