import { createContext, useContext } from "react";
import type { ChoboApiClient } from "./api/client";

export type Toast = { kind: "success" | "error"; text: string } | null;

export const ApiContext = createContext<{ api: ChoboApiClient; showToast: (toast: Toast) => void } | null>(null);

export function useApi() {
  const context = useContext(ApiContext);
  if (!context) throw new Error("Missing API context.");
  return context;
}
