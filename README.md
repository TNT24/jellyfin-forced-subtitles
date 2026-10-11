# Forced Subtitles — Jellyfin plugin

<img src="assets/logo.png" width="400" alt="Forced Subtitles logo">

The goal: you can watch anything without missing subtitles.

| Video | What the plugin does |
|---|---|
| Audio in your language (e.g. English) and **no forced** English subtitle | Downloads a **forced** English subtitle (foreign dialogue only) **if OpenSubtitles has one**. If none exists, nothing is downloaded. It never falls back to a full subtitle. |
| Audio in **another language** and **no** English subtitle | Downloads a **full** English subtitle (can be turned off). |
| Already has the right subtitle (embedded or external) | Nothing. No API calls. |

Works on **Jellyfin 12.x** and **Jellyfin 10.11.x**. Each has its own build; use the one matching your server.

## Settings page
Open it from the Jellyfin sidebar (**Forced Subtitles**) or Dashboard → Plugins. It has:
* **Account status**: shows where the API key and login come from, plus a **Test connection** button that shows how many downloads you have left today.
* **Username and password** only. Leave them blank to reuse the login from the official OpenSubtitles plugin.
* Subtitle language, movies/TV, and full subtitles for foreign audio.
* **Libraries** to check.
* **Where to save**:
  * Jellyfin's metadata folder (default; media folders stay clean)
  * next to the video
  * each library's own setting
* Check after library scans, matching options, and the download cap.
* **Results**, with **Run now**, **Refresh** and **Forget history** buttons.

## API key
OpenSubtitles needs an app API key on every request. The official plugin ships Jellyfin's key built in. This plugin can do the same with **your** key:

1. On opensubtitles.com go to Profile → **API consumers** → create one (free) and copy the key.
2. Save it in `opensubtitles-api-key.txt` in this folder. The file is git-ignored.
3. Run `sh package.sh`. The key is compiled into the DLL, so the settings page only needs a username and password.

Without a built-in key, you enter the key under *Advanced* on the settings page instead.

## Where subtitles go
Jellyfin only reads external subtitles from two places: right next to the video, or its internal metadata folder. Subfolders such as `Movie/subtitles/` are **not** scanned. That's why the default is Jellyfin's metadata folder: media folders stay untouched and the subtitles still show up.

Files are named `<video>.eng.forced.srt` (or `.eng.srt` for full subtitles).

> Subtitles in the metadata folder belong to Jellyfin's database entry for the item. If you delete the item from Jellyfin and re-add it, the plugin simply downloads them again. Choose "next to the video" if other players (VLC, Plex) need them.

## When it runs
* **Scheduled task:** *Download missing forced subtitles* (Dashboard → Scheduled Tasks → Subtitles), daily at 3am by default.
* **After every library scan:** only videos it hasn't checked before. It runs in the background so the scan isn't held up.
* **Run now** button on the settings page.
* **Manual search:** an item's *Edit subtitles → Search* dialog gets an **"OpenSubtitles (Forced aware)"** source. Forced results are marked `[FORCED]`.

## How it decides
* **Checks locally first.** It reads the video's existing subtitle and audio tracks. A subtitle counts as forced if its forced flag is set, or if its title or filename contains "forced" or "foreign".
* **Default audio track** language decides between forced and full. Unknown (`und`) audio is treated as your language.
* **Ranking:** exact file-hash match, then release-name similarity to your filename, then trusted uploader, non-SDH and download count. Machine translations are skipped by default.
* **Remembers results.** "None available" videos are only searched again after *Re-search after (days)*. Nothing is downloaded twice.

## Install from repository (easiest)
1. In Jellyfin go to Dashboard → Plugins → **Repositories** and add:
   `https://raw.githubusercontent.com/TNT24/jellyfin-forced-subtitles/main/manifest.json`
2. Open the **Catalog**, find **Forced Subtitles** under Subtitles and install it.
3. Restart Jellyfin.

Published builds have no built-in API key, so enter yours under *Advanced* on the settings page.

## Install manually
1. `sh package.sh` (needs the .NET 10 SDK). It builds both versions:
   * `dist/jellyfin-12/ForcedSubtitles_1.1.0.0/` for Jellyfin 12.x
   * `dist/jellyfin-10.11/ForcedSubtitles_1.1.0.0/` for Jellyfin 10.11.x

   Check your version under Dashboard → General.
2. Copy the matching `ForcedSubtitles_1.1.0.0` folder into the server's plugin directory:
   * Windows: `%ProgramData%\Jellyfin\Server\plugins\`
   * Linux: `/var/lib/jellyfin/plugins/`
   * Docker: `/config/plugins/`

   Remove any older `ForcedSubtitles_*` folder first.
3. Restart Jellyfin, then open **Forced Subtitles** in the sidebar.

## Limits
* Free OpenSubtitles accounts get about **20 downloads per day**, shared with the official OpenSubtitles plugin if you use the same account. Searches don't count. When the quota runs out, the run stops and continues next time.
* Many films have no forced subtitle uploaded, or have the foreign lines burned into the picture, so "None available" is a normal result.

## Development
* Tests: `dotnet test tests/Jellyfin.Plugin.ForcedSubtitles.Tests`
* Build for one server line: `dotnet build Jellyfin.Plugin.ForcedSubtitles -p:JellyfinTarget=10.11` (default `12`).
