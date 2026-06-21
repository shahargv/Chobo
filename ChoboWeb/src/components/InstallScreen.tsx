import { useState } from "react";
import { Check, Clipboard, KeyRound, ShieldCheck } from "lucide-react";
import type { InstallResponse } from "../api/generated";

export function InstallScreen({ onInstall }: { onInstall: () => Promise<InstallResponse> }) {
  const [installing, setInstalling] = useState(false);
  const [installed, setInstalled] = useState<InstallResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [copyError, setCopyError] = useState<string | null>(null);

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
    setCopyError(null);
    try {
      await copyTextToClipboard(installed.accessToken);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2500);
    } catch {
      setCopied(false);
      setCopyError("Could not copy the token. Select it and copy it manually.");
    }
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
          {copyError && <div className="field-error">{copyError}</div>}
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

export async function copyTextToClipboard(text: string) {
  if (navigator.clipboard?.writeText && window.isSecureContext) {
    try {
      await navigator.clipboard.writeText(text);
      return;
    } catch {
      // Fall back for browsers that expose the API but deny writes in this context.
    }
  }

  if (copyTextWithTextArea(text)) return;

  throw new Error("Clipboard copy failed.");
}

function copyTextWithTextArea(text: string) {
  const textArea = document.createElement("textarea");
  textArea.value = text;
  textArea.setAttribute("readonly", "");
  textArea.style.position = "fixed";
  textArea.style.top = "0";
  textArea.style.left = "-9999px";
  document.body.appendChild(textArea);
  textArea.focus();
  textArea.select();

  try {
    return document.execCommand("copy");
  } finally {
    document.body.removeChild(textArea);
  }
}
