import { useEffect, useState } from "react";
import { api } from "../api";
import type { Alarme } from "../api";
import "./AlarmLog.css";

const TIPOS = ["TEMP", "HUM", "CO2", "RUIDO", "LUMIN", "PART", "NO2", "O3", "WIND"];
const UNIDADES: Record<string, string> = {
  TEMP: "°C", HUM: "%", CO2: "ppm", RUIDO: "dB", LUMIN: "lux", PART: "µg/m³",
  NO2: "µg/m³", O3: "ppb", WIND: "km/h",
};
const TIPO_COLOR: Record<string, string> = {
  TEMP:  "badge-red",
  CO2:   "badge-purple",
  RUIDO: "badge-amber",
  HUM:   "badge-blue",
  LUMIN: "badge-amber",
  PART:  "badge-red",
  NO2:   "badge-purple",
  O3:    "badge-blue",
  WIND:  "badge-blue",
};

export default function AlarmLog() {
  const [alarmes, setAlarmes] = useState<Alarme[]>([]);
  const [zona,    setZona]    = useState("");
  const [tipo,    setTipo]    = useState("");
  const [limite,  setLimite]  = useState(100);
  const [zonas,   setZonas]   = useState<string[]>([]);
  const [erro,    setErro]    = useState("");

  useEffect(() => {
    api.sensores().then(ss => setZonas([...new Set(ss.map(s => s.zona))])).catch(() => {});
  }, []);

  const carregar = () =>
    api.alarmes(zona, tipo, limite)
      .then(a => { setAlarmes(a); setErro(""); })
      .catch(() => setErro("Servidor indisponível."));

  useEffect(() => {
    carregar();
    const id = setInterval(carregar, 5000);
    return () => clearInterval(id);
  }, [zona, tipo, limite]);

  // Count by tipo for summary chips
  const porTipo = TIPOS.reduce<Record<string, number>>((acc, t) => {
    acc[t] = alarmes.filter(a => a.tipoDado === t).length;
    return acc;
  }, {});

  return (
    <div className="al-wrap">
      {/* Filters */}
      <div className="filters-bar">
        <select value={zona}   onChange={e => setZona(e.target.value)}>
          <option value="">Todas as zonas</option>
          {zonas.map(z => <option key={z}>{z}</option>)}
        </select>
        <select value={tipo}   onChange={e => setTipo(e.target.value)}>
          <option value="">Todos os tipos</option>
          {TIPOS.map(t => <option key={t}>{t}</option>)}
        </select>
        <select value={limite} onChange={e => setLimite(+e.target.value)}>
          {[50, 100, 200, 500].map(l => <option key={l} value={l}>{l} registos</option>)}
        </select>
        <button className="btn" onClick={carregar}>Atualizar</button>
        <span className="al-total">{alarmes.length} alarme{alarmes.length !== 1 ? "s" : ""}</span>
      </div>

      {/* Breakdown chips */}
      {alarmes.length > 0 && (
        <div className="al-breakdown">
          {TIPOS.filter(t => porTipo[t] > 0).map(t => (
            <button
              key={t}
              className={`al-chip badge ${TIPO_COLOR[t] ?? "badge-gray"} ${tipo === t ? "al-chip-active" : ""}`}
              onClick={() => setTipo(tipo === t ? "" : t)}
            >
              {t} {porTipo[t]}
            </button>
          ))}
        </div>
      )}

      {erro && <p className="erro">{erro}</p>}
      {alarmes.length === 0 && !erro && <p className="vazio">Sem alarmes para os filtros seleccionados.</p>}

      {alarmes.length > 0 && (
        <div className="card al-table-card">
          <table className="al-table">
            <thead>
              <tr>
                <th>Timestamp</th>
                <th>Sensor</th>
                <th>Zona</th>
                <th>Tipo</th>
                <th>Valor</th>
                <th>Gateway</th>
              </tr>
            </thead>
            <tbody>
              {alarmes.map((a, i) => {
                const un = UNIDADES[a.tipoDado] ?? "";
                return (
                  <tr key={i} className={`al-row tipo-${a.tipoDado}`}>
                    <td className="al-ts">{a.timestamp.replace("T", " ")}</td>
                    <td><span className="badge badge-blue">{a.sensorId}</span></td>
                    <td className="al-zona">{a.zona}</td>
                    <td><span className={`badge ${TIPO_COLOR[a.tipoDado] ?? "badge-gray"}`}>{a.tipoDado}</span></td>
                    <td className="al-valor">{a.valor}{un}</td>
                    <td className="al-gw">{a.gatewayId}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
