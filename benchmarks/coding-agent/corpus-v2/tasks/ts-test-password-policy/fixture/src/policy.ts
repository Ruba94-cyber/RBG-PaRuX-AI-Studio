export function validatePassword(pw: string, username = ""): string[] {
  const problems: string[] = [];
  if (pw.length < 10) problems.push("too short");
  if (pw.length > 64) problems.push("too long");
  if (!/[a-z]/.test(pw)) problems.push("needs lowercase");
  if (!/[A-Z]/.test(pw)) problems.push("needs uppercase");
  if (!/[0-9]/.test(pw)) problems.push("needs digit");
  if (username.length >= 3 && pw.toLowerCase().includes(username.toLowerCase())) problems.push("contains username");
  return problems;
}
