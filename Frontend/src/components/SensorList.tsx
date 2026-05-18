import { useEffect, useState } from "react";
import { api } from "../api";
import type { Sensor } from "../api";
import "./SensorList.css";

const BASE = "http://localhost:8080";

function tempoRelativo(iso: string): string {
  if (!iso) return "";
  const diff = (Date.now() - new Date(iso.replace(" ", "T")).getTime()) / 1000;
  if (diff < 60)  return `há ${Math.round(diff)}s`;
  if (diff < 3600) return `há ${Math.round(diff / 60)}min`;
  return `há ${Math.round(diff / 3600)}h`;
}

export default function SensorList() {
  const [sensores,    setSensores]    = useState<Sensor[]>([]);
  const [apenasAtivos, setApenasAtivos] = useState(false);
  const [streamAtivo, setStreamAtivo] = useState<string | null>(null);
  const [erro,        setErro]        = useState("");

  const carregar = () =>
    api.sensores()
      .then(setSensores)
      .catch(() => setErro("Servidor indisponível."));

  useEffect(() => {
    carregar();
    const id = setInterval(carregar, 5000);
    return () => clearInterval(id);
  }, []);

  const toggleStream = async (s: Sensor) => {
    if (streamAtivo === s.sensorId) {
      await fetch(`${BASE}/api/stream/stop?sensor=${s.sensorId}`);
      setStreamAtivo(null);
    } else {
      if (streamAtivo) await fetch(`${BASE}/api/stream/stop?sensor=${streamAtivo}`);
      const res  = await fetch(`${BASE}/api/stream/start?sensor=${s.sensorId}`);
      const json = await res.json();
      if (json.erro) { alert(`Erro stream: ${json.erro}`); return; }
      setStreamAtivo(s.sensorId);
    }
  };

  const lista = apenasAtivos
    ? sensores.filter(s => s.status === "ativo")
    : sensores;

  if (erro) return <p className="erro">{erro}</p>;

  return (
    <div>
      <div className="sensor-toolbar">
        <span className="sensor-count">{lista.length} sensor(es)</span>
        <button
          className={`toggle-btn ${apenasAtivos ? "active" : ""}`}
          onClick={() => setApenasAtivos(v => !v)}
        >
          {apenasAtivos ? "A mostrar: Online" : "A mostrar: Todos"}
        </button>
      </div>

      <div className="sensor-grid">
        {lista.length === 0 && <p className="vazio">Sem sensores registados.</p>}
        {lista.map(s => (
          <div key={s.sensorId} className={`sensor-card ${s.status}`}>

            {/* Header: ID + badges alinhados, alarmes no canto direito */}
            <div className="sensor-header">
              <span className="sensor-id">{s.sensorId}</span>
              {s.videoStream && (
                <span
                  className={`badge video ${streamAtivo === s.sensorId ? "streaming" : ""}`}
                  title={streamAtivo === s.sensorId ? "Parar stream" : "Iniciar stream"}
                  onClick={() => toggleStream(s)}
                >
                  {streamAtivo === s.sensorId ? "◼ LIVE" : "▶ VIDEO"}
                </span>
              )}
              <span className="spacer" />
              {s.totalAlarmes > 0 && (
                <span className="badge alarme">⚠ {s.totalAlarmes}</span>
              )}
            </div>

            <div className="sensor-zona">{s.zona}</div>

            <div className="sensor-tipos">
              {s.tipos.split(",").map(t => (
                <span key={t} className="badge tipo">{t.trim()}</span>
              ))}
            </div>

            <div className="sensor-footer">
              <span className={`estado ${s.status}`}>
                {s.status === "ativo"        ? "ATIVO"        :
                 s.status === "manutencao"   ? "MANUTENÇÃO"   :
                 s.status === "desativado"   ? "DESATIVADO"   : "DESCONHECIDO"}
              </span>
              {s.ultimaLeitura && (
                <span className="ultima">{tempoRelativo(s.ultimaLeitura)}</span>
              )}
            </div>

          </div>
        ))}
      </div>
    </div>
  );
}
