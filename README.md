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

The default build creates a static export in `out/`.

```powershell
npm install
npm run build
```

The included GitHub Actions workflow publishes `out/` to GitHub Pages whenever `main` is pushed.

## Discord setup

1. Create a Discord application in the Developer Portal.
2. Set the application name to the default text you want Discord to show after "Playing". Discord controls this label from the app name; individual presets can still change Details, State, buttons, timer, and artwork.
3. Upload Rich Presence art assets using these keys:
   - `lofi-bedroom-morning`
   - `lofi-bedroom-afternoon`
   - `lofi-bedroom-evening`
   - `lofi-bedroom-night`
   - `ems-room-morning`
   - `ems-room-afternoon`
   - `ems-room-evening`
   - `ems-room-night`

   Interviews, CC Review, and Audit intentionally reuse the lo-fi bedroom artwork so only the Rich Presence text changes.
4. The companion is already configured with Discord application ID `1531990024122532003`.

## Windows companion

Build the companion on a Windows machine with the .NET SDK installed:

```powershell
dotnet publish companion/LoFiRoom.Companion.csproj -c Release -r win-x64 --self-contained false
```

Run the published `LoFiRoomCompanion.exe`. It listens on:

```text
http://127.0.0.1:47372
```

The PWA sends preset updates to `/presence`; Discord is only contacted by the companion.

## iPhone note

GitHub Pages is HTTPS, while the companion currently exposes a local HTTP bridge. For same-PC testing, use `http://127.0.0.1:47372`. For iPhone control, the next production step is adding a trusted HTTPS bridge on the PC or a tiny private relay that the companion polls. The web app still never talks to Discord directly.
