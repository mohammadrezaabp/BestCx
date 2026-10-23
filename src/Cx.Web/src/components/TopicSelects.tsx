import type { TopicOption } from "../hooks/useTopics";

/** Resolve a stored descendant code to the root shown by the first select. */
export function rootTopicCode(options: TopicOption[], code: string): string | undefined {
  let root: string | undefined;
  for (const { topic, depth } of options) {
    if (depth === 0) root = topic.code;
    if (topic.code === code) return root;
  }
}

export default function TopicSelects({
  options,
  parentValue,
  childValue,
  onParentChange,
  onChildChange,
  className = "grid-filter",
  required = false,
  extraParents = [],
}: {
  options: TopicOption[];
  parentValue: string;
  childValue: string;
  onParentChange: (value: string) => void;
  onChildChange: (value: string) => void;
  className?: string;
  required?: boolean;
  extraParents?: { value: string; label: string }[];
}) {
  const parents = options.filter(({ depth }) => depth === 0);
  const parent = parents.find(({ topic }) => topic.code === parentValue);
  const children: TopicOption[] = [];
  const start = parent ? options.indexOf(parent) : -1;
  if (start >= 0) {
    for (let i = start + 1; i < options.length && options[i].depth > 0; i++) children.push(options[i]);
  }
  const missingParent = parentValue && !parent && !extraParents.some(o => o.value === parentValue);
  return (
    <>
      <label className={className}>
        <span>Main category</span>
        <select aria-label="Main category" name="parent_category" required={required} value={parentValue}
          onChange={event => onParentChange(event.target.value)}>
          <option value="" disabled={required}>{required ? "Selecting the main category" : "All categories"}</option>
          {parents.map(({ topic }) => <option key={topic.code} value={topic.code}>
            {topic.title}{topic.is_active ? "" : " (Disabled)"}
          </option>)}
          {extraParents.filter(o => !parents.some(({ topic }) => topic.code === o.value))
            .map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
          {missingParent && <option value={parentValue}>{parentValue}</option>}
        </select>
      </label>
      <label className={className}>
        <span>Subcategory</span>
        <select aria-label="Subcategory" name="category" required={required}
          disabled={!parent || !children.length} value={childValue}
          onChange={event => onChildChange(event.target.value)}>
          <option value="" disabled={required}>
            {!parent ? "First select the main category"
              : !children.length ? "It has no subcategories"
              : required ? "Subcategory selection" : "All subcategories"}
          </option>
          {children.map(({ topic, path }) => <option key={topic.code} value={topic.code}>
            {path.slice(parent!.path.length + " › ".length)}{topic.is_active ? "" : " (Disabled)"}
          </option>)}
          {childValue && !children.some(({ topic }) => topic.code === childValue)
            && <option value={childValue}>{childValue}</option>}
        </select>
      </label>
    </>
  );
}
