"""
ONE HEALTH — Serviço de Análise e Previsão (gRPC / Python)
Invocado pelo Servidor Central para análise estatística e deteção de padrões.

Gerar código gRPC a partir do proto (executar uma vez):
    python -m grpc_tools.protoc -I. --python_out=. --grpc_python_out=. analysis.proto
"""

import grpc
from concurrent import futures
import analysis_pb2
import analysis_pb2_grpc
import sqlite3
import pandas as pd
import numpy as np
from datetime import datetime

# Caminho para a base de dados do servidor (ajustar conforme necessário)
DB_PATH = "../Server/ServerData.db"
PORT    = 50052


class AnaliseServicer(analysis_pb2_grpc.AnaliseServiceServicer):

    def _carregar_dados(self, zona, tipo_dado, sensor_id, data_inicio, data_fim):
        """Carrega dados da DB com os filtros do pedido."""
        conn = sqlite3.connect(DB_PATH)
        query = "SELECT Valor, Timestamp, IsAlarm FROM Dados WHERE 1=1"
        params = []

        if zona:
            query += " AND Zona = ?";     params.append(zona)
        if tipo_dado:
            query += " AND TipoDado = ?"; params.append(tipo_dado)
        if sensor_id:
            query += " AND SensorId = ?"; params.append(sensor_id)
        if data_inicio:
            query += " AND Timestamp >= ?"; params.append(data_inicio)
        if data_fim:
            query += " AND Timestamp <= ?";  params.append(data_fim)

        df = pd.read_sql_query(query, conn, params=params)
        conn.close()
        df["Valor"] = pd.to_numeric(df["Valor"], errors="coerce")
        return df.dropna(subset=["Valor"])

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
        # TODO: implementar deteção de padrões temporais (hora de pico, correlações)
        return analysis_pb2.ResultadoPadroes(
            zona      = request.zona,
            tipo_dado = request.tipo_dado,
            padroes   = [],
            timestamp = datetime.now().isoformat()
        )

    def PreviRisco(self, request, context):
        # TODO: implementar previsão com base em tendência histórica
        return analysis_pb2.ResultadoPrevisao(
            zona              = request.zona,
            tipo_dado         = request.tipo_dado,
            valores_previstos = [],
            risco_saude       = 0.0,
            recomendacao      = "Análise de previsão ainda não implementada.",
            timestamp         = datetime.now().isoformat()
        )


def serve():
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    analysis_pb2_grpc.add_AnaliseServiceServicer_to_server(AnaliseServicer(), server)
    server.add_insecure_port(f"[::]:{PORT}")
    server.start()
    print(f"[ONE HEALTH] Serviço de Análise activo na porta {PORT}")
    server.wait_for_termination()


if __name__ == "__main__":
    serve()
