import { useState } from "react";
import Dashboard    from "./components/Dashboard";
import SensorList   from "./components/SensorList";
import DataChart    from "./components/DataChart";
import AlarmLog     from "./components/AlarmLog";
import Analise      from "./components/Analise";
import AnomaliasMl  from "./components/AnomaliasMl";
import Login        from "./components/Login";
import { api, auth } from "./api";
import "./App.css";

type Tab = "dashboard" | "sensores" | "dados" | "alarmes" | "analise" | "anomalias";

const TABS: { id: Tab; label: string; icon: string }[] = [
  { id: "dashboard",  label: "Dashboard",    icon: "▦"  },
  { id: "sensores",   label: "Sensores",     icon: "⬡"  },
  { id: "dados",      label: "Dados",        icon: "↗"  },
  { id: "alarmes",    label: "Alarmes",      icon: "⚠"  },
  { id: "analise",    label: "Análise",      icon: "∿"  },
  { id: "anomalias",  label: "Anomalias ML", icon: "◉"  },
];

export default function App() {
  const [tab,      setTab]      = useState<Tab>("dashboard");
  const [loggedIn, setLoggedIn] = useState(auth.isLoggedIn());
  const [username, setUsername] = useState("");

  if (!loggedIn)
    return <Login onLogin={u => { setUsername(u); setLoggedIn(true); }} />;

  async function handleShutdown() {
    if (!confirm("Desligar o servidor?")) return;
    fetch("http://localhost:8080/api/shutdown", {
      headers: { Authorization: `Bearer ${auth.getToken() ?? ""}` }
    }).catch(() => {});
  }

  function handleLogout() {
    auth.clearToken();
    setLoggedIn(false);
    setUsername("");
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
          <span className="header-user">{username}</span>
          <button className="logout-btn" onClick={handleLogout} title="Terminar sessão">⎋</button>
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
        {tab === "dashboard"  && <Dashboard onNavigate={setTab} />}
        {tab === "sensores"   && <SensorList />}
        {tab === "dados"      && <DataChart  />}
        {tab === "alarmes"    && <AlarmLog   />}
        {tab === "analise"    && <Analise    />}
        {tab === "anomalias"  && <AnomaliasMl />}
      </main>
    </div>
  );
}
