# Licence public key

`licence-public-key.pem` in this folder is built into the program. The bridge accepts only licence keys signed
with the matching **private key**, which never goes into this repository.

Create the pair once with `tools/LicenceTool` (see `docs/licensing.md`):

```bash
dotnet run --project tools/LicenceTool -- keygen
```

That writes this file and the private key outside the repository. Without this file, every build is locked in
production ("no licence public key").
