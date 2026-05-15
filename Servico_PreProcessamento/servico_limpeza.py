import grpc
from concurrent import futures
import limpeza_pb2
import limpeza_pb2_grpc


class ServicoLimpeza(limpeza_pb2_grpc.LimpezaDadosServicer):

    def UniformizarBloco(self, request, context):
        print(
            f"\n[gRPC Recebido] Bloco com {len(request.dados)} dados para pré-processar.")

        bloco_resposta = limpeza_pb2.BlocoDadosLimpos()

        for dado_bruto in request.dados:
            dado_limpo = bloco_resposta.dados.add()
            dado_limpo.sensor_id = dado_bruto.sensor_id
            print(
                f"Sensor: {dado_bruto.sensor_id} | Variável: {dado_bruto.tipo_dado}")
            print(
                f"Entrada : '{dado_bruto.valor_sujo}'")

            try:
                numero = float(dado_bruto.valor_sujo)

                dado_limpo.sucesso = True
                dado_limpo.valor_limpo = round(numero, 2)

                print(
                    f"Saída   : {dado_limpo.valor_limpo}")

            except Exception as e:
                dado_limpo.sucesso = False
                dado_limpo.mensagem_erro = str(e)
                print(
                    f"[Erro no sensor {dado_bruto.sensor_id}]: {dado_bruto.valor_sujo}")

        print(f"[Sucesso] Bloco processado e devolvido ao Gateway!")
        return bloco_resposta


def serve():
    servidor = grpc.server(futures.ThreadPoolExecutor(max_workers=10))
    limpeza_pb2_grpc.add_LimpezaDadosServicer_to_server(
        ServicoLimpeza(), servidor)
    servidor.add_insecure_port('[::]:50051')
    servidor.start()
    print("Protótipo Python a correr na porta 50051...")
    servidor.wait_for_termination()


if __name__ == '__main__':
    serve()
