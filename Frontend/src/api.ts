const BASE = "http://localhost:8080";

const TOKEN_KEY = "one_health_token";

export const auth = {
  getToken:   ()           => localStorage.getItem(TOKEN_KEY),
  setToken:   (t: string)  => localStorage.setItem(TOKEN_KEY, t),
  clearToken: ()           => localStorage.removeItem(TOKEN_KEY),
  isLoggedIn: ()           => !!localStorage.getItem(TOKEN_KEY),
};

export interface Sensor {
  sensorId: string;
  zona: string;
  tipos: string;
  ultimaLeitura: string;
  totalAlarmes: number;
  videoStream: boolean;
  status: "ativo" | "manutencao" | "desativado" | "desconhecido";
}

export interface Leitura {
  gatewayId: string;
  sensorId: string;
  zona: string;
  tipoDado: string;
  valor: string;
  timestamp: string;
  isAlarm: boolean;
  qualidade: number;
  anomalyScore?: number;
}

export interface Anomalia {
  gatewayId: string;
  sensorId: string;
  zona: string;
  tipoDado: string;
  valor: string;
  timestamp: string;
  anomalyScore: number;
}

export interface Alarme {
  gatewayId: string;
  sensorId: string;
  zona: string;
  tipoDado: string;
  valor: string;
  timestamp: string;
}

export interface Analise {
  zona: string;
  tipoDado: string;
  media: number;
  desvioPadrao: number;
  minimo: number;
  maximo: number;
  totalLeituras: number;
  totalAlarmes: number;
  timestamp: string;
}

export interface Padrao {
  descricao: string;
  confianca: number;
  horaPico: string;
}

export interface Padroes {
  zona: string;
  tipoDado: string;
  padroes: Padrao[];
  timestamp: string;
}

export interface Previsao {
  zona: string;
  tipoDado: string;
  valoresPrevistos: number[];
  riscoSaude: number;
  recomendacao: string;
  timestamp: string;
}

function authHeaders(): HeadersInit {
  const token = auth.getToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

const TIMEOUT_MS = 8000;

async function get<T>(path: string): Promise<T> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const res = await fetch(BASE + path, { signal: ctrl.signal, headers: authHeaders() });
    if (res.status === 401) {
      auth.clearToken();
      window.location.reload();
      throw new Error("Sessão expirada.");
    }
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
  } finally {
    clearTimeout(timer);
  }
}

async function post<T>(path: string, body: unknown): Promise<T> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const res = await fetch(BASE + path, {
      method: "POST",
      signal: ctrl.signal,
      headers: { "Content-Type": "application/json", ...authHeaders() },
      body: JSON.stringify(body),
    });
    if (res.status === 401) {
      auth.clearToken();
      window.location.reload();
      throw new Error("Sessão expirada.");
    }
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
  } finally {
    clearTimeout(timer);
  }
}

export const api = {
  login:    (username: string, password: string) =>
              post<{ token: string; username: string }>("/api/login", { username, password }),
  sensores: ()                                         => get<Sensor[]>("/api/sensores"),
  dados:    (zona="", tipo="", sensor="", limite=150)  => get<Leitura[]>(`/api/dados?zona=${zona}&tipo=${tipo}&sensor=${sensor}&limite=${limite}`),
  alarmes:  (zona="", tipo="", limite=50)              => get<Alarme[]>(`/api/alarmes?zona=${zona}&tipo=${tipo}&limite=${limite}`),
  analise:  (zona="", tipo="")                         => get<Analise>(`/api/analise?zona=${zona}&tipo=${tipo}`),
  padroes:  (zona="", tipo="")                         => get<Padroes>(`/api/padroes?zona=${zona}&tipo=${tipo}`),
  previsao: (zona="", tipo="", horas=6)                => get<Previsao>(`/api/previsao?zona=${zona}&tipo=${tipo}&horas=${horas}`),
  anomalias:    (zona="", tipo="", minScore=0.5, limite=100) => get<Anomalia[]>(`/api/anomalias?zona=${zona}&tipo=${tipo}&min_score=${minScore}&limite=${limite}`),
  mlStatus:     ()                                          => get<{ aquecido: boolean }>("/api/ml/status"),
  dadosPeriodo: (zona: string, tipo: string, inicio: string, fim: string, limite = 5000) =>
    get<Leitura[]>(`/api/dados?zona=${encodeURIComponent(zona)}&tipo=${encodeURIComponent(tipo)}&inicio=${encodeURIComponent(inicio)}&fim=${encodeURIComponent(fim)}&limite=${limite}`),
};
