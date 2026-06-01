import sqlite3
import numpy as np
import pandas as pd
from sklearn.ensemble import IsolationForest

class MotorIsolationForest:
    def __init__(self, db_path="../Server/ServerData.db"):
        self.db_path = db_path
        # Dicionário para funcionar como Cache dos modelos.
        # Evita retreinar a mesma zona se o modelo já estiver na memória.
        # Chave: "ZONA_TIPODADO" (ex: "CHAVES_NORTE_TEMP")
        self.modelos = {}

    def _extrair_features(self, janela):
        """
        Transforma a série temporal de 15 valores em 3 indicadores matemáticos
        para o Isolation Forest conseguir interpretar o tempo.
        """
        janela_y = np.array(janela)
        janela_x = np.arange(len(janela_y))

        inclinacao = np.polyfit(janela_x, janela_y, 1)[0]
        desvio_padrao = np.std(janela_y)
        
        # Delta: o salto do último valor em relação à média do passado recente
        media_anterior = np.mean(janela_y[:-1]) if len(janela_y) > 1 else janela_y[0]
        delta_final = abs(janela_y[-1] - media_anterior)

        return [inclinacao, desvio_padrao, delta_final]

    def treinar_modelo_se_necessario(self, zona, tipo_dado):
        """
        Garante que o modelo para esta zona e tipo específico está treinado.
        Faz o treino a frio (Cold Start) usando o histórico da DB.
        """
        chave = f"{zona}_{tipo_dado}"
        if chave in self.modelos:
            return True # O modelo já está em memória pronto a disparar

        # 1. Ir buscar o histórico à BD para aprender o "Normal" desta Zona
        conn = sqlite3.connect(self.db_path)
        query = "SELECT Valor FROM Dados WHERE Zona = ? AND TipoDado = ? ORDER BY Timestamp ASC"
        df = pd.read_sql_query(query, conn, params=(zona, tipo_dado))
        conn.close()

        # Precisamos de um mínimo histórico para o modelo ser fiável
        if len(df) < 50:
            return False

        df["Valor"] = pd.to_numeric(df["Valor"], errors="coerce")
        df = df.dropna()
        valores = df["Valor"].values

        # 2. Construir o Dataset de Treino (Roling Windows)
        features_treino = []
        for i in range(14, len(valores)):
            janela = valores[i-14:i+1]
            features_treino.append(self._extrair_features(janela))

        if not features_treino:
            return False

        # 3. Treinar o modelo (Não Supervisionado)
        # contamination=0.05 diz ao modelo para assumir que 5% do histórico são anomalias passadas
        modelo = IsolationForest(contamination=0.05, random_state=42)
        modelo.fit(features_treino)

        # 4. Guardar na cache
        self.modelos[chave] = modelo
        print(f"[ML] Modelo treinado a frio e em cache para: {chave}")
        return True

    def avaliar_severidade(self, zona, tipo_dado, janela_15_valores):
        """
        Recebe a janela atual e devolve (Booleano Is_Anomalia_Real, Float Severidade_0_a_100).
        """
        if len(janela_15_valores) != 15:
            return False, 0.0

        treinado = self.treinar_modelo_se_necessario(zona, tipo_dado)
        if not treinado:
            # Fallback seguro: se não temos histórico para avaliar, não gritamos "lobo"
            return False, 0.0

        chave = f"{zona}_{tipo_dado}"
        modelo = self.modelos[chave]

        # 1. Extrair os parâmetros da situação de emergência atual
        features_atuais = self._extrair_features(janela_15_valores)

        # 2. Pedir o "Anomaly Score" contínuo ao modelo
        score = modelo.decision_function([features_atuais])[0]

        # O scikit-learn define: Scores positivos = Normal | Scores negativos = Anomalia
        if score >= 0:
            return False, 0.0 # É perfeitamente normal ou um pico de isqueiro inofensivo
        
        # 3. Mapear a gravidade
        # Um score muito grave ronda os -0.5. Multiplicar por 200 mapeia isto para uma escala de 0 a 100%.
        severidade_bruta = abs(score) * 200.0
        severidade = min(100.0, severidade_bruta) # Cap nos 100%
        
        # Só declaramos anomalia real se a severidade for substancial (ex: ignora flutuações minúsculas de 5%)
        is_real = severidade > 15.0

        return is_real, round(severidade, 1)