export function sortBy<T>(items: T[], key: (t: T) => number, order: "asc" | "desc" = "asc"): T[] {
  return items.sort((a, b) => key(a) - key(b));
}
