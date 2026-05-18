import { useEffect, useState } from "react";
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid,
  Tooltip, ResponsiveContainer, ReferenceLine
} from "recharts";
import { api } from "../api";
import type { Leitura } from "../api";
import "./DataChart.css";

const TIPOS = ["TEMP", "HUM", "CO2", "RUIDO", "LUMIN", "PART"];
const UNIDADES: Record<string, string> = {
  TEMP: "°C", HUM: "%", CO2: "ppm", RUIDO: "dB", LUMIN: "lux", PART: "µg/m³"
};

interface Ponto { ts: string; valor: number; alarme: boolean; }

export default function DataChart() {
  const [zona,    setZona]    = useState("");
  const [tipo,    setTipo]    = useState("TEMP");
  const [sensor,  setSensor]  = useState("");
  const [dados,   setDados]   = useState<Ponto[]>([]);
  const [zonas,   setZonas]   = useState<string[]>([]);
  const [sensores,setSensores]= useState<string[]>([]);
  const [erro,    setErro]    = useState("");

  const carregar = () =>
    api.dados(zona, tipo, sensor, 150)
      .then((raw: Leitura[]) => {
        const pts = [...raw].reverse().map(r => ({
          ts:     r.timestamp.replace("T", " ").slice(5, 16),
          valor:  parseFloat(r.valor),
          alarme: r.isAlarm,
        }));
        setDados(pts);
        setErro("");
      })
      .catch(() => setErro("Sem dados ou servidor indisponível."));

  useEffect(() => {
    api.sensores().then(ss => {
      const zs = [...new Set(ss.map(s => s.zona))];
      const ids = ss.map(s => s.sensorId);
      setZonas(zs);
      setSensores(ids);
    }).catch(() => {});
  }, []);

  useEffect(() => {
    carregar();
    const id = setInterval(carregar, 5000);
    return () => clearInterval(id);
  }, [zona, tipo, sensor]);

  const unidade = UNIDADES[tipo] ?? "";
  const alarmePts = dados.filter(d => d.alarme);

  return (
    <div className="chart-wrap">
      <div className="chart-filters">
        <select value={zona} onChange={e => setZona(e.target.value)}>
          <option value="">Todas as zonas</option>
          {zonas.map(z => <option key={z}>{z}</option>)}
        </select>
        <select value={tipo} onChange={e => setTipo(e.target.value)}>
          {TIPOS.map(t => <option key={t}>{t}</option>)}
        </select>
        <select value={sensor} onChange={e => setSensor(e.target.value)}>
          <option value="">Todos os sensores</option>
          {sensores.map(s => <option key={s}>{s}</option>)}
        </select>
        <button onClick={carregar}>Atualizar</button>
      </div>

      {erro && <p className="erro">{erro}</p>}

      {dados.length > 0 && (
        <>
          <div className="chart-stats">
            <span>Leituras: <b>{dados.length}</b></span>
            <span>Mín: <b>{Math.min(...dados.map(d => d.valor)).toFixed(1)}{unidade}</b></span>
            <span>Máx: <b>{Math.max(...dados.map(d => d.valor)).toFixed(1)}{unidade}</b></span>
            <span>Média: <b>{(dados.reduce((s,d) => s+d.valor,0)/dados.length).toFixed(1)}{unidade}</b></span>
            {alarmePts.length > 0 && <span className="stat-alarme">⚠ {alarmePts.length} alarme(s)</span>}
          </div>

          <ResponsiveContainer width="100%" height={320}>
            <LineChart data={dados} margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="#2a2a4a" />
              <XAxis dataKey="ts" tick={{ fill: "#666", fontSize: 11 }}
                interval={Math.floor(dados.length / 8)} />
              <YAxis tick={{ fill: "#666", fontSize: 11 }}
                unit={unidade} width={60} />
              <Tooltip
                contentStyle={{ background: "#1a1a2e", border: "1px solid #2a2a4a", color: "#eee" }}
                formatter={(v) => [`${v}${unidade}`, tipo]}
              />
              {alarmePts.map((p, i) => (
                <ReferenceLine key={i} x={p.ts} stroke="#ff5252" strokeDasharray="4 2" />
              ))}
              <Line type="monotone" dataKey="valor" stroke="#00d4ff"
                dot={false} strokeWidth={2} isAnimationActive={false} />
            </LineChart>
          </ResponsiveContainer>
        </>
      )}

      {dados.length === 0 && !erro && <p className="vazio">Sem leituras para os filtros seleccionados.</p>}
    </div>
  );
}
