import { useEffect, useMemo, useState } from "react";
import { api, type TopicNode, type TopicTree } from "../api";
import { topicLabels } from "../format";

/**
 * The topic tree is loaded once and shared by every page that labels or filters by topic.
 * Editing the tree calls {@link invalidateTopics}, which reloads it for everyone mounted.
 */
let cache: Promise<TopicTree> | null = null;
const listeners = new Set<() => void>();

function load(): Promise<TopicTree> {
  if (!cache) {
    cache = api.topics().catch((err) => {
      cache = null;
      throw err;
    });
  }
  return cache;
}

export function invalidateTopics() {
  cache = null;
  listeners.forEach((notify) => notify());
}

export interface TopicOption {
  topic: TopicNode;
  depth: number;
  /** «Payment › Port error» */
  path: string;
}

/** Depth-first order, siblings by sort order then title — the order the tree page shows. */
export function flattenTopics(topics: TopicNode[]): TopicOption[] {
  const children = new Map<string | null, TopicNode[]>();
  for (const topic of topics) {
    const key = topic.parent_id ?? null;
    children.set(key, [...(children.get(key) ?? []), topic]);
  }
  for (const list of children.values())
    list.sort((a, b) => a.sort_order - b.sort_order || a.title.localeCompare(b.title, "en"));

  const out: TopicOption[] = [];
  const seen = new Set<string>();
  const walk = (parent: string | null, depth: number, prefix: string) => {
    for (const topic of children.get(parent) ?? []) {
      if (seen.has(topic.id)) continue;
      seen.add(topic.id);
      const path = prefix ? `${prefix} › ${topic.title}` : topic.title;
      out.push({ topic, depth, path });
      walk(topic.id, depth + 1, path);
    }
  };
  walk(null, 0, "");
  return out;
}

export function useTopics() {
  const [tree, setTree] = useState<TopicTree | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const notify = () => setNonce((n) => n + 1);
    listeners.add(notify);
    return () => {
      listeners.delete(notify);
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    load()
      .then((result) => {
        if (!cancelled) {
          setTree(result);
          setError(null);
        }
      })
      .catch((err) => {
        if (!cancelled) setError(err);
      });
    return () => {
      cancelled = true;
    };
  }, [nonce]);

  return useMemo(() => {
    const options = flattenTopics(tree?.topics ?? []);
    const byCode = new Map(options.map((o) => [o.topic.code, o]));
    return {
      tree,
      error,
      loading: !tree && !error,
      options,
      /** Active top-level topics: the values analyses store as `category`. */
      categories: options.filter((o) => o.depth === 0 && o.topic.is_active),
      /** Everything beneath a top-level topic, in tree order (DFS keeps a branch contiguous). */
      branchOf: (rootCode?: string | null) => {
        const start = options.findIndex((o) => o.depth === 0 && o.topic.code === rootCode);
        if (start < 0) return [];
        const out: TopicOption[] = [];
        for (let i = start + 1; i < options.length && options[i].depth > 0; i++) out.push(options[i]);
        return out;
      },
      byCode,
      /** Operator-facing title for a stored topic code; old codes fall back to the static map. */
      label: (code?: string | null) => {
        if (!code) return "—";
        if (code === "unclassified") return "Uncategorized";
        return byCode.get(code)?.topic.title ?? topicLabels[code] ?? code;
      },
      /** Full path for a category/subcategory pair, e.g. «Payment › Port error». */
      pathLabel: (category?: string | null, subcategory?: string | null) => {
        const node = (subcategory && byCode.get(subcategory)) || (category && byCode.get(category));
        if (node) return node.path;
        return category ? (topicLabels[category] ?? category) : "—";
      },
    };
  }, [tree, error]);
}
