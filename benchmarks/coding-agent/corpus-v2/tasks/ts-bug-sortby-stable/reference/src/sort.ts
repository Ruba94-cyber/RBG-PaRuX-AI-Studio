export function sortBy<T>(items: T[], key: (t: T) => number, order: "asc" | "desc" = "asc"): T[] {
  const sign = order === "desc" ? -1 : 1;
  return items
    .map((item, index) => ({ item, index, k: key(item) }))
    .sort((a, b) => (a.k === b.k ? a.index - b.index : sign * (a.k - b.k)))
    .map((entry) => entry.item);
}
