# WSGM.Deelevate

This user-facing Steam launch wrapper runs a game at medium integrity when elevated Steam would make
the game incompatible.

- Preserve Steam's command, arguments, environment, and working directory exactly.
- The elevated wrapper must remain alive for the target lifetime and stop the target tree if Steam
  terminates the wrapper. Do not replace it with a fire-and-forget scheduled task or Explorer shortcut.
- The scheduled-task XML is UTF-16, uses `InteractiveToken`, and must never use `/NoUACCheck`.
