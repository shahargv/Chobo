import { useState } from "react";
import { Check, Clipboard, KeyRound, ShieldCheck } from "lucide-react";
import type { InstallResponse } from "../api/generated";

export function InstallScreen({ onInstall }: { onInstall: () => Promise<InstallResponse> }) {
  const [installing, setInstalling] = useState(false);
  const [installed, setInstalled] = useState<InstallResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const install = async () => {
    setInstalling(true);
    setError(null);
    try {
      setInstalled(await onInstall());
    } catch (err) {
      setError(err instanceof Error ? err.message : "Installation failed.");
    } finally {
      setInstalling(false);
    }
  };

  const copyToken = async () => {
    if (!installed) return;
    await navigator.clipboard.writeText(installed.accessToken);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 2500);
  };

  if (installed) {
    return (
      <div className="login-screen install-screen">
        <section className="login-panel install-panel">
          <div className="brand-mark large"><ShieldCheck size={26} /></div>
          <h1>Store this token</h1>
          <p>This is the only time Chobo will show the initial access token.</p>
          <div className="token-box install-token" aria-label="Initial access token">{installed.accessToken}</div>
          <div className="install-actions">
            <button className="secondary" type="button" onClick={copyToken}><Clipboard size={16} /> {copied ? "Copied" : "Copy token"}</button>
            <button className="primary" type="button" onClick={() => window.location.reload()}><Check size={16} /> Ready to start</button>
          </div>
          <p className="install-note">Keep it in a password manager. After this screen, sign in with the token above.</p>
        </section>
      </div>
    );
  }

  return (
    <div className="login-screen install-screen">
      <section className="login-panel install-panel">
        <div className="brand-mark large">C</div>
        <h1>Install Chobo</h1>
        <p>Welcome. Create the initial admin token to finish setup and start using Chobo.</p>
        {error && <div className="field-error">{error}</div>}
        <button className="primary" type="button" onClick={install} disabled={installing}><KeyRound size={16} /> {installing ? "Installing..." : "Install"}</button>
      </section>
    </div>
  );
}
