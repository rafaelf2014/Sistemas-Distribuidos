import { useState } from "react";
import SensorList from "./components/SensorList";
import DataChart  from "./components/DataChart";
import AlarmLog   from "./components/AlarmLog";
import Analise    from "./components/Analise";
import "./App.css";

type Tab = "sensores" | "dados" | "alarmes" | "analise";

const TABS: { id: Tab; label: string }[] = [
  { id: "sensores", label: "Sensores" },
  { id: "dados",    label: "Dados"    },
  { id: "alarmes",  label: "Alarmes"  },
  { id: "analise",  label: "Análise"  },
];

export default function App() {
  const [tab, setTab] = useState<Tab>("sensores");

  return (
    <div className="app">
      <header className="app-header">
        <div className="header-brand">
          <span className="brand-dot" />
          <span className="brand-title">ONE HEALTH</span>
          <span className="brand-sub">Sistema de Monitorização Ambiental</span>
        </div>
      </header>

      <nav className="tab-bar">
        {TABS.map(t => (
          <button
            key={t.id}
            className={`tab-btn ${tab === t.id ? "active" : ""}`}
            onClick={() => setTab(t.id)}
          >
            {t.label}
          </button>
        ))}
      </nav>

      <main className="app-main">
        {tab === "sensores" && <SensorList />}
        {tab === "dados"    && <DataChart  />}
        {tab === "alarmes"  && <AlarmLog   />}
        {tab === "analise"  && <Analise    />}
      </main>
    </div>
  );
}
