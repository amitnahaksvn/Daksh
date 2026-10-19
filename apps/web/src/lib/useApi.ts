"use client";
import { useCallback, useEffect, useRef, useState } from "react";

export type AsyncState<T> =
  | { status: "loading" }
  | { status: "error"; error: Error }
  | { status: "ready"; data: T };

/** Loads data on mount; `reload` re-fetches (used for retry and after mutations). */
export function useApi<T>(loader: () => Promise<T>) {
  const [state, setState] = useState<AsyncState<T>>({ status: "loading" });
  const loaderRef = useRef(loader);

  const fetchData = useCallback(() => {
    loaderRef.current().then(
      (data) => setState({ status: "ready", data }),
      (error) => setState({ status: "error", error }),
    );
  }, []);

  const reload = useCallback(() => {
    setState({ status: "loading" });
    fetchData();
  }, [fetchData]);

  useEffect(fetchData, [fetchData]);
  return { state, reload };
}
