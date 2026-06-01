import { useState } from "react";
import { api, auth } from "../api";

interface Props {
  onLogin: (username: string) => void;
}

export default function Login({ onLogin }: Props) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [erro,     setErro]     = useState("");
  const [loading,  setLoading]  = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!username || !password) { setErro("Preencha todos os campos."); return; }
    setLoading(true);
    setErro("");
    try {
      const res = await api.login(username, password);
      auth.setToken(res.token);
      onLogin(res.username);
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : "";
      setErro(msg.includes("401") ? "Credenciais inválidas." : "Servidor indisponível.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="login-wrap">
      <div className="login-card">
        <div className="login-brand">
          <span className="login-icon">🌿</span>
          <h1>ONE HEALTH</h1>
          <p>Monitorização Ambiental</p>
        </div>
        <form className="login-form" onSubmit={handleSubmit}>
          <label>
            Utilizador
            <input
              type="text"
              value={username}
              onChange={e => setUsername(e.target.value)}
              autoFocus
              autoComplete="username"
            />
          </label>
          <label>
            Palavra-passe
            <input
              type="password"
              value={password}
              onChange={e => setPassword(e.target.value)}
              autoComplete="current-password"
            />
          </label>
          {erro && <p className="login-erro">{erro}</p>}
          <button className="btn login-btn" type="submit" disabled={loading}>
            {loading ? "A entrar…" : "Entrar"}
          </button>
        </form>
      </div>
    </div>
  );
}
