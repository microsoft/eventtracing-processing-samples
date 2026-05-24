# Releasing `MemoryUsageChecker`

This document is the **standard procedure** for producing a `MemoryUsageChecker`
binary drop — whether the goal is

- sharing the tool on a USB flash drive for ad-hoc testing on another device, or
- posting an official release for external users on a public web site.

It is intentionally kept out of the publish drop (no `<Content>` entry in
`MemoryUsageChecker.csproj`) — end users see `README.md`, releasers see this
file.

---

## 1. Pre-flight checks (before producing a release)

1. **Clean working tree.** `git status` should be empty (or only contain the
   intentional release-stamp commit).
2. **Latest pulled.** `git pull --ff-only` on the release branch.
3. **Toolchain.** Confirm the .NET 10 SDK is installed:
   ```
   dotnet --list-sdks
   ```
   Any 10.x SDK is sufficient.
4. **Optional smoke ETL on hand.** Keep one known-good `*.etl` capture you have
   analyzed before, so steps in §4 can compare against a baseline.

## 2. Build both architectures from scratch

Always clean before producing a release drop. A fresh publish guarantees no
stale outputs (or stripped stub folders from prior builds) sneak into the drop.

```
:: from repo root
rmdir /S /Q v2\MemoryUsageChecker\bin
rmdir /S /Q v2\MemoryUsageChecker\obj

dotnet publish v2/MemoryUsageChecker -p:PublishProfile=win-x64
dotnet publish v2/MemoryUsageChecker -p:PublishProfile=win-arm64
```

Each publish should end with one line of the form

```
MemoryUsageChecker -> ...\bin\Release\net10.0\<rid>\publish\
```

and **zero** errors / warnings.

## 3. Verify the publish folder contains exactly the expected 4 files

For each RID, the publish folder must contain **only** these four files —
nothing else, no empty folders, no leftover `.pdb` (PDBs are embedded into the
single-file `.exe`):

```
v2\MemoryUsageChecker\bin\Release\net10.0\win-x64\publish\
  MemoryUsageChecker.exe       <-- ~47 MB self-contained single-file
  MemoryUsageChecker.wprp      <-- WPR profile
  MemoryUsageTrace.cmd         <-- one-click trace-collection helper
  README.md                    <-- user guide (canonical copy)
```

Quick PowerShell check:

```powershell
foreach ($rid in 'win-x64','win-arm64') {
    $dir = "v2\MemoryUsageChecker\bin\Release\net10.0\$rid\publish"
    $files = Get-ChildItem $dir -Recurse -Force | Where-Object { -not $_.PSIsContainer }
    $dirs  = Get-ChildItem $dir -Recurse -Force | Where-Object { $_.PSIsContainer }
    "$rid : $($files.Count) files, $($dirs.Count) subfolders"
    $files | Select-Object Name, Length
}
```

Expected output:

```
win-x64 : 4 files, 0 subfolders
win-arm64 : 4 files, 0 subfolders
```

If the file count is not 4 or the subfolder count is not 0, **stop and
investigate** — most likely a transitive NuGet package started shipping new
content. The `RemoveEmptyPublishStubs` MSBuild target in
`MemoryUsageChecker.csproj` handles the known offenders (`Catalog`,
`CredentialProviders`); extend it if a new one appears.

## 4. Smoke test the freshly built `.exe` on a known ETL

Run the win-x64 build (or the win-arm64 build, on an ARM64 box) against a
known-good capture and confirm:

```
:: from a temp folder, with %TRACE% pointing at a known .etl
.\MemoryUsageChecker.exe %TRACE% --no-symbols
```

Expected:

- Exit code `0`.
- Three sibling files produced next to the working directory:
  `MemoryUsage_Result_<ts>.txt`, `MemoryUsage_Result_<ts>.json`,
  `MemoryUsage_Diag_<ts>.log`.
- Sizes roughly consistent with prior runs (txt ~100-120 KB, json ~150-200 KB,
  log ~3-5 KB for a ~6 GB capture).
- Visual: color-coded ranked tables in the console.

If anything looks off, do not ship the drop.

## 5. Stage the drop

Use this layout — it is what testers and external users expect, and it is what
the per-architecture publish folders already are:

```
MemoryUsageChecker\
  win-x64\
    MemoryUsageChecker.exe
    MemoryUsageChecker.wprp
    MemoryUsageTrace.cmd
    README.md
  win-arm64\
    MemoryUsageChecker.exe
    MemoryUsageChecker.wprp
    MemoryUsageTrace.cmd
    README.md
```

### 5a. USB flash drive (ad-hoc testing on another device)

```powershell
$dest = 'G:\MemoryUsageChecker'   # adjust drive letter
Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dest\win-x64,$dest\win-arm64 | Out-Null
Copy-Item v2\MemoryUsageChecker\bin\Release\net10.0\win-x64\publish\*   $dest\win-x64\
Copy-Item v2\MemoryUsageChecker\bin\Release\net10.0\win-arm64\publish\* $dest\win-arm64\
```

### 5b. Official release zip (for posting on a web site)

```powershell
$version = '1.0.0'                # bump per release
$staging = "$env:TEMP\MemoryUsageChecker-$version"
Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
New-Item -ItemType Directory $staging\win-x64,$staging\win-arm64 | Out-Null
Copy-Item v2\MemoryUsageChecker\bin\Release\net10.0\win-x64\publish\*   $staging\win-x64\
Copy-Item v2\MemoryUsageChecker\bin\Release\net10.0\win-arm64\publish\* $staging\win-arm64\

Compress-Archive -Path $staging\* -DestinationPath ".\MemoryUsageChecker-$version.zip" -Force
```

## 6. Generate and publish hashes

External users need a way to verify what they downloaded. Always publish
SHA-256 hashes of every shipped binary (and of the zip itself for §5b drops).

```powershell
$rels = Get-ChildItem v2\MemoryUsageChecker\bin\Release\net10.0\*\publish\MemoryUsageChecker.exe
$rels | Get-FileHash -Algorithm SHA256 | Format-List Path, Hash
```

For the zip:

```powershell
Get-FileHash .\MemoryUsageChecker-1.0.0.zip -Algorithm SHA256 | Format-List Hash
```

Paste the hash list into the GitHub release notes (or the download page) under
a "Verification" section.

## 7. Final sanity check on the actual drop (USB or zip)

Mount the USB (or extract the zip into a temp folder) and re-run the hash
check against what you computed in §6 — they must match bit-for-bit.

```powershell
Get-FileHash G:\MemoryUsageChecker\win-x64\MemoryUsageChecker.exe   -Algorithm SHA256
Get-FileHash G:\MemoryUsageChecker\win-arm64\MemoryUsageChecker.exe -Algorithm SHA256
```

Also confirm both arch folders contain exactly 4 files each and no stray
subfolders:

```powershell
foreach ($arch in 'win-x64','win-arm64') {
    $dir = "G:\MemoryUsageChecker\$arch"
    $files = (Get-ChildItem $dir -Force | Where-Object { -not $_.PSIsContainer }).Count
    $dirs  = (Get-ChildItem $dir -Force | Where-Object {     $_.PSIsContainer }).Count
    "$arch : $files files, $dirs subfolders"
}
```

If either count is wrong, redo §5.

## 8. Caveats for an *official* (web-site) release

Items above are sufficient for an internal / IHV / tester drop. For a public
web-site release also consider:

- **Authenticode signing.** The default `dotnet publish` output is unsigned —
  `Get-AuthenticodeSignature` will report `NotSigned`. SmartScreen will warn
  end users until the binary is signed. Sign with a corporate code-signing
  certificate after §3 and before §5b. Re-run §6 on the signed binary so the
  published hash matches what the user downloads.
- **Versioning.** Stamp `Version` / `FileVersion` / `InformationalVersion` in
  `MemoryUsageChecker.csproj` (or pass `/p:Version=1.0.0` to `dotnet publish`)
  so `Get-ItemProperty -LiteralPath ...exe | Select VersionInfo` shows the
  release version. Keep the version visible in the file name of the zip.
- **License + notices.** Include a top-level `LICENSE` / `THIRD-PARTY-NOTICES`
  file in the zip if your distribution channel requires them.
- **SBOM.** If the channel requires a Software Bill of Materials, generate one
  alongside the zip (e.g. with `dotnet sbom-tool`) and publish it next to the
  hash list.
- **Release notes.** Summarize behavior changes since the previous release —
  especially anything that affects the JSON sidecar schema (`schemaVersion`
  bumps are breaking for downstream A/B-comparison scripts).

## 9. Quick reference - one-shot release script

For a routine drop where signing is not required, the entire flow above is:

```powershell
# 1. clean
Remove-Item -Recurse -Force v2\MemoryUsageChecker\bin, v2\MemoryUsageChecker\obj -ErrorAction SilentlyContinue

# 2. build both architectures
dotnet publish v2/MemoryUsageChecker -p:PublishProfile=win-x64
dotnet publish v2/MemoryUsageChecker -p:PublishProfile=win-arm64

# 3. verify file counts
foreach ($rid in 'win-x64','win-arm64') {
    $dir = "v2\MemoryUsageChecker\bin\Release\net10.0\$rid\publish"
    $n = (Get-ChildItem $dir -Force).Count
    if ($n -ne 4) { throw "$rid publish has $n entries, expected 4" }
}

# 4. (manual) smoke test against a known ETL

# 5. stage the drop
$dest = 'G:\MemoryUsageChecker'   # or a staging path for a zip
Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dest\win-x64,$dest\win-arm64 | Out-Null
Copy-Item v2\MemoryUsageChecker\bin\Release\net10.0\win-x64\publish\*   $dest\win-x64\
Copy-Item v2\MemoryUsageChecker\bin\Release\net10.0\win-arm64\publish\* $dest\win-arm64\

# 6. hash list
Get-ChildItem $dest -Recurse -Filter MemoryUsageChecker.exe |
    Get-FileHash -Algorithm SHA256 | Format-List Path, Hash
```
