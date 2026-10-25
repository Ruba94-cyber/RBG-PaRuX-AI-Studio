# Restore safety snapshots

Before PaRuX replaces any data during **Settings, Backup, Import backup**, it saves a copy of your current data to
`%LOCALAPPDATA%\PaRuX\backups\pre-restore-<UTC time>.zip`. If a restore step fails, PaRuX puts everything it had
already replaced back from that copy and tells you the outcome and the file path.

- **What is in it:** the same content as a normal backup of your current data (conversations, memory, settings,
  paired devices and so on). Treat it like your data.
- **Privacy:** the folder is readable only by your Windows account and SYSTEM (inherited permissions are removed). If
  PaRuX cannot apply that restriction, it does not write a snapshot and the restore does not start.
- **How many:** the newest 3 are kept (by file time); older ones are deleted automatically. They are stored outside the
  model and runtime folders and do not count against the AI storage limit.
- **Delete them now:** Settings, Backup, **Clear safety snapshots** (or `DELETE /api/v1/backup/safety-snapshots` from the
  desktop app). It waits for a restore in progress.
- **Damaged or missing data:** if some current data cannot be copied (it is missing or corrupt), PaRuX refuses the restore
  and lists it. You can confirm "Restore anyway"; the rest is still protected, but the listed parts cannot be undone if a
  later step fails.
- Only one restore runs at a time.
