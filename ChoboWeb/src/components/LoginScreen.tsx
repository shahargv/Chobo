import { useState } from "react";
import { KeyRound } from "lucide-react";
export function LoginScreen({ onLogin }: { onLogin: (token: string, remembered: boolean) => void }) {
  const [token, setToken] = useState("");
  const [remembered, setRemembered] = useState(false);
  return (
    <div className="login-screen">
      <form className="login-panel" onSubmit={(event) => {
        event.preventDefault();
        if (token.trim()) onLogin(token, remembered);
      }}>
        <div className="brand-mark large">C</div>
        <h1>Chobo</h1>
        <p>Paste an existing Chobo access token to manage backups, restores, policies, and schedules.</p>
        <label>
          Access token
          <input value={token} onChange={(event) => setToken(event.target.value)} type="password" autoFocus />
        </label>
        <label className="checkbox-row">
          <input type="checkbox" checked={remembered} onChange={(event) => setRemembered(event.target.checked)} />
          Remember this browser
        </label>
        <button className="primary" type="submit"><KeyRound size={16} /> Sign in</button>
      </form>
    </div>
  );
}

