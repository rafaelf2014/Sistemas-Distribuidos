import { useEffect, useState } from "react";
import { api } from "../api";
import type { Sensor, Alarme } from "../api";
import "./Dashboard.css";

type Tab = "dashboard" | "sensores" | "dados" | "alarmes" | "analise";

interface Props { onNavigate: (tab: Tab) => void; }

const UNIDADES: Record<string, string> = {
  TEMP: "°C", HUM: "%", CO2: "ppm", RUIDO: "dB", LUMIN: "lux", PART: "µg/m³",
};

function tempoRelativo(iso: string): string {
  if (!iso) return "—";
  const diff = (Date.now() - new Date(iso.replace(" ", "T")).getTime()) / 1000;
  if (diff < 60)   return `${Math.round(diff)}s atrás`;
  if (diff < 3600) return `${Math.round(diff / 60)}min atrás`;
  return `${Math.round(diff / 3600)}h atrás`;
}

export default function Dashboard({ onNavigate }: Props) {
  const [sensores, setSensores] = useState<Sensor[]>([]);
  const [alarmes,  setAlarmes]  = useState<Alarme[]>([]);
  const [erro,     setErro]     = useState("");

  const carregar = () => {
    Promise.all([api.sensores(), api.alarmes("", "", 8)])
      .then(([ss, al]) => { setSensores(ss); setAlarmes(al); setErro(""); })
      .catch(() => setErro("Servidor indisponível."));
  };

  useEffect(() => {
    carregar();
    const id = setInterval(carregar, 6000);
    return () => clearInterval(id);
  }, []);

  const ativos       = sensores.filter(s => s.status === "ativo").length;
  const manutencao   = sensores.filter(s => s.status === "manutencao").length;
  const desativados  = sensores.filter(s => s.status === "desativado").length;
  const totalAlarmes = sensores.reduce((acc, s) => acc + s.totalAlarmes, 0);

  // Zone summary — group sensors by zone
  const zonaMap = new Map<string, { ativos: number; total: number; alarmes: number; tipos: Set<string> }>();
  for (const s of sensores) {
    if (!zonaMap.has(s.zona)) zonaMap.set(s.zona, { ativos: 0, total: 0, alarmes: 0, tipos: new Set() });
    const z = zonaMap.get(s.zona)!;
    z.total++;
    if (s.status === "ativo") z.ativos++;
    z.alarmes += s.totalAlarmes;
    s.tipos.split(",").forEach(t => z.tipos.add(t.trim()));
  }
  const zonas = Array.from(zonaMap.entries()).sort((a, b) => b[1].alarmes - a[1].alarmes);

  return (
    <div className="dash-wrap">
      {erro && <p className="erro">{erro}</p>}

      {/* KPI row */}
      <div className="kpi-row">
        <div className="kpi-card" onClick={() => onNavigate("sensores")} style={{ cursor: "pointer" }}>
          <div className="kpi-icon kpi-blue">⬡</div>
          <div className="kpi-body">
            <span className="kpi-value">{sensores.length}</span>
            <span className="kpi-label">Total de Sensores</span>
          </div>
        </div>

        <div className="kpi-card" onClick={() => onNavigate("sensores")} style={{ cursor: "pointer" }}>
          <div className="kpi-icon kpi-green">✓</div>
          <div className="kpi-body">
            <span className="kpi-value kpi-green-text">{ativos}</span>
            <span className="kpi-label">Online</span>
          </div>
          {manutencao > 0 && (
            <span className="kpi-sub kpi-amber-text">+{manutencao} manutenção</span>
          )}
        </div>

        <div className="kpi-card" onClick={() => onNavigate("alarmes")} style={{ cursor: "pointer" }}>
          <div className="kpi-icon kpi-red">⚠</div>
          <div className="kpi-body">
            <span className="kpi-value kpi-red-text">{totalAlarmes}</span>
            <span className="kpi-label">Total de Alarmes</span>
          </div>
        </div>

        <div className="kpi-card">
          <div className="kpi-icon kpi-purple">▦</div>
          <div className="kpi-body">
            <span className="kpi-value">{zonas.length}</span>
            <span className="kpi-label">Zonas Monitorizadas</span>
          </div>
        </div>

        <div className="kpi-card">
          <div className="kpi-icon kpi-amber">⊘</div>
          <div className="kpi-body">
            <span className="kpi-value kpi-amber-text">{desativados}</span>
            <span className="kpi-label">Offline</span>
          </div>
        </div>
      </div>

      <div className="dash-grid">
        {/* Zone summary */}
        <div className="card dash-card">
          <div className="dash-card-header">
            <span className="dash-card-title">Resumo por Zona</span>
            <span className="dash-card-count">{zonas.length} zonas</span>
          </div>
          {zonas.length === 0 && <p className="vazio">Sem zonas registadas.</p>}
          <div className="zone-list">
            {zonas.map(([zona, info]) => (
              <div key={zona} className="zone-row">
                <div className="zone-name">{zona}</div>
                <div className="zone-meta">
                  {Array.from(info.tipos).map(t => (
                    <span key={t} className="badge badge-blue">{t}</span>
                  ))}
                </div>
                <div className="zone-stats">
                  <span className="zone-stat">
                    <span className="zone-stat-dot dot-green" />
                    {info.ativos}/{info.total}
                  </span>
                  {info.alarmes > 0 && (
                    <span className="zone-stat kpi-red-text">
                      ⚠ {info.alarmes}
                    </span>
                  )}
                </div>
              </div>
            ))}
          </div>
        </div>

        {/* Recent alarms */}
        <div className="card dash-card">
          <div className="dash-card-header">
            <span className="dash-card-title">Alarmes Recentes</span>
            <button className="dash-link" onClick={() => onNavigate("alarmes")}>Ver todos →</button>
          </div>
          {alarmes.length === 0 && <p className="vazio">Sem alarmes registados.</p>}
          <div className="recent-alarms">
            {alarmes.map((a, i) => {
              const un = UNIDADES[a.tipoDado] ?? "";
              return (
                <div key={i} className="alarm-row">
                  <div className="alarm-row-left">
                    <span className="badge badge-red">{a.tipoDado}</span>
                    <span className="alarm-sensor">{a.sensorId}</span>
                    <span className="alarm-zona">{a.zona}</span>
                  </div>
                  <div className="alarm-row-right">
                    <span className="alarm-value">{a.valor}{un}</span>
                    <span className="alarm-time">{tempoRelativo(a.timestamp)}</span>
                  </div>
                </div>
              );
            })}
          </div>
        </div>

        {/* Sensor status breakdown */}
        <div className="card dash-card">
          <div className="dash-card-header">
            <span className="dash-card-title">Estado dos Sensores</span>
            <button className="dash-link" onClick={() => onNavigate("sensores")}>Ver todos →</button>
          </div>
          {sensores.length === 0 && <p className="vazio">Sem sensores registados.</p>}
          <div className="sensor-status-list">
            {sensores.map(s => (
              <div key={s.sensorId} className="ss-row">
                <div className="ss-left">
                  <span className={`ss-dot dot-${s.status === "ativo" ? "green" : s.status === "manutencao" ? "amber" : "red"}`} />
                  <span className="ss-id">{s.sensorId}</span>
                  <span className="ss-zona">{s.zona}</span>
                </div>
                <div className="ss-right">
                  {s.totalAlarmes > 0 && (
                    <span className="badge badge-red">⚠ {s.totalAlarmes}</span>
                  )}
                  <span className="ss-time">{tempoRelativo(s.ultimaLeitura)}</span>
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
