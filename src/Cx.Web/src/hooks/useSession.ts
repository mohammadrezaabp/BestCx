import { useCallback, useEffect, useState } from "react";
import { ApiError, api, UNAUTHORIZED_EVENT, type SessionUser } from "../api";

export type SessionState =
  | { status: "loading" }
  | { status: "signed-out"; error?: unknown }
  | { status: "signed-in"; user: SessionUser };

/** Who is using the console. A 401 from any API call signs the console out. */
export function useSession() {
  const [state, setState] = useState<SessionState>({ status: "loading" });

  const refresh = useCallback(() => {
    api
      .me()
      .then((user) => setState({ status: "signed-in", user }))
      .catch((error) =>
        setState(
          error instanceof ApiError && error.status === 401
            ? { status: "signed-out" }
            : { status: "signed-out", error },
        ),
      );
  }, []);

  useEffect(() => {
    refresh();
    const signedOut = () => setState({ status: "signed-out" });
    window.addEventListener(UNAUTHORIZED_EVENT, signedOut);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, signedOut);
  }, [refresh]);

  const signIn = useCallback((user: SessionUser) => setState({ status: "signed-in", user }), []);

  const signOut = useCallback(async () => {
    try {
      await api.logout();
    } finally {
      setState({ status: "signed-out" });
    }
  }, []);

  return { state, signIn, signOut, refresh };
}
