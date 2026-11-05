# Switch Unpacker

A small Windows GUI that unpacks Nintendo Switch `.nsp` and `.xci` files into `exefs` and `romfs` (plus control, legal, and data) in one click. It wraps [hactool](https://github.com/SciresM/hactool) 1.4.0 (bundled into the exe) so you don't have to run commands by hand.

## Features

- Supports `.nsp` and `.xci`
- Pick the game file, output folder, and `prod.keys` in a simple window (or drag the game onto it)
- Detects each NCA's type automatically, so no manual filename hunting
- Reads the titlekey from the ticket for you (NSP)
- Output subfolder named by game file name, game title (read from the game data), or a custom name
- Choose what to extract: `exefs` + `romfs` always, optional Control, Legal, Data
- Single exe with hactool built in; remembers your last settings

## Requirements

- Windows 10 or 11 (uses the .NET Framework that ships with Windows)
- A `prod.keys` dumped from **your own** Switch with [Lockpick_RCM](https://github.com/shchmue/Lockpick_RCM)
- Games you own and dumped yourself
- Free disk space of roughly the game's size, as a temporary unpack folder (deleted automatically)

No keys or games are included or distributed with this tool.

## Usage

1. Run `SwitchUnpacker.exe`.
2. Select the `.nsp` / `.xci` file.
3. Select the output folder.
4. Select your `prod.keys` (auto-filled if it's next to the exe or in `%USERPROFILE%\.switch\`).
5. Choose the subfolder naming option and what to extract.
6. Click **Unpack**.

Output:

```
<output folder>\<name>\
  exefs\     main, main.npdm, rtld, sdk
  romfs\     game assets
  control\   control.nacp, icons
  legal\     legal info
  data\      extra data (if present)
```

If the subfolder already exists, `(2)`, `(3)`, ... is added instead of overwriting.

## What works

| Input | Works |
|---|---|
| Base game `.nsp` | Yes |
| Base game `.xci` | Yes |
| Games for original Switch hardware, also playable on Switch 2 | Yes, if your `prod.keys` is new enough for the game's firmware |

## What doesn't work

| Input | Why |
|---|---|
| Switch 2 games | Different platform and keys. Not supported by hactool, and Lockpick_RCM can't dump keys from a Switch 2 |
| Game-Key Cards | They hold a license only, no game data |
| Update / DLC NSPs | They extract as patch data only. hactool 1.4.0 can't merge them with the base game |
| `.nsz` / `.xcz` | Compressed. Decompress first: `pip install nsz` then `nsz -D file.nsz` |
| NSPs with several tickets | Only the first ticket's titlekey is used |
| Anything needing keys newer than your `prod.keys` | Re-dump keys on current firmware |

## Troubleshooting

- **"header_key not found"**: your `prod.keys` is incomplete or the wrong file.
- **"No NCA files found" / "Nothing was extracted"**: keys are too old for the game, or the file is corrupt, compressed, or not supported (see above).
- **Windows SmartScreen warning**: the exe is unsigned. Click *More info*, then *Run anyway*. Antivirus may flag the embedded hactool as a false positive. You can build it yourself from source (below) to avoid this.

## Build from source

You only need `SwitchUnpacker.cs` and a copy of `hactool.exe` (1.4.0) in the same folder. Open PowerShell there and run:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:SwitchUnpacker.exe /resource:hactool.exe,hactool.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll SwitchUnpacker.cs
```

No Visual Studio or SDK required.

## Files created on your PC

- `%LOCALAPPDATA%\SwitchUnpacker\hactool.exe`: the bundled hactool, extracted on first run
- `%APPDATA%\SwitchUnpacker\settings.ini`: your last-used options
- A temporary unpack folder inside the output folder during a run (removed afterwards)

## Legal

For personal backups and homebrew development using your own console, keys, and games. Do not share keys or game files. This project is not affiliated with Nintendo.

## Credits

- [hactool](https://github.com/SciresM/hactool) by SciresM (ISC license)
- [Lockpick_RCM](https://github.com/shchmue/Lockpick_RCM) by shchmue
