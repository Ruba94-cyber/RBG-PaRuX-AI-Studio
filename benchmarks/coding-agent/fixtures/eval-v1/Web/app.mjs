export function reduceState(state, action) {
  if (action.type === "replaceConversations") {
    return { ...state, conversations: action.items, selectedId: action.items[0]?.id ?? null };
  }
  return state;
}

export function parseNdjsonChunks(chunks) {
  return chunks.flatMap(chunk => chunk.split("\n")).filter(Boolean).map(line => JSON.parse(line));
}

export function sanitizeArtifactHref(href) {
  return href;
}
