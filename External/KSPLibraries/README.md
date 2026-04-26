# External KSPLibraries

This folder is intentionally excluded from git.

It is populated locally from a Kerbal Space Program install because the stock KSP managed DLLs are required for the client build but should not be committed to this repository.

Use the bootstrap script from the repo root to populate it:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Scripts\BootstrapLocalBuild.ps1
```