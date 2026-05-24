import { useEffect, useState } from "react";
import { api } from "../api";
import type { Sensor } from "../api";
import "./SensorList.css";

const BASE = "http://localhost:8080";

function tempoRelativo(iso: string): string {
  if (!iso) return "—";
  const diff = (Date.now() - new Date(iso.replace(" ", "T")).getTime()) / 1000;
  if (diff < 60)   return `${Math.round(diff)}s atrás`;
  if (diff < 3600) return `${Math.round(diff / 60)}min atrás`;
  return `${Math.round(diff / 3600)}h atrás`;
}

const STATUS_LABEL: Record<string, string> = {
  ativo:        "Online",
  manutencao:   "Manutenção",
  desativado:   "Offline",
  desconhecido: "Desconhecido",
};

export default function SensorList() {
  const [sensores,     setSensores]     = useState<Sensor[]>([]);
  const [filtroStatus, setFiltroStatus] = useState<string>("todos");
  const [filtroZona,   setFiltroZona]   = useState<string>("");
  const [streamAtivo,  setStreamAtivo]  = useState<string | null>(null);
  const [erro,         setErro]         = useState("");

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

  const zonas = [...new Set(sensores.map(s => s.zona))].sort();

  const lista = sensores
    .filter(s => filtroStatus === "todos" || s.status === filtroStatus)
    .filter(s => !filtroZona || s.zona === filtroZona);

  const counts = {
    total:      sensores.length,
    ativo:      sensores.filter(s => s.status === "ativo").length,
    manutencao: sensores.filter(s => s.status === "manutencao").length,
    desativado: sensores.filter(s => s.status === "desativado").length,
  };

  if (erro) return <p className="erro">{erro}</p>;

  return (
    <div className="sl-wrap">
      {/* Summary strip */}
      <div className="sl-summary">
        <button className={`summary-chip ${filtroStatus === "todos" ? "active" : ""}`}      onClick={() => setFiltroStatus("todos")}>
          Todos <b>{counts.total}</b>
        </button>
        <button className={`summary-chip chip-green  ${filtroStatus === "ativo"      ? "active" : ""}`} onClick={() => setFiltroStatus("ativo")}>
          Online <b>{counts.ativo}</b>
        </button>
        <button className={`summary-chip chip-amber  ${filtroStatus === "manutencao" ? "active" : ""}`} onClick={() => setFiltroStatus("manutencao")}>
          Manutenção <b>{counts.manutencao}</b>
        </button>
        <button className={`summary-chip chip-red    ${filtroStatus === "desativado" ? "active" : ""}`} onClick={() => setFiltroStatus("desativado")}>
          Offline <b>{counts.desativado}</b>
        </button>

        <div className="sl-spacer" />

        <select value={filtroZona} onChange={e => setFiltroZona(e.target.value)} className="sl-zone-select">
          <option value="">Todas as zonas</option>
          {zonas.map(z => <option key={z}>{z}</option>)}
        </select>
      </div>

      {lista.length === 0 && <p className="vazio">Sem sensores para os filtros seleccionados.</p>}

      <div className="sensor-grid">
        {lista.map(s => (
          <div key={s.sensorId} className={`sensor-card status-${s.status}`}>

            <div className="sc-header">
              <div className="sc-id-row">
                <span className={`sc-dot dot-${s.status === "ativo" ? "green" : s.status === "manutencao" ? "amber" : "red"}`} />
                <span className="sc-id">{s.sensorId}</span>
              </div>
              <span className={`sc-status badge ${
                s.status === "ativo"      ? "badge-green" :
                s.status === "manutencao" ? "badge-amber" : "badge-red"
              }`}>
                {STATUS_LABEL[s.status] ?? s.status}
              </span>
            </div>

            <div className="sc-zona">{s.zona}</div>

            <div className="sc-tipos">
              {s.tipos.split(",").map(t => (
                <span key={t} className="badge badge-blue">{t.trim()}</span>
              ))}
            </div>

            <div className="sc-footer">
              <div className="sc-footer-left">
                {s.totalAlarmes > 0
                  ? <span className="badge badge-red">⚠ {s.totalAlarmes} alarme{s.totalAlarmes > 1 ? "s" : ""}</span>
                  : <span className="badge badge-gray">Sem alarmes</span>
                }
              </div>
              <span className="sc-time">{tempoRelativo(s.ultimaLeitura)}</span>
            </div>

            {s.videoStream && (
              <button
                className={`sc-stream-btn ${streamAtivo === s.sensorId ? "streaming" : ""}`}
                onClick={() => toggleStream(s)}
              >
                {streamAtivo === s.sensorId ? "◼ Parar Stream" : "▶ Ver Vídeo"}
              </button>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
