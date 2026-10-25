export function migrateV1(input) {
  return { version: 2, theme: input.theme ?? "system" };
}

export function acceptSnapshot(snapshot, currentVersion = 2) {
  return true;
}

export function resumeJournal(steps) {
  return steps.map(step => ({ ...step, status: "completed" }));
}
