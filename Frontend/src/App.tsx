import { useState } from "react";
import Dashboard  from "./components/Dashboard";
import SensorList from "./components/SensorList";
import DataChart  from "./components/DataChart";
import AlarmLog   from "./components/AlarmLog";
import Analise    from "./components/Analise";
import "./App.css";

const BASE = "http://localhost:8080";

type Tab = "dashboard" | "sensores" | "dados" | "alarmes" | "analise";

const TABS: { id: Tab; label: string; icon: string }[] = [
  { id: "dashboard", label: "Dashboard", icon: "▦"  },
  { id: "sensores",  label: "Sensores",  icon: "⬡"  },
  { id: "dados",     label: "Dados",     icon: "↗"  },
  { id: "alarmes",   label: "Alarmes",   icon: "⚠"  },
  { id: "analise",   label: "Análise",   icon: "∿"  },
];

export default function App() {
  const [tab, setTab] = useState<Tab>("dashboard");

  async function handleShutdown() {
    if (!confirm("Desligar o servidor?")) return;
    await fetch(`${BASE}/api/shutdown`).catch(() => {});
  }

  return (
    <div className="app">
      <header className="app-header">
        <div className="header-brand">
          <div className="brand-icon">🌿</div>
          <span className="brand-title">ONE HEALTH</span>
          <span className="brand-sub">Monitorização Ambiental</span>
        </div>
        <div className="header-actions">
          <button className="shutdown-btn" onClick={handleShutdown} title="Desligar servidor">⏻</button>
        </div>
      </header>

      <nav className="tab-bar">
        {TABS.map(t => (
          <button
            key={t.id}
            className={`tab-btn ${tab === t.id ? "active" : ""}`}
            onClick={() => setTab(t.id)}
          >
            <span className="tab-icon">{t.icon}</span>
            {t.label}
          </button>
        ))}
      </nav>

      <main className="app-main">
        {tab === "dashboard" && <Dashboard onNavigate={setTab} />}
        {tab === "sensores"  && <SensorList />}
        {tab === "dados"     && <DataChart  />}
        {tab === "alarmes"   && <AlarmLog   />}
        {tab === "analise"   && <Analise    />}
      </main>
    </div>
  );
}
