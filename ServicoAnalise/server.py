"""
ONE HEALTH - Servico de Analise e Previsao (gRPC / Python)

Deteta anomalias (Isolation Forest), calcula estatisticas e padroes, e preve risco.
Os modelos sao treinados a partir da base de dados.

Gerar o codigo gRPC a partir do proto (executar uma vez):
    python -m grpc_tools.protoc -I. --python_out=. --grpc_python_out=. analysis.proto
"""

import grpc
import os
import time
import threading
from concurrent import futures
from collections import defaultdict

import analysis_pb2
import analysis_pb2_grpc
import psycopg2
from psycopg2 import pool as pg_pool
import pandas as pd
import numpy as np
from sklearn.ensemble import IsolationForest
from datetime import datetime, timezone

DATABASE_URL = os.environ.get("DATABASE_URL", "postgresql://postgres:postgres@localhost/one_health")
PORT         = 50052

_pool: pg_pool.ThreadedConnectionPool = None

# Modelos Isolation Forest, um por tipo de dado.
_models: dict       = {}   # tipo -> modelo treinado
_model_lock         = threading.Lock()
TIPOS_SUPORTADOS    = ["TEMP", "HUM", "CO2", "RUIDO", "LUMIN", "PART", "NO2", "O3", "WIND"]
MIN_AMOSTRAS        = 1440  # numero minimo de amostras para o modelo ser considerado aquecido
RETRAIN_INTERVAL    = 300  # segundos entre retreinos em segundo plano
CYCLE_SECS          = 7200  # duracao do dia simulado, em segundos reais (2 horas)


# Converte um instante na hora do dia simulado (0 a 24), para dar contexto temporal ao modelo.
def _hora_simulada_de_dt(dt: datetime) -> float:
    return (int(dt.timestamp()) % CYCLE_SECS) / CYCLE_SECS * 24.0

# Carrega amostras de treino da base de dados para um tipo. Cada amostra e
# [valor, seno da hora, cosseno da hora].
def _carregar_amostras_treino(tipo: str, limit: int = 2000) -> np.ndarray:
    conn = _pool.getconn()
    try:
        df = pd.read_sql_query(
            "SELECT valor, "
            "(EXTRACT(epoch FROM timestamp)::bigint %% %s) / %s::float * 24 AS sim_h "
            "FROM leituras "
            "WHERE tipo_dado = %s AND is_alarm = FALSE "
            "ORDER BY timestamp DESC LIMIT %s",
            conn, params=[CYCLE_SECS, CYCLE_SECS, tipo, limit]
        )
    finally:
        _pool.putconn(conn)
    df = df.dropna(subset=["valor", "sim_h"])
    vals  = df["valor"].values
    sim_h = df["sim_h"].values
    sin_h = np.sin(2 * np.pi * sim_h / 24.0)
    cos_h = np.cos(2 * np.pi * sim_h / 24.0)
    return np.column_stack([vals, sin_h, cos_h])

# Treina um modelo por tipo, mas so se houver amostras suficientes.
def _treinar_modelos():
    for tipo in TIPOS_SUPORTADOS:
        try:
            X = _carregar_amostras_treino(tipo)
            if len(X) < MIN_AMOSTRAS:
                continue
            modelo = IsolationForest(n_estimators=100, contamination="auto", random_state=42, n_jobs=-1)
            modelo.fit(X)
            with _model_lock:
                _models[tipo] = modelo
            print(f"[ML] Modelo {tipo} treinado com {len(X)} amostras.", flush=True)
        except Exception as e:
            print(f"[ML] Erro a treinar {tipo}: {e}", flush=True)

# Ciclo que retreina os modelos periodicamente em segundo plano.
def _loop_retreino():
    time.sleep(30)
    while True:
        _treinar_modelos()
        time.sleep(RETRAIN_INTERVAL)

# Limiares de risco por tipo. baixo/medio/alto definem os escaloes de risco.
LIMITES_MIN = {
    "TEMP": -50.0, "HUM": 0.0, "CO2": 0.0, "RUIDO": 0.0,
    "LUMIN": 0.0,  "PART": 0.0, "NO2": 0.0, "O3": 0.0, "WIND": 0.0,
}

RISCO_THRESHOLDS = {
    "TEMP":  {"baixo": 28.0,   "medio": 33.0,   "alto": 38.0},
    "HUM":   {"baixo": 30.0,   "medio": 20.0,   "alto": 10.0},   # invertido
    "CO2":   {"baixo": 800.0,  "medio": 1200.0, "alto": 1800.0},
    "RUIDO": {"baixo": 60.0,   "medio": 75.0,   "alto": 85.0},
    "LUMIN": {"baixo": 200.0,  "medio": 50.0,   "alto": 10.0},   # invertido
    "PART":  {"baixo": 35.0,   "medio": 75.0,   "alto": 150.0},
    "NO2":   {"baixo": 100.0,  "medio": 200.0,  "alto": 400.0},
    "O3":    {"baixo": 60.0,   "medio": 120.0,  "alto": 200.0},
    "WIND":  {"baixo": 50.0,   "medio": 75.0,   "alto": 100.0},
}


class AnaliseServicer(analysis_pb2_grpc.AnaliseServiceServicer):

    # Carrega as leituras da base de dados conforme os filtros (zona, tipo, sensor, datas).
    def _carregar_dados(self, zona, tipo_dado, sensor_id, data_inicio, data_fim):
        query = "SELECT valor, timestamp, is_alarm FROM leituras WHERE 1=1"
        params = []
        if zona:        query += " AND zona = %s";       params.append(zona)
        if tipo_dado:   query += " AND tipo_dado = %s";  params.append(tipo_dado)
        if sensor_id:   query += " AND sensor_id = %s";  params.append(sensor_id)
        if data_inicio: query += " AND timestamp >= %s"; params.append(data_inicio)
        if data_fim:    query += " AND timestamp <= %s"; params.append(data_fim)
        query += " ORDER BY timestamp DESC LIMIT 10000"

        conn = _pool.getconn()
        try:
            df = pd.read_sql_query(query, conn, params=params)
        finally:
            _pool.putconn(conn)
        df = df.rename(columns={"valor": "Valor", "timestamp": "Timestamp", "is_alarm": "IsAlarm"})
        df["Valor"]     = pd.to_numeric(df["Valor"], errors="coerce")
        df["Timestamp"] = pd.to_datetime(df["Timestamp"], errors="coerce")
        df = df.dropna(subset=["Valor"])
        return df.sort_values("Timestamp").reset_index(drop=True)

    # Pontua um lote de leituras com o modelo de cada tipo. Devolve, por leitura, um score
    # de anomalia entre 0 e 1 e se e ou nao anomalia. modelo_aquecido indica se ja havia
    # pelo menos um modelo treinado.
    def ScoreBatch(self, request, context):
        por_tipo = defaultdict(list)  # tipo -> [(indice, valor, timestamp)]
        for i, l in enumerate(request.leituras):
            por_tipo[l.tipo.upper()].append((i, l.valor, l.timestamp))

        resultados  = [None] * len(request.leituras)
        algum_modelo = False

        with _model_lock:
            snapshot = dict(_models)

        for tipo, items in por_tipo.items():
            modelo = snapshot.get(tipo)
            if modelo is None:
                continue
            algum_modelo = True

            vals, sin_h, cos_h = [], [], []
            for _, v, ts in items:
                try:
                    dt = datetime.fromisoformat(ts.replace("Z", "+00:00")) if ts else datetime.now(timezone.utc)
                except Exception:
                    dt = datetime.now(timezone.utc)
                if dt.tzinfo is None:
                    dt = dt.replace(tzinfo=timezone.utc)
                h = _hora_simulada_de_dt(dt)
                vals.append(v)
                sin_h.append(np.sin(2 * np.pi * h / 24.0))
                cos_h.append(np.cos(2 * np.pi * h / 24.0))

            X = np.column_stack([vals, sin_h, cos_h])
            try:
                decisions = modelo.decision_function(X)
            except Exception:
                continue

            print(f"[ML] {tipo}: decisions={[round(float(d),3) for d in decisions]}", flush=True)
            for j in range(len(items)):
                idx      = items[j][0]
                decision = decisions[j]
                # decision fica perto de [-0.15, +0.15]: 0.0 e a fronteira.
                # Mapeia para score: 0.0 -> 0.5, -0.15 -> 0.95, +0.15 -> 0.05.
                score       = float(np.clip(0.5 - (decision * 3.0), 0.0, 1.0))
                is_anomalia = bool(decision < 0)
                leitura     = request.leituras[idx]
                resultados[idx] = analysis_pb2.AnomaliaInfo(
                    sensor_id   = leitura.sensor_id,
                    tipo        = leitura.tipo,
                    score       = score,
                    is_anomalia = is_anomalia,
                    motivo      = f"IF score={score:.2f}" if is_anomalia else ""
                )

        anomalias = []
        for i, leitura in enumerate(request.leituras):
            anomalias.append(resultados[i] if resultados[i] is not None else
                analysis_pb2.AnomaliaInfo(
                    sensor_id=leitura.sensor_id, tipo=leitura.tipo,
                    score=0.0, is_anomalia=False, motivo=""
                ))

        return analysis_pb2.ResultadoScoreBatch(
            anomalias=anomalias,
            modelo_aquecido=algum_modelo
        )

    # Estatisticas descritivas de uma zona/tipo: media, desvio, minimo, maximo e contagens.
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


    # Deteta padroes: pico e minimo diarios, taxa de alarmes, tendencia (ajuste linear)
    # e taxa de anomalias do ML.
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
        medias_hora = df.groupby("hora")["Valor"].mean()

        if not medias_hora.empty:
            hora_pico    = int(medias_hora.idxmax())
            valor_pico   = float(medias_hora.max())
            valor_global = float(df["Valor"].mean())
            confianca    = min(1.0, abs(valor_pico - valor_global) / (valor_global + 1e-9) * 2)
            padroes.append(analysis_pb2.Padrao(
                descricao=f"Pico diário às {hora_pico:02d}h (média={valor_pico:.1f})",
                confianca=round(confianca, 2),
                hora_pico=f"{hora_pico:02d}:00"
            ))

            hora_min  = int(medias_hora.idxmin())
            valor_min = float(medias_hora.min())
            padroes.append(analysis_pb2.Padrao(
                descricao=f"Mínimo diário às {hora_min:02d}h (média={valor_min:.1f})",
                confianca=round(min(1.0, len(df) / 100), 2),
                hora_pico=f"{hora_min:02d}:00"
            ))

        taxa_alarmes = float(df["IsAlarm"].mean()) if "IsAlarm" in df.columns else 0.0
        if taxa_alarmes > 0.05:
            padroes.append(analysis_pb2.Padrao(
                descricao=f"Alta taxa de alarmes: {taxa_alarmes*100:.1f}% das leituras",
                confianca=round(min(1.0, taxa_alarmes * 5), 2),
                hora_pico=""
            ))

        if len(df) >= 10:
            x    = np.arange(len(df))
            coef = np.polyfit(x, df["Valor"].values, 1)
            if abs(coef[0]) > 0.01:
                direcao = "crescente" if coef[0] > 0 else "decrescente"
                padroes.append(analysis_pb2.Padrao(
                    descricao=f"Tendência {direcao} (Δ={coef[0]:+.3f}/leitura)",
                    confianca=round(min(1.0, abs(coef[0]) * 10), 2),
                    hora_pico=""
                ))

        conn = _pool.getconn()
        try:
            zona_filter   = f"AND zona = %s"     if request.zona     else ""
            tipo_filter   = f"AND tipo_dado = %s" if request.tipo_dado else ""
            params        = [p for p in [request.zona, request.tipo_dado] if p]
            df_ml = pd.read_sql_query(
                f"SELECT anomaly_score FROM leituras WHERE anomaly_score IS NOT NULL "
                f"{zona_filter} {tipo_filter} ORDER BY timestamp DESC LIMIT 500",
                conn, params=params
            )
        finally:
            _pool.putconn(conn)

        if not df_ml.empty:
            taxa_ml = float((df_ml["anomaly_score"] >= 0.6).mean())
            if taxa_ml > 0.05:
                padroes.append(analysis_pb2.Padrao(
                    descricao=f"ML: {taxa_ml*100:.1f}% leituras com score de anomalia ≥ 0.6",
                    confianca=round(min(1.0, taxa_ml * 4), 2),
                    hora_pico=""
                ))

        return analysis_pb2.ResultadoPadroes(
            zona=request.zona, tipo_dado=request.tipo_dado,
            padroes=padroes, timestamp=datetime.now().isoformat()
        )

    # Preve valores de curto prazo (ajuste linear misturado com a media recente) e calcula
    # um indice de risco para a saude, com uma recomendacao textual.
    def PreviRisco(self, request, context):
        df = self._carregar_dados(request.zona, request.tipo_dado, request.sensor_id, "", "")

        if df.empty or len(df) < 5:
            return analysis_pb2.ResultadoPrevisao(
                zona=request.zona, tipo_dado=request.tipo_dado,
                valores_previstos=[], risco_saude=0.0,
                recomendacao="Dados insuficientes para previsão (mínimo 5 leituras).",
                timestamp=datetime.now().isoformat()
            )

        valores = df["Valor"].values
        n       = len(valores)
        horas   = request.horas_futuras if request.horas_futuras > 0 else 6

        x    = np.arange(n)
        coef = np.polyfit(x, valores, 1)

        leituras_por_hora = 12
        df_ts = df.dropna(subset=["Timestamp"])
        if len(df_ts) >= 2:
            span = (df_ts["Timestamp"].max() - df_ts["Timestamp"].min()).total_seconds()
            if span > 0:
                leituras_por_hora = max(1, int(n / (span / 3600)))

        passos_futuros = horas * leituras_por_hora
        previstos      = np.polyval(coef, np.arange(n, n + passos_futuros))
        media_recente  = float(np.mean(valores[-min(20, n):]))
        previstos      = previstos * 0.6 + media_recente * 0.4
        previstos      = np.maximum(previstos, LIMITES_MIN.get(request.tipo_dado.upper(), 0.0))

        valor_medio   = float(np.mean(previstos))
        taxa_alarmes  = float(df["IsAlarm"].mean()) if "IsAlarm" in df.columns else 0.0
        risco         = _calcular_risco(request.tipo_dado.upper(), valor_medio,
                                        float(np.std(valores)), taxa_alarmes)
        recomendacao  = _gerar_recomendacao(request.tipo_dado.upper(), risco, valor_medio, coef[0])

        step              = max(1, passos_futuros // horas)
        previstos_por_hora = [round(float(previstos[i * step]), 2)
                              for i in range(horas) if i * step < len(previstos)]

        return analysis_pb2.ResultadoPrevisao(
            zona=request.zona, tipo_dado=request.tipo_dado,
            valores_previstos=previstos_por_hora,
            risco_saude=round(risco, 3),
            recomendacao=recomendacao,
            timestamp=datetime.now().isoformat()
        )


# Funcoes auxiliares

# Calcula o indice de risco (0 a 1) a partir da media prevista, do desvio e da taxa de
# alarmes, usando os limiares por tipo.
def _calcular_risco(tipo: str, media: float, desvio: float, taxa_alarmes: float) -> float:
    thresh = RISCO_THRESHOLDS.get(tipo)
    if thresh is None:
        return min(1.0, taxa_alarmes * 2)

    if tipo in ("TEMP", "CO2", "RUIDO", "PART", "NO2", "O3", "WIND"):
        if   media >= thresh["alto"]:  risco_valor = 1.0
        elif media >= thresh["medio"]: risco_valor = 0.6
        elif media >= thresh["baixo"]: risco_valor = 0.3
        else:                          risco_valor = 0.0
    else:  # HUM e LUMIN: invertido (valores baixos sao piores)
        if   media <= thresh["alto"]:  risco_valor = 1.0
        elif media <= thresh["medio"]: risco_valor = 0.6
        elif media <= thresh["baixo"]: risco_valor = 0.3
        else:                          risco_valor = 0.0

    return min(1.0, risco_valor
               + min(0.3, desvio / (abs(media) + 1) * 0.5)
               + min(0.2, taxa_alarmes))


# Gera a recomendacao textual conforme o escalao de risco e a tendencia.
def _gerar_recomendacao(tipo: str, risco: float, media: float, tendencia: float) -> str:
    labels = {
        "TEMP":  ("temperatura",          "°C"),
        "HUM":   ("humidade",             "%"),
        "CO2":   ("CO₂",                  "ppm"),
        "RUIDO": ("ruído",                "dB"),
        "LUMIN": ("luminosidade",         "lux"),
        "PART":  ("partículas",           "µg/m³"),
        "NO2":   ("dióxido de azoto",     "µg/m³"),
        "O3":    ("ozono",                "ppb"),
        "WIND":  ("velocidade do vento",  "km/h"),
    }
    nome, unidade = labels.get(tipo, (tipo.lower(), ""))

    if   risco >= 0.8: base = f"RISCO ALTO: {nome} prevista em {media:.1f}{unidade}. Intervenção imediata recomendada."
    elif risco >= 0.5: base = f"RISCO MÉDIO: {nome} prevista em {media:.1f}{unidade}. Monitorizar com atenção."
    elif risco >= 0.2: base = f"RISCO BAIXO: {nome} prevista em {media:.1f}{unidade}. Dentro dos parâmetros normais."
    else:              base = f"SEM RISCO: {nome} prevista em {media:.1f}{unidade}. Condições ideais."

    if abs(tendencia) > 0.05:
        base += f" Tendência de {'aumento' if tendencia > 0 else 'diminuição'} detectada."
    return base



# Arranca o servico: liga a base de dados, treina os modelos, lanca o retreino periodico
# e fica a servir pedidos gRPC na porta definida.
def serve():
    global _pool
    _pool = pg_pool.ThreadedConnectionPool(minconn=2, maxconn=8, dsn=DATABASE_URL)

    print("[ML] Treino inicial dos modelos...", flush=True)
    _treinar_modelos()

    t = threading.Thread(target=_loop_retreino, daemon=True, name="ML-Retreino")
    t.start()

    server = grpc.server(futures.ThreadPoolExecutor(max_workers=8))
    analysis_pb2_grpc.add_AnaliseServiceServicer_to_server(AnaliseServicer(), server)
    server.add_insecure_port(f"[::]:{PORT}")
    server.start()
    print(f"[ONE HEALTH] Serviço de Análise activo na porta {PORT}", flush=True)
    server.wait_for_termination()
    _pool.closeall()


if __name__ == "__main__":
    serve()
