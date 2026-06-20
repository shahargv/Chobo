const SESSION_KEY = "chobo.auth.session";
const LOCAL_KEY = "chobo.auth.remembered";

export interface StoredAuth {
  token: string;
  remembered: boolean;
}

export function readStoredAuth(): StoredAuth | null {
  const local = window.localStorage.getItem(LOCAL_KEY);
  if (local) return { token: local, remembered: true };
  const session = window.sessionStorage.getItem(SESSION_KEY);
  return session ? { token: session, remembered: false } : null;
}

export function storeAuth(token: string, remembered: boolean) {
  clearAuth();
  const trimmed = token.trim();
  if (remembered) window.localStorage.setItem(LOCAL_KEY, trimmed);
  else window.sessionStorage.setItem(SESSION_KEY, trimmed);
}

export function clearAuth() {
  window.localStorage.removeItem(LOCAL_KEY);
  window.sessionStorage.removeItem(SESSION_KEY);
}
