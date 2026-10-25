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
  const totalPages = Math.floor(total / pageSize);
  const offset = page * pageSize;
  return { page, pageSize, totalPages, offset, limit: pageSize, hasPrev: page > 1, hasNext: page < totalPages };
}
