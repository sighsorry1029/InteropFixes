# Archived ServerSync input

2026-11-09: InteropFixes removed ServerSync compilation, merging and runtime
initialization when ordinary integrations became optional across endpoints.
This directory retains the reviewed input, license and manifest as historical
evidence; current builds do not consume or deploy it. Cosmetic support uses its
own v2 capability agreement instead of a global mandatory-install/version check.

- Pinned common baseline: `valheim-1.0.7-r1`, prepared 2026-09-09.
- Upstream: https://github.com/blaxxun-boop/ServerSync, commit
  `c57c2aa54e07cdcc7630d6068699ea781622323e`.
- License: MIT-0; original text is `ServerSync.LICENSE.txt`.
- Vendored artifact: `ServerSync.dll`, SHA-256
  `b4dd786997f4e90d770f09ef3e9d64154754fe7e8edfb4841795751895b35846`.
- Assembly identity remains ServerSync 1.0.0.0; file version 1.0.0.1.
- Original baseline manifest is preserved in `ServerSync.manifest.json`.
- Source/reproduction and comparison baseline are preserved at
  `C:/Users/blizz/.codex/references/valheim/integrations/serversync/versions/valheim-1.0.7-r1`.

The common baseline was compiled against original Valheim DLLs. It replaces
obsolete Everybody field access with the original game's constant, uses public
ZNet.IsAdmin, and preserves buffered PeerInfo/player-list/time message order.
The earlier mandatory-install consumer used the fixed DLL directly and
internalized it into InteropFixes.dll. No additional IL rewrite or standalone
ServerSync deployment was performed. The retained file is not an active build
input, and general builds do not copy a new file from the global reference folder.

InteropFixes compiles against installed original Valheim 1.0.21. On 2026-11-07
the installed assembly_valheim.dll matched the preserved original client
b26249040 snapshot, SHA-256
`9f959c0396ec4240d47fcbe5d0d062c428cc627244506db5ea567f9d77b2b769`.
The common baseline's static/isolated checks are not a claim of live consumer
compatibility with hosts, dedicated servers or Steam/PlayFab crossplay.
