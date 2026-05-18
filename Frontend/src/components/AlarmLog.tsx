import { useEffect, useState } from "react";
import { api } from "../api";
import type { Alarme } from "../api";
import "./AlarmLog.css";

const UNIDADES: Record<string, string> = {
  TEMP: "°C", HUM: "%", CO2: "ppm", RUIDO: "dB", LUMIN: "lux", PART: "µg/m³"
};

export default function AlarmLog() {
  const [alarmes, setAlarmes] = useState<Alarme[]>([]);
  const [zona,    setZona]    = useState("");
  const [tipo,    setTipo]    = useState("");
  const [zonas,   setZonas]   = useState<string[]>([]);
  const [erro,    setErro]    = useState("");

  useEffect(() => {
    api.sensores().then(ss => {
      setZonas([...new Set(ss.map(s => s.zona))]);
    }).catch(() => {});
  }, []);

  const carregar = () =>
    api.alarmes(zona, tipo, 100)
      .then(a => { setAlarmes(a); setErro(""); })
      .catch(() => setErro("Servidor indisponível."));

  useEffect(() => {
    carregar();
    const id = setInterval(carregar, 5000);
    return () => clearInterval(id);
  }, [zona, tipo]);

  return (
    <div className="alarm-wrap">
      <div className="alarm-filters">
        <select value={zona} onChange={e => setZona(e.target.value)}>
          <option value="">Todas as zonas</option>
          {zonas.map(z => <option key={z}>{z}</option>)}
        </select>
        <select value={tipo} onChange={e => setTipo(e.target.value)}>
          <option value="">Todos os tipos</option>
          {["TEMP","HUM","CO2","RUIDO","LUMIN","PART"].map(t => <option key={t}>{t}</option>)}
        </select>
        <button onClick={carregar}>Atualizar</button>
        <span className="alarm-count">{alarmes.length} alarme(s)</span>
      </div>

      {erro && <p className="erro">{erro}</p>}

      {alarmes.length === 0 && !erro && (
        <p className="vazio">Sem alarmes para os filtros seleccionados.</p>
      )}

      {alarmes.length > 0 && (
        <table className="alarm-table">
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
                <tr key={i}>
                  <td>{a.timestamp.replace("T", " ")}</td>
                  <td><span className="sensor-tag">{a.sensorId}</span></td>
                  <td>{a.zona}</td>
                  <td><span className="tipo-tag">{a.tipoDado}</span></td>
                  <td className="valor-alarme">{a.valor}{un}</td>
                  <td className="gw">{a.gatewayId}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}
