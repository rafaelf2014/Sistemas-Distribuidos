import { useEffect, useState, useMemo } from "react";
import { api } from "../api";
import type { Anomalia } from "../api";
import "./AnomaliasMl.css";

const TIPOS = ["", "TEMP", "HUM", "CO2", "RUIDO", "LUMIN", "PART", "NO2", "O3", "WIND"];

type SortCol = "sensorId" | "zona" | "tipoDado" | "valor" | "timestamp" | "anomalyScore";
type SortDir = "asc" | "desc";

function ScoreBar({ score }: { score: number }) {
  const pct   = Math.round(score * 100);
  const color = score >= 0.8 ? "var(--danger)" : score >= 0.5 ? "var(--warning)" : "var(--success)";
  return (
    <div className="score-bar-wrap">
      <div className="score-bar-track">
        <div className="score-bar-fill" style={{ width: `${pct}%`, background: color }} />
      </div>
      <span className="score-bar-label" style={{ color }}>{pct}%</span>
    </div>
  );
}

function SortIcon({ col, sortCol, sortDir }: { col: SortCol; sortCol: SortCol; sortDir: SortDir }) {
  if (col !== sortCol) return <span className="sort-icon sort-inactive">⇅</span>;
  return <span className="sort-icon">{sortDir === "asc" ? "↑" : "↓"}</span>;
}

export default function AnomaliasMl() {
  const [zona,     setZona]     = useState("");
  const [tipo,     setTipo]     = useState("TEMP");
  const [minScore, setMinScore] = useState(0);
  const [perPage,  setPerPage]  = useState(50);
  const [page,     setPage]     = useState(1);
  const [dados,    setDados]    = useState<Anomalia[]>([]);
  const [zonas,    setZonas]    = useState<string[]>([]);
  const [erro,     setErro]     = useState("");
  const [aquecido, setAquecido] = useState<boolean | null>(null);
  const [sortCol,  setSortCol]  = useState<SortCol>("timestamp");
  const [sortDir,  setSortDir]  = useState<SortDir>("desc");
  const [ready,    setReady]    = useState(false);

  useEffect(() => {
    api.sensores().then(ss => {
      const zs = [...new Set(ss.map(s => s.zona))];
      setZonas(zs);
      if (zs.length > 0) setZona(zs[0]);
    }).catch(() => {}).finally(() => setReady(true));
    const checkWarm = () => api.mlStatus().then(s => setAquecido(s.aquecido)).catch(() => {});
    checkWarm();
    const id = setInterval(checkWarm, 30000);
    return () => clearInterval(id);
  }, []);

  // Always fetch the full 500 — pagination is done client-side
  useEffect(() => {
    if (!ready) return;
    const carregar = () =>
      api.anomalias(zona, tipo, minScore, 500)
        .then(a => { setDados(a); setErro(""); })
        .catch(() => setErro("Sem dados ou servidor indisponível."));
    carregar();
    const id = setInterval(carregar, 8000);
    return () => clearInterval(id);
  }, [ready, zona, tipo, minScore]);

  // Reset to page 1 whenever filters or page size change
  useEffect(() => { setPage(1); }, [zona, tipo, minScore, perPage]);

  function toggleSort(col: SortCol) {
    if (col === sortCol) setSortDir(d => d === "asc" ? "desc" : "asc");
    else { setSortCol(col); setSortDir("desc"); }
  }

  const sorted = useMemo(() => {
    return [...dados].sort((a, b) => {
      let av: string | number = a[sortCol];
      let bv: string | number = b[sortCol];
      if (sortCol === "valor") { av = parseFloat(av as string); bv = parseFloat(bv as string); }
      if (av < bv) return sortDir === "asc" ? -1 : 1;
      if (av > bv) return sortDir === "asc" ?  1 : -1;
      return 0;
    });
  }, [dados, sortCol, sortDir]);

  const agora = Date.now();
  const criticos24h = dados.filter(a =>
    a.anomalyScore >= 0.8 &&
    (agora - new Date(a.timestamp.replace(" ", "T")).getTime()) < 86400_000
  ).length;

  const totalPages = Math.max(1, Math.ceil(sorted.length / perPage));
  const safePage   = Math.min(page, totalPages);
  const paginated  = sorted.slice((safePage - 1) * perPage, safePage * perPage);
  const firstEntry = sorted.length === 0 ? 0 : (safePage - 1) * perPage + 1;
  const lastEntry  = Math.min(safePage * perPage, sorted.length);

  return (
    <div className="aml-wrap">
      <div className="filters-bar">
        <select value={zona} onChange={e => setZona(e.target.value)}>
          <option value="">Todas as zonas</option>
          {zonas.map(z => <option key={z}>{z}</option>)}
        </select>
        <select value={tipo} onChange={e => setTipo(e.target.value)}>
          {TIPOS.map(t => <option key={t} value={t}>{t || "Todos os tipos"}</option>)}
        </select>
        <label className="filter-inline">
          Score mín.
          <input
            type="range" min={0} max={1} step={0.05}
            value={minScore}
            onChange={e => setMinScore(+e.target.value)}
          />
          <span>{Math.round(minScore * 100)}%</span>
        </label>
        <select value={perPage} onChange={e => setPerPage(+e.target.value)}>
          {[20, 50, 100].map(n => <option key={n} value={n}>{n} por página</option>)}
        </select>
        <button className="btn" onClick={() =>
          api.anomalias(zona, tipo, minScore, 500)
            .then(a => { setDados(a); setErro(""); })
            .catch(() => setErro("Sem dados ou servidor indisponível."))
        }>Atualizar</button>
      </div>

      {aquecido === false && (
        <div className="aml-banner banner-warn">
          Modelo ML ainda a aquecer — os scores reflectem poucas amostras.
        </div>
      )}

      {erro && <p className="erro">{erro}</p>}

      {dados.length > 0 && (
        <>
          <div className="aml-stats card">
            <div className="dc-stat">
              <span className="dc-stat-label">Críticos ≥80% (24h)</span>
              <span className="dc-stat-value" style={{ color: "var(--danger)" }}>{criticos24h}</span>
            </div>
          </div>

          <div className="card aml-table-card">
            <table className="aml-table">
              <thead>
                <tr>
                  <th className="sortable" onClick={() => toggleSort("sensorId")}>
                    Sensor <SortIcon col="sensorId" sortCol={sortCol} sortDir={sortDir} />
                  </th>
                  <th className="sortable" onClick={() => toggleSort("zona")}>
                    Zona <SortIcon col="zona" sortCol={sortCol} sortDir={sortDir} />
                  </th>
                  <th className="sortable" onClick={() => toggleSort("tipoDado")}>
                    Tipo <SortIcon col="tipoDado" sortCol={sortCol} sortDir={sortDir} />
                  </th>
                  <th className="sortable" onClick={() => toggleSort("valor")}>
                    Valor <SortIcon col="valor" sortCol={sortCol} sortDir={sortDir} />
                  </th>
                  <th className="sortable" onClick={() => toggleSort("timestamp")}>
                    Timestamp <SortIcon col="timestamp" sortCol={sortCol} sortDir={sortDir} />
                  </th>
                  <th className="sortable" onClick={() => toggleSort("anomalyScore")}>
                    Score ML <SortIcon col="anomalyScore" sortCol={sortCol} sortDir={sortDir} />
                  </th>
                </tr>
              </thead>
              <tbody>
                {paginated.map((a, i) => (
                  <tr key={i} className={a.anomalyScore >= 0.8 ? "row-critico" : a.anomalyScore >= 0.5 ? "row-medio" : ""}>
                    <td>{a.sensorId}</td>
                    <td>{a.zona}</td>
                    <td><span className="badge badge-blue">{a.tipoDado}</span></td>
                    <td className="aml-valor">{a.valor}</td>
                    <td className="aml-ts">{a.timestamp.slice(5, 19).replace("T", " ")}</td>
                    <td><ScoreBar score={a.anomalyScore} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Pagination */}
          <div className="aml-pagination">
            <button
              className="btn"
              onClick={() => setPage(p => Math.max(1, p - 1))}
              disabled={safePage === 1}
            >← Anterior</button>

            <span className="aml-page-info">
              {firstEntry}–{lastEntry} de {sorted.length}
              &nbsp;·&nbsp;
              Página {safePage} / {totalPages}
            </span>

            <button
              className="btn"
              onClick={() => setPage(p => Math.min(totalPages, p + 1))}
              disabled={safePage === totalPages}
            >Próxima →</button>
          </div>
        </>
      )}

      {dados.length === 0 && !erro && (
        <p className="vazio">Sem anomalias acima do score mínimo seleccionado.</p>
      )}
    </div>
  );
}
