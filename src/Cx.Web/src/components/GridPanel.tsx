import { Children, isValidElement, type ComponentPropsWithoutRef, type HTMLAttributes, type ReactNode } from "react";
import { Empty, ErrorBox, Loading, Pager } from "./Common";
import Pagination from "./Pagination";

/** One frame and state layout for every table, including embedded and settings grids. */
export default function GridPanel({ children, className = "", title, actions, filters, footer, loading, error, empty, emptyText, ...props }:
  Omit<HTMLAttributes<HTMLElement>, "title"> & {
    title?: ReactNode; actions?: ReactNode; filters?: ReactNode; footer?: ReactNode;
    loading?: boolean; error?: unknown; empty?: boolean; emptyText?: string;
  }) {
  const nodes = Children.toArray(children);
  const isPager = (node: ReactNode) => isValidElement(node) && (node.type === Pagination || node.type === Pager);
  const paging = footer ?? nodes.filter(isPager);
  return <>{filters}<section {...props} className={`grid-panel ${className}`} aria-busy={loading || props["aria-busy"]}>
    {(title || actions) && <header className="grid-panel-heading">{title && <h2>{title}</h2>}{actions && <div className="grid-panel-actions">{actions}</div>}</header>}
    <div className="grid-panel-body"><ErrorBox error={error} />{loading ? <Loading /> : empty ? <Empty text={emptyText} /> : nodes.filter(node => !isPager(node))}</div>
    {Children.count(paging) > 0 && <footer className="grid-panel-footer">{paging}</footer>}
  </section></>;
}

type FilterProps = ({ as: "form" } & ComponentPropsWithoutRef<"form">) | ({ as?: "div" } & ComponentPropsWithoutRef<"div">);
export function GridFilters(props: FilterProps) {
  if (props.as === "form") {
    const { as: Element, className = "", ...rest } = props;
    return <Element {...rest} className={`grid-filter-panel ${className}`} />;
  }
  const { as: Element = "div", className = "", ...rest } = props;
  return <Element {...rest} className={`grid-filter-panel ${className}`} />;
}
