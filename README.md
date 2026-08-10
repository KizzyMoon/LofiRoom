# Lo-fi Room Discord Rich Presence

A mobile-first PWA control panel plus a lightweight Windows tray companion for Discord Rich Presence.

## What is included

- Installable dark-mode PWA with offline support.
- Home-first preset controls with one-tap activation.
- Long-press preset editing.
- Add, delete, reorder, enable, and disable presets.
- Artwork manager with time-of-day variants.
- Settings and profile views.
- Shortcut-friendly links such as `/?preset=training`.
- Windows tray companion source with Discord IPC, auto reconnect, elapsed timer, Auto Away, and startup registration.

## GitHub Pages

The current site is a static GitHub Pages app served directly from the repository root.

## Discord setup

1. Create a Discord application in the Developer Portal.
2. Set the application name to the default text you want Discord to show after "Playing". Discord controls this label from the app name; individual presets can still change Details, State, buttons, timer, and artwork.
3. Upload Rich Presence art assets using these exact keys:
   - `chilling`
   - `busy`
   - `away`
   - `ems`
   - `training`
   - `gaming`

   The current presets intentionally use those simple asset keys. Do not add `2` suffixes unless matching Discord assets with those exact names have also been uploaded.
4. The companion is already configured with Discord application ID `1531990024122532003`.

## Windows companion

The companion is the local bridge between the GitHub Pages controller and the Discord desktop client. It listens on:

```text
http://127.0.0.1:47372
```

The PWA sends preset updates to `/presence`; Discord is contacted by the companion through local IPC. The companion can also poll `presets.json` for shared preset changes.

To build it on Windows with the .NET SDK installed:

```powershell
dotnet publish companion/LoFiRoom.Companion.csproj -c Release -r win-x64 --self-contained false
```

## Artwork mapping

The web dashboard artwork and Discord Rich Presence use matching names:

- Chilling → `assets/chilling.png` / Discord key `chilling`
- Busy → `assets/busy.png` / Discord key `busy`
- Away → `assets/away.png` / Discord key `away`
- On Duty → `assets/ems.jpg` / Discord key `ems`
- Training / Interviews → `assets/training.jpg` / Discord key `training`
- Gaming → `assets/gaming.jpg` / Discord key `gaming`

## iPhone note

GitHub Pages is HTTPS, while the companion exposes a local HTTP bridge. The shared `presets.json` sync is used so another device can change the selected preset and the companion can pick it up on the PC.
