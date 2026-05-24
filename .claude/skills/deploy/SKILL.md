---
name: deploy
description: Deploy the MindAttic.Vault landing page (mindattic.com/mindatticvault.htm) via MindAttic.Deploy (sibling repo). Renders this repo's README.md through the catalog template and FTPS-uploads the single-file result.
---

When invoked, run:

```
powershell -NoProfile -ExecutionPolicy Bypass -Command "cd D:\Projects\MindAttic\MindAttic.Deploy; npm run deploy -- --only mindatticvault"
```

Report the result and flag any failures.

Notes:
- Catalog entry: `MindAttic.Deploy/projects.json` -> `projects[]` slug `mindatticvault` (theme: Cyberspace).
- Credentials: `MindAttic.Deploy/secrets/ftp.json` (gitignored).
- The legacy `scripts/cli/deploy.{bat,ps1}` + `build-html.js` + `deploy.settings.json[.template]` in this repo are dead code -- do not invoke them.
