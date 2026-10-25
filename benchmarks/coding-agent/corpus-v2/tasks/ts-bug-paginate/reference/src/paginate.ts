export interface Page {
  page: number;
  pageSize: number;
  totalPages: number;
  offset: number;
  limit: number;
  hasPrev: boolean;
  hasNext: boolean;
}

export function paginate(total: number, page: number, pageSize: number): Page {
  if (!Number.isInteger(pageSize) || pageSize < 1) {
    throw new RangeError("pageSize must be a positive integer");
  }
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const clamped = Math.min(Math.max(page, 1), totalPages);
  const offset = (clamped - 1) * pageSize;
  const limit = Math.max(0, Math.min(pageSize, total - offset));
  return { page: clamped, pageSize, totalPages, offset, limit, hasPrev: clamped > 1, hasNext: clamped < totalPages };
}
