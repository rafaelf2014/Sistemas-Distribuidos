import { useEffect, useState } from "react";
import { api } from "../api";
import type { Sensor } from "../api";
import "./SensorList.css";

export default function SensorList() {
  const [sensores, setSensores] = useState<Sensor[]>([]);
  const [erro, setErro]         = useState("");

  const carregar = () =>
    api.sensores()
      .then(setSensores)
      .catch(() => setErro("Servidor indisponível."));

  useEffect(() => {
    carregar();
    const id = setInterval(carregar, 5000);
    return () => clearInterval(id);
  }, []);

  if (erro) return <p className="erro">{erro}</p>;

  return (
    <div className="sensor-grid">
      {sensores.length === 0 && <p className="vazio">Sem sensores registados.</p>}
      {sensores.map(s => (
        <div key={s.sensorId} className="sensor-card">
          <div className="sensor-header">
            <span className="sensor-id">{s.sensorId}</span>
            {s.videoStream && <span className="badge video">VIDEO</span>}
          </div>
          <div className="sensor-zona">{s.zona}</div>
          <div className="sensor-tipos">
            {s.tipos.split(",").map(t => (
              <span key={t} className="badge tipo">{t.trim()}</span>
            ))}
          </div>
          <div className="sensor-footer">
            <span className={`estado ${s.status}`}>
              {s.status === "ativo"        ? "ATIVO"       :
               s.status === "manutencao"   ? "MANUTENÇÃO"  :
               s.status === "desativado"   ? "DESATIVADO"  : "DESCONHECIDO"}
            </span>
            {s.ultimaLeitura && (
              <span className="ultima">{s.ultimaLeitura.replace("T", " ")}</span>
            )}
          </div>
          {s.totalAlarmes > 0 && (
            <div className="sensor-alarmes">⚠ {s.totalAlarmes} alarme(s)</div>
          )}
        </div>
      ))}
    </div>
  );
}
