const BASE = "http://localhost:8080";

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

async function get<T>(path: string): Promise<T> {
  const res = await fetch(BASE + path);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

export const api = {
  sensores: ()                                         => get<Sensor[]>("/api/sensores"),
  dados:    (zona="", tipo="", sensor="", limite=150) => get<Leitura[]>(`/api/dados?zona=${zona}&tipo=${tipo}&sensor=${sensor}&limite=${limite}`),
  alarmes:  (zona="", tipo="", limite=50)             => get<Alarme[]>(`/api/alarmes?zona=${zona}&tipo=${tipo}&limite=${limite}`),
  analise:  (zona="", tipo="")                        => get<Analise>(`/api/analise?zona=${zona}&tipo=${tipo}`),
  padroes:  (zona="", tipo="")                        => get<Padroes>(`/api/padroes?zona=${zona}&tipo=${tipo}`),
  previsao: (zona="", tipo="", horas=6)               => get<Previsao>(`/api/previsao?zona=${zona}&tipo=${tipo}&horas=${horas}`),
};
