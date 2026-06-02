FROM python:3.12-slim
WORKDIR /app
COPY ServicoAnalise/ .
RUN pip install --no-cache-dir -r requirements.txt
EXPOSE 50052
CMD ["python", "server.py"]
