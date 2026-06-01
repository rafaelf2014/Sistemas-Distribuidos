import { useEffect, useState } from "react";
import {
  BarChart, Bar, XAxis, YAxis, CartesianGrid,
  Tooltip, ResponsiveContainer, Cell
} from "recharts";
import { api } from "../api";
import "./Analise.css";

// Interface para garantir a tipagem dos dados vindos do C#
interface AnomaliaZona {
  zona: string;
  totalAnomalias: number;
}

export default function Analise() {
  const [anomalias, setAnomalias] = useState<AnomaliaZona[]>([]);
  const [erro, setErro] = useState("");
  const [loading, setLoading] = useState(false);

  // Carrega os dados agregados das anomalias apenas uma vez no arranque
  useEffect(() => {
    const carregarDados = async () => {
      setLoading(true);
      setErro("");
      try {
        // Assume que adicionaste anomaliasZonas ao teu ficheiro api.ts
        // Se não adicionaste, usa: const res = await fetch("http://localhost:8080/api/anomalias/zonas"); const dados = await res.json();
        const dados = await api.anomaliasZonas();

        // Pega apenas no Top 5 (ou Top 10) para o gráfico não ficar gigante
        setAnomalias(dados.slice(0, 5));
      } catch {
        setErro("Falha ao carregar o ranking de anomalias.");
      } finally {
        setLoading(false);
      }
    };

    carregarDados();
  }, []);

  return (
    <div className="analise-wrap">

      {/* Cabeçalho Limpo */}
      <div className="analise-filters" style={{ marginBottom: "24px" }}>
        <h2>Painel de Controlo de Risco</h2>
        {loading && <span style={{ color: "#aaa", fontSize: "14px" }}>A carregar dados do servidor...</span>}
      </div>

      {erro && <p className="erro" style={{ color: "#EF5350" }}>{erro}</p>}

      {/* O Gráfico de Barras Horizontais com Design Profissional */}
      {!erro && anomalias.length > 0 ? (
        // WRAPPER com maxWidth e centrado para não ficar gigante
        <div className="card card-full" style={{ maxWidth: "850px", margin: "0", padding: "24px" }}>

          <h3 style={{ marginBottom: "24px", color: "#e0e0e0", fontSize: "1.1rem", textTransform: "uppercase", letterSpacing: "1px" }}>
            Top 5 Zonas Críticas <span style={{ color: "#888", fontSize: "0.9rem", textTransform: "none" }}>(Total de Alarmes críticos)</span>
          </h3>

          {/* Altura dinâmica: Se houver só 1 zona, não precisa de 350px. Usamos minHeight. */}
          <div style={{ width: "100%", height: Math.max(200, anomalias.length * 60) }}>
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={anomalias} layout="vertical" margin={{ top: 5, right: 30, left: 20, bottom: 5 }}>
                {/* Grelha mais subtil */}
                <CartesianGrid strokeDasharray="3 3" horizontal={true} vertical={false} stroke="#33334d" />

                <XAxis
                  type="number"
                  tick={{ fill: "#666", fontSize: 12 }}
                  allowDecimals={false}
                  axisLine={{ stroke: '#444' }}
                  tickLine={false}
                />

                <YAxis
                  dataKey="zona"
                  type="category"
                  tick={{ fill: "#ccc", fontSize: 13, fontWeight: "500" }}
                  width={140}
                  axisLine={{ stroke: '#444' }}
                  tickLine={false}
                />

                <Tooltip
                  cursor={{ fill: "rgba(255, 255, 255, 0.03)" }}
                  contentStyle={{
                    background: "#1e1e2f",
                    border: "1px solid #33334d",
                    borderRadius: "8px",
                    boxShadow: "0 4px 6px rgba(0,0,0,0.3)"
                  }}
                  // Muda a cor do Nome da Zona (Branco com texto em negrito)
                  labelStyle={{ color: "#ffffff", fontWeight: "bold", paddingBottom: "4px" }}

                  // Muda a cor do valor e do texto (Verde Néon para contraste)
                  itemStyle={{ color: "#69f0ae", fontSize: "14px", fontWeight: "500" }}

                  formatter={(value: any) => [`${value} Alarmes`, "Total"]}
                />

                {/* barSize limita a grossura da barra. radius arredonda as pontas. */}
                <Bar dataKey="totalAnomalias" barSize={28} radius={[0, 6, 6, 0]}>
                  {anomalias.map((entry, index) => (
                    <Cell
                      key={`cell-${index}`}
                      // Pior zona (índice 0) fica a vermelho elegante, restantes a laranja/dourado
                      fill={index === 0 ? "#EF5350" : "#FFA726"}
                    />
                  ))}
                </Bar>
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>
      ) : (
        !loading && !erro && <p style={{ color: "#aaa" }}>O sistema está estável. Não há anomalias registadas.</p>
      )}

    </div>
  );

}