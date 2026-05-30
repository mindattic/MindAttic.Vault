Deploy the MindAttic.Vault landing page (`mindattic.com/mindatticvault.htm`) via **MindAttic.Deploy** (sibling repo at `D:\Projects\MindAttic\MindAttic.Deploy`).

Renders this repo's `README.md` through the catalog template (`template/index.template.htm`, Cyberspace theme, MindAttic.UiUx components loaded via jsDelivr) and FTPS-uploads the single-file result. One repo owns the whole FTP pipeline — there is no per-project deploy state in this folder.

Run this command and report the result:

```
powershell -NoProfile -ExecutionPolicy Bypass -Command "cd D:\Projects\MindAttic\MindAttic.Deploy; npm run deploy -- --only mindatticvault"
```

It will:

1. Render `D:\Projects\MindAttic\MindAttic.Vault\README.md` through the catalog template.
2. FTPS-upload `out/mindatticvault.htm` to `/mindattic.com/mindatticvault.htm`.

After running, summarize the result and flag any failures.

Notes:
- Catalog entry: `MindAttic.Deploy/projects.json` -> `projects[]` slug `mindatticvault` (theme: Cyberspace).
- Credentials: the single source of truth is `%APPDATA%\MindAttic\Deploy\ftp.json` (canonical Vault bucket; folder == `MindAttic:Vault:Deploy`), or `MINDATTIC_FTP_JSON` env in CI. Transitional fallback: `MindAttic.Deploy/secrets/ftp.json` (gitignored). **No User Secrets** — the family retired it; APPDATA is the one local home for credentials.
- MindAttic.Vault is a library (no app deploy target) — this command only ships the landing page.
