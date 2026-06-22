import { useState } from "react";
import { Check, Clipboard, KeyRound, ShieldCheck } from "lucide-react";
import type { InstallResponse } from "../api/generated";

export function InstallScreen({ onInstall }: { onInstall: () => Promise<InstallResponse> }) {
  const [installing, setInstalling] = useState(false);
  const [installed, setInstalled] = useState<InstallResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [copyStatus, setCopyStatus] = useState<"idle" | "copied" | "failed">("idle");

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
    const copied = await copyText(installed.accessToken);
    setCopyStatus(copied ? "copied" : "failed");
    window.setTimeout(() => setCopyStatus("idle"), 2500);
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
            <button className="secondary" type="button" onClick={copyToken}><Clipboard size={16} /> {copyStatus === "copied" ? "Copied" : copyStatus === "failed" ? "Copy failed" : "Copy token"}</button>
            <button className="primary" type="button" onClick={() => window.location.reload()}><Check size={16} /> Ready to start</button>
          </div>
          {copyStatus === "failed" && <p className="field-error">Clipboard access is blocked. Select the token above and copy it manually.</p>}
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

export async function copyText(text: string) {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Fall through to the selection-based copy path.
  }

  const textArea = document.createElement("textarea");
  textArea.value = text;
  textArea.setAttribute("readonly", "");
  textArea.style.position = "fixed";
  textArea.style.top = "-1000px";
  textArea.style.opacity = "0";
  document.body.appendChild(textArea);
  textArea.select();

  try {
    return document.execCommand("copy");
  } catch {
    return false;
  } finally {
    document.body.removeChild(textArea);
  }
}
