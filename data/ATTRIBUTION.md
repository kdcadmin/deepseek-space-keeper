# Bundled Winapp2 data attribution

## 4.0 execution clarification

The read-only interpreter claims and boundaries below describe the original C# community/reference scanner only. Version 4.0 adds a separate, explicitly confirmed compatible cleanup window using a **modified BleachBit 5.0.2** engine. That window executes supported CleanerML/Winapp2 file, application-data and registry actions. It must not be described as read-only. The 251109 execution dataset omits the complete `Windows Shell - Folder/Dialog Box View Settings *` section because the engine cannot honor its registry exclusions; its attribution/license headers and the rest of the dataset remain. This adapted dataset remains CC-BY-SA-4.0 with the same Winapp2 attribution. The unmodified original reference resource remains separate.

BleachBit is GPL-3.0-or-later; bundled `vendor/bleachbit-5.0.2-cdrive/COPYING`, `CDriveCleaner-changes.txt`, `modified-source/`, `library.zip` source entries, and `source/bleachbit-v5.0.2-source.zip` document the exact base and changes. See `source/README-BUILD.md` for authenticated upstream URLs and reproduction instructions. Runtime and rules do not auto-update.

Winapp2.ini is provided by the Winapp2 project and its contributors: https://github.com/MoscaDotTo/Winapp2

Downloaded 2026-10-17 from https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/Winapp2.ini

Header version: 260915. Sections: 4068. FileKey definitions: 14121. RegKey definitions: 3677.

SHA256: 54CE86839B5C5E6ED796CC17B0CE879A795920BA5FCB930DC84BBA7ECE535C5B

The original unmodified data is distributed under Creative Commons Attribution-ShareAlike 4.0 International (CC-BY-SA-4.0); the full license is included as Winapp2-License.md. The embedded data and its original attribution headers remain intact. This program independently interprets a read-only subset of FileKey definitions and their supported application detection/exclusion conditions. It never executes RegKey definitions, commands, or community cleanup actions. File matches do not establish data disposability or safe cleanup authorization.

## Original application reference dataset

`Winapp2-251109.ini` is the complete 3,715-section Winapp2 dataset extracted from the user-supplied 磨针 3.2.8 executable. Its version header, license notices and attribution are unchanged. SHA256: `359543D47AC69C5300686ED55A36736C82BE17E6EE53F10CE82F2AD52FB558F8`.

The same Winapp2 contributors and CC-BY-SA-4.0 attribution above apply to this original data. `CommunityRules.LoadReference()` loads this separate embedded resource; `LoadBundled()` continues to select the existing 4,068-section catalog by default.

## Interpreter semantics and limits

The interpreter is independently written. Declarative syntax is documented by the [Winapp2 project's contribution reference](https://github.com/MoscaDotTo/Winapp2/blob/master/CONTRIBUTING.md#key-syntax-reference). Original application module string constants were inspected only to identify supported directive names and the OR detection model; no implementation code was copied.

- `Detect` and `DetectFile` alternatives use OR. If an alternative proves applicability, unresolved alternatives are still flagged. Otherwise unresolved detection skips the entry. `DetectOS` ranges are an additional mandatory inclusive gate (`minimum|maximum`, either bound may be empty). Bounds compare their specified kernel-version components; a `10.0` maximum includes later 10.0 builds. OS version comes from the read-only native version query. Unknown directives or malformed lines skip the entry.
- `Detect` reads key existence in HKCU/HKLM only, using both registry views on 64-bit Windows. Inaccessible keys are unresolved. HKU/HKCR/HKCC, registry wildcards and registry value predicates are unsupported. `RegKey` definitions remain count-only; `REG` exclusions have no file action to affect. No registry writes occur.
- `FILE` exclusions preserve a literal filename in its direct directory; `PATH` exclusions preserve matching filenames recursively. Directory-component wildcards are anchored. Unsupported file exclusions skip the entire entry, including otherwise supported FileKeys. `SpecialDetect` is preserved but unresolved. Application-version predicates and environment conditions outside the supported path variables are preserved as unknown directives and skip the entry.
- File inspection stays within the selected user profile. Supported variables are `%UserProfile%`, `%AppData%`, `%LocalAppData%`, `%LocalLowAppData%`, `%Documents%`, `%Music%`, `%Pictures%`, `%Video%`, and `%TEMP%`. Profile folders use their conventional locations beneath the selected profile; redirected known folders are not resolved. TEMP uses the process's current temporary-directory path only when it is inside that profile. System/program/shared-user variables, drive literals, bare variable roots, broad FileKey wildcard expansion immediately under a variable, traversal, links, offline/cloud placeholders and protected roots remain unsupported or skipped.
- `RECURSE` and `REMOVESELF` both enumerate ordinary matching files recursively for inspection. Neither removes files or directories. Semicolon masks and Windows `*.*` matching are supported; unsupported flags skip that FileKey. All results lack cleanup, recycle and organization authority. Existing work budgets, cancellation, pinned ancestor traversal and file-identity deduplication remain in force.

These are partial parity semantics, not a claim of complete compatibility with the original program, CCleaner, or all Winapp2 consumers.

## Separate read-only reference inventory

`CommunityScanner.CreateReferenceInventory()` uses the unchanged 251109 resource and additionally reads rule-matched metadata within the current profile, Windows, ProgramData, Program Files, `C:\PerfLogs` and `C:\Config.msi`. It does not traverse other user profiles. This distinct mode permits the original bare `%Temp%` file root and first-component wildcards, uses the user's configured TEMP path (read-only environment registry lookup; conventional profile TEMP fallback) rather than an executor's redirected process TMP, and adds WinDir/ProgramData/ProgramFiles/SystemDrive variable resolution within the fixed scope. Default community behavior above is unchanged.

Reference inventory results carry no internal cleanup snapshot and never authorize deletion, recycling, registry mutation, or organization. System directories can remain unknown when normal privileges cannot read them. The optional administrator inventory window exposes only scanning, cancellation and report export; its system consent prompt must be handled manually by the user. Group totals may overlap; the global logical-byte total deduplicates volume/file identity. Neither amount proves data disposability or expected disk-space release.
