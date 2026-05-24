"""
ONE HEALTH — Serviço de Análise e Previsão (gRPC / Python)
Invocado pelo Servidor Central para análise estatística e deteção de padrões.

Gerar código gRPC a partir do proto (executar uma vez):
    python -m grpc_tools.protoc -I../protos --python_out=. --grpc_python_out=. ../protos/analysis.proto
"""

import grpc
import os
from concurrent import futures
import analysis_pb2
import analysis_pb2_grpc
import psycopg2
from psycopg2 import pool as pg_pool
import pandas as pd
import numpy as np
from datetime import datetime, timedelta

DATABASE_URL = os.environ.get("DATABASE_URL", "postgresql://postgres:postgres@localhost/one_health")
PORT    = 50052

_pool: pg_pool.ThreadedConnectionPool = None

# Thresholds de risco por tipo de dado (valores médios considerados preocupantes)
RISCO_THRESHOLDS = {
    "TEMP":  {"baixo": 28.0,  "medio": 33.0,  "alto": 38.0},
    "HUM":   {"baixo": 30.0,  "medio": 20.0,  "alto": 10.0},   # invertido: baixa humidade = risco
    "CO2":   {"baixo": 800.0, "medio": 1200.0, "alto": 1800.0},
    "RUIDO": {"baixo": 60.0,  "medio": 75.0,  "alto": 85.0},
    "LUMIN": {"baixo": 200.0, "medio": 50.0,  "alto": 10.0},   # invertido: pouca luz = risco
    "PART":  {"baixo": 35.0,  "medio": 75.0,  "alto": 150.0},
}


class AnaliseServicer(analysis_pb2_grpc.AnaliseServiceServicer):

    def _carregar_dados(self, zona, tipo_dado, sensor_id, data_inicio, data_fim):
        query = "SELECT valor, timestamp, is_alarm FROM leituras WHERE 1=1"
        params = []

        if zona:
            query += " AND zona = %s";           params.append(zona)
        if tipo_dado:
            query += " AND tipo_dado = %s";      params.append(tipo_dado)
        if sensor_id:
            query += " AND sensor_id = %s";      params.append(sensor_id)
        if data_inicio:
            query += " AND timestamp >= %s";     params.append(data_inicio)
        if data_fim:
            query += " AND timestamp <= %s";     params.append(data_fim)

        query += " ORDER BY timestamp DESC LIMIT 10000"

        conn = _pool.getconn()
        try:
            df = pd.read_sql_query(query, conn, params=params)
        finally:
            _pool.putconn(conn)
        df = df.rename(columns={"valor": "Valor", "timestamp": "Timestamp", "is_alarm": "IsAlarm"})
        df["Valor"] = pd.to_numeric(df["Valor"], errors="coerce")
        df["Timestamp"] = pd.to_datetime(df["Timestamp"], errors="coerce")
        df = df.dropna(subset=["Valor"])
        return df.sort_values("Timestamp").reset_index(drop=True)

    def AnalisarZona(self, request, context):
        df = self._carregar_dados(
            request.zona, request.tipo_dado, request.sensor_id,
            request.data_inicio, request.data_fim
        )

        if df.empty:
            context.set_code(grpc.StatusCode.NOT_FOUND)
            context.set_details("Sem dados para os filtros fornecidos.")
            return analysis_pb2.ResultadoAnalise()

        return analysis_pb2.ResultadoAnalise(
            zona           = request.zona,
            tipo_dado      = request.tipo_dado,
            media          = float(df["Valor"].mean()),
            desvio_padrao  = float(df["Valor"].std()),
            minimo         = float(df["Valor"].min()),
            maximo         = float(df["Valor"].max()),
            total_leituras = len(df),
            total_alarmes  = int(df["IsAlarm"].sum()),
            timestamp      = datetime.now().isoformat()
        )

    def DetectarPadroes(self, request, context):
        df = self._carregar_dados(
            request.zona, request.tipo_dado, request.sensor_id,
            request.data_inicio, request.data_fim
        )

        padroes = []

        if df.empty or df["Timestamp"].isna().all():
            return analysis_pb2.ResultadoPadroes(
                zona=request.zona, tipo_dado=request.tipo_dado,
                padroes=padroes, timestamp=datetime.now().isoformat()
            )

        df = df.dropna(subset=["Timestamp"])
        df["hora"] = df["Timestamp"].dt.hour

        # Hora de pico (maior média horária)
        medias_hora = df.groupby("hora")["Valor"].mean()
        if not medias_hora.empty:
            hora_pico = int(medias_hora.idxmax())
            valor_pico = float(medias_hora.max())
            valor_global = float(df["Valor"].mean())
            desvio_relativo = abs(valor_pico - valor_global) / (valor_global + 1e-9)
            confianca = min(1.0, desvio_relativo * 2)
            padroes.append(analysis_pb2.Padrao(
                descricao=f"Pico diário às {hora_pico:02d}h (média={valor_pico:.1f})",
                confianca=round(confianca, 2),
                hora_pico=f"{hora_pico:02d}:00"
            ))

        # Hora de mínimo
        if not medias_hora.empty:
            hora_min = int(medias_hora.idxmin())
            valor_min = float(medias_hora.min())
            padroes.append(analysis_pb2.Padrao(
                descricao=f"Mínimo diário às {hora_min:02d}h (média={valor_min:.1f})",
                confianca=round(min(1.0, len(df) / 100), 2),
                hora_pico=f"{hora_min:02d}:00"
            ))

        # Taxa de alarmes
        taxa_alarmes = float(df["IsAlarm"].mean()) if "IsAlarm" in df.columns else 0.0
        if taxa_alarmes > 0.05:
            padroes.append(analysis_pb2.Padrao(
                descricao=f"Alta taxa de alarmes: {taxa_alarmes*100:.1f}% das leituras",
                confianca=round(min(1.0, taxa_alarmes * 5), 2),
                hora_pico=""
            ))

        # Tendência crescente/decrescente (regressão linear simples)
        if len(df) >= 10:
            x = np.arange(len(df))
            y = df["Valor"].values
            coef = np.polyfit(x, y, 1)
            slope = coef[0]
            if abs(slope) > 0.01:
                direcao = "crescente" if slope > 0 else "decrescente"
                confianca_tendencia = min(1.0, abs(slope) * 10)
                padroes.append(analysis_pb2.Padrao(
                    descricao=f"Tendência {direcao} (Δ={slope:+.3f}/leitura)",
                    confianca=round(confianca_tendencia, 2),
                    hora_pico=""
                ))

        return analysis_pb2.ResultadoPadroes(
            zona=request.zona,
            tipo_dado=request.tipo_dado,
            padroes=padroes,
            timestamp=datetime.now().isoformat()
        )

    def PreviRisco(self, request, context):
        df = self._carregar_dados(request.zona, request.tipo_dado, request.sensor_id, "", "")

        if df.empty or len(df) < 5:
            return analysis_pb2.ResultadoPrevisao(
                zona=request.zona,
                tipo_dado=request.tipo_dado,
                valores_previstos=[],
                risco_saude=0.0,
                recomendacao="Dados insuficientes para previsão (mínimo 5 leituras).",
                timestamp=datetime.now().isoformat()
            )

        valores = df["Valor"].values
        n = len(valores)
        horas = request.horas_futuras if request.horas_futuras > 0 else 6

        # Regressão linear para extrapolação
        x = np.arange(n)
        coef = np.polyfit(x, valores, 1)
        slope, intercept = coef

        # Estimar quantas leituras por hora (usar timestamps se disponíveis)
        leituras_por_hora = 12  # default
        df_ts = df.dropna(subset=["Timestamp"])
        if len(df_ts) >= 2:
            span = (df_ts["Timestamp"].max() - df_ts["Timestamp"].min()).total_seconds()
            if span > 0:
                leituras_por_hora = max(1, int(n / (span / 3600)))

        passos_futuros = horas * leituras_por_hora
        x_futuro = np.arange(n, n + passos_futuros)
        previstos = np.polyval(coef, x_futuro)

        # Amortizar para que não extrapole demais — misturar com média recente
        media_recente = float(np.mean(valores[-min(20, n):]))
        previstos = previstos * 0.6 + media_recente * 0.4

        # Calcular risco com base no valor previsto médio
        valor_medio_previsto = float(np.mean(previstos))
        risco = _calcular_risco(request.tipo_dado.upper(), valor_medio_previsto,
                                float(np.std(valores)), float(df["IsAlarm"].mean()) if "IsAlarm" in df.columns else 0.0)

        recomendacao = _gerar_recomendacao(request.tipo_dado.upper(), risco, valor_medio_previsto, slope)

        # Devolver apenas um valor previsto por hora (sub-amostrado)
        step = max(1, passos_futuros // horas)
        previstos_por_hora = [round(float(previstos[i * step]), 2) for i in range(horas) if i * step < len(previstos)]

        return analysis_pb2.ResultadoPrevisao(
            zona=request.zona,
            tipo_dado=request.tipo_dado,
            valores_previstos=previstos_por_hora,
            risco_saude=round(risco, 3),
            recomendacao=recomendacao,
            timestamp=datetime.now().isoformat()
        )


def _calcular_risco(tipo: str, media: float, desvio: float, taxa_alarmes: float) -> float:
    thresh = RISCO_THRESHOLDS.get(tipo)
    if thresh is None:
        return min(1.0, taxa_alarmes * 2)

    # Tipos onde alto valor = maior risco
    if tipo in ("TEMP", "CO2", "RUIDO", "PART"):
        if media >= thresh["alto"]:   risco_valor = 1.0
        elif media >= thresh["medio"]: risco_valor = 0.6
        elif media >= thresh["baixo"]: risco_valor = 0.3
        else:                          risco_valor = 0.0
    # Tipos onde baixo valor = maior risco (HUM, LUMIN)
    else:
        if media <= thresh["alto"]:   risco_valor = 1.0
        elif media <= thresh["medio"]: risco_valor = 0.6
        elif media <= thresh["baixo"]: risco_valor = 0.3
        else:                          risco_valor = 0.0

    # Desvio elevado agrava o risco
    risco_variabilidade = min(0.3, desvio / (abs(media) + 1) * 0.5)
    # Taxa de alarmes histórica agrava o risco
    risco_alarmes = min(0.2, taxa_alarmes)

    return min(1.0, risco_valor + risco_variabilidade + risco_alarmes)


def _gerar_recomendacao(tipo: str, risco: float, media: float, tendencia: float) -> str:
    labels = {
        "TEMP":  ("temperatura", "°C"),
        "HUM":   ("humidade",    "%"),
        "CO2":   ("CO₂",         "ppm"),
        "RUIDO": ("ruído",       "dB"),
        "LUMIN": ("luminosidade","lux"),
        "PART":  ("partículas",  "µg/m³"),
    }
    nome, unidade = labels.get(tipo, (tipo.lower(), ""))

    if risco >= 0.8:
        base = f"RISCO ALTO: {nome} prevista em {media:.1f}{unidade}. Intervenção imediata recomendada."
    elif risco >= 0.5:
        base = f"RISCO MÉDIO: {nome} prevista em {media:.1f}{unidade}. Monitorizar com atenção."
    elif risco >= 0.2:
        base = f"RISCO BAIXO: {nome} prevista em {media:.1f}{unidade}. Dentro dos parâmetros normais."
    else:
        base = f"SEM RISCO: {nome} prevista em {media:.1f}{unidade}. Condições ideais."

    if abs(tendencia) > 0.05:
        direcao = "aumento" if tendencia > 0 else "diminuição"
        base += f" Tendência de {direcao} detectada."

    return base


def serve():
    global _pool
    _pool = pg_pool.ThreadedConnectionPool(minconn=1, maxconn=4, dsn=DATABASE_URL)

    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    analysis_pb2_grpc.add_AnaliseServiceServicer_to_server(AnaliseServicer(), server)
    server.add_insecure_port(f"[::]:{PORT}")
    server.start()
    print(f"[ONE HEALTH] Serviço de Análise activo na porta {PORT}")
    server.wait_for_termination()
    _pool.closeall()


if __name__ == "__main__":
    serve()
