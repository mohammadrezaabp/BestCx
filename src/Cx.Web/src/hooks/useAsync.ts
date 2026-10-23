import { useCallback, useEffect, useRef, useState } from "react";

/** Ignore obsolete requests and clear previous query results when filters change. */
export function useAsync<T>(loader: () => Promise<T>, deps: unknown[], { keepDataOnReload = false } = {}) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [nonce, setNonce] = useState(0);
  const loaderRef = useRef(loader);
  const previousDeps = useRef(deps);
  loaderRef.current = loader;
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    const changed = previousDeps.current.length !== deps.length || deps.some((value, index) => !Object.is(value, previousDeps.current[index]));
    previousDeps.current = deps;
    if (changed || !keepDataOnReload) setData(null);
    const load = loaderRef.current;
    Promise.resolve()
      .then(load)
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((err) => {
        if (!cancelled) setError(err);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [...deps, nonce]);
  const reload = useCallback(() => setNonce((value) => value + 1), []);
  return { data, error, loading, reload };
}

/** One atomic history avoids updating cursor state from another state's updater. */
export function useCursorPaging(initialPageSize = 20) {
  const [history, setHistory] = useState<(string | undefined)[]>([undefined]);
  const [pageSize, setPageSize] = useState(initialPageSize);
  const resize = useCallback((size: number) => { setPageSize(size); setHistory([undefined]); }, []);
  const next = useCallback((cursor?: string | null) => {
    if (cursor)
      setHistory((current) =>
        current[current.length - 1] === cursor ? current : [...current, cursor],
      );
  }, []);
  const back = useCallback(
    () =>
      setHistory((current) =>
        current.length > 1 ? current.slice(0, -1) : current,
      ),
    [],
  );
  const reset = useCallback(() => setHistory([undefined]), []);
  return {
    cursor: history[history.length - 1],
    page: history.length,
    pageSize,
    setPageSize: resize,
    canGoBack: history.length > 1,
    next,
    back,
    reset,
  };
}
