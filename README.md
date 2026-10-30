# DeveSTSCharacterSharer

A small Blazor Server app for sharing Slay the Spire 2 character mods with friends.

- **Upload**: drag a `.zip`, `.7z` or `.rar`, or a folder, onto the page. It's uploaded right away
  (no confirm step), only the mod folder is kept, and it replaces the previous version of that
  character.
- **Download**: everyone grabs one `sts-character-sync.cmd`, drops it in their
  `Slay the Spire 2\mods` folder and double-clicks it. It downloads only the characters that changed
  since the last run.

## Running it

`docker-compose.yml`:

```yaml
services:
  sts-character-sharer:
    image: ghcr.io/devedse/devestscharactersharer:latest
    container_name: sts-character-sharer
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      # How your friends reach this server. It is baked into the sync script.
      - PUBLIC_URL=http://192.168.1.10:8080
      # - PUBLIC_URL=https://sts.example.com
    volumes:
      - ./data:/data
```

```sh
docker compose up -d
```

Then open `http://<host>:8080`. Images are built for amd64 and arm64 on every push: `latest` and
`1.0.<build>` from master, `beta_<branch>` and `1.0.<build>-beta` from other branches.

### Settings

| Variable        | Default | Meaning |
|-----------------|---------|---------|
| `PUBLIC_URL`    | (empty) | Address friends use to reach the server: a hostname or an IP, with or without port. Without `http://`/`https://`, IPs, `localhost` and dot-less names get `http://`, other hostnames `https://`. When empty, the address your browser used to open the page is used instead. |
| `MAX_UPLOAD_MB` | `2048`  | Largest archive accepted. |
| `DATA_DIR`      | `/data` | Where everything is stored inside the container. |

The container runs as root, so files in `./data` are owned by root. To have them owned by your own
user, add `user: "1000:1000"` (your uid:gid) to the service and create `./data` yourself first.

### Behind a reverse proxy

Blazor Server keeps a WebSocket open to the browser, so the proxy has to pass WebSockets through
(Traefik and Caddy do this by default; nginx needs the `Upgrade`/`Connection` headers). Uploads from
the web page travel over that connection; only `curl` uploads (below) are subject to the proxy's
request body limit (nginx `client_max_body_size`).

## What gets stored from an upload

The app looks for the folder that holds a mod: `X.json` next to `X.dll` and/or `X.pck`. For the
`Yugi-0.3.0.zip` example, which contains

```
BUILD.txt  NOTES.txt  PLAY_GUIDE.md  README.txt  Yugi.source.zip  ...
Yugi/Yugi.dll  Yugi/Yugi.json  Yugi/Yugi.pck
```

only `Yugi/` is kept and everything else is thrown away. Some details:

- The character's name comes from the mod files (`Yugi.json` gives `Yugi`), so an archive with a
  versioned folder like `Yugi-0.4.0/Yugi.dll` still replaces `Yugi`. Names match case-insensitively,
  like folders on Windows.
- If the mod files sit loose at the top of the archive, only the `Yugi.*` files are kept.
- An archive containing several mods (say a character plus `BaseLib`) adds or updates each of them.
- A zip inside a zip is opened when the outer archive has no mod of its own.
- Folders can be dropped too: one character's folder, or your whole `mods` folder to add or update
  every character in it. The browser packs a dropped folder into a tar (without copying the files
  into memory) and uploads that. The sync script's hidden `.character-sync` files and
  `desktop.ini`/`Thumbs.db`/`.DS_Store` are never stored, so a folder that is already in sync counts
  as unchanged.
- Re-uploading the same build is reported as "already up to date" and doesn't make anyone download
  it again. The version is compared by a hash of the files' contents, not by file dates.
- Version, author and description shown on the page come from the mod's own json.
- The portrait on the page comes from the mod's `.pck` (a Godot resource pack):
  `images/packed/portraits/<id>_portrait.png`, or else the HUD icon, character select or map
  marker image there. Packs either contain the original PNG or only Godot's compiled `.ctex`
  texture; both work (the image inside a `.ctex` is a PNG or WebP). Characters without one show
  their initial.

Supported formats: zip, 7z, rar (v4 and v5), tar, tar.gz/bz2/xz (via
[SharpCompress](https://github.com/adamhathcock/sharpcompress)). Password protected archives are
rejected.

Uploading from a terminal works too:

```sh
curl -T Yugi-0.3.0.zip http://192.168.1.10:8080/api/upload/Yugi-0.3.0.zip
```

## The sync script

`sts-character-sync.cmd` is the same for everyone. It only contains the server address, not a list
of characters. Each run it:

1. downloads the character list with content hashes (`/api/manifest.txt`),
2. compares each hash with the one it saved in `<Character>\.character-sync` (a hidden file) last
   time,
3. downloads and unpacks only the characters that are new or changed, replacing the old folder.

Before that it updates itself: when the server has a newer version of the script, it replaces itself
and restarts. (A running `.cmd` can't simply be overwritten, because cmd.exe keeps reading the file
from where it was; the copy and the restart run from one block cmd has already read in full.)

It uses the `curl.exe` and `tar.exe` that come with Windows 10 (1803+) and 11, so nothing needs to be
installed. Folders that aren't on the server are left alone, and so are characters that get removed
from the server. If the game is still running, the old folder is locked; the script then leaves it
untouched and reports the failure instead of leaving a half-deleted folder. Close the game and run
it again.

Options: `sts-character-sync.cmd force` re-downloads everything, `nopause` skips the 5 second wait at
the end (useful for scheduled tasks).

## Data folder

```
data/
  characters/<Name>/         the mod folder exactly as players get it
  packages/<Name>.zip        prebuilt download
  packages/<Name>.info.json  upload time and original archive name
  packages/<Name>.portrait.* portrait taken from the mod's .pck
  keys/                      ASP.NET data protection keys
  work/                      temporary upload space, cleaned on start
```

You can also copy a mod folder into `data/characters/` by hand and restart the container; it's
picked up and packaged on start.

## Security

There is no login. Anyone who can open the page can upload or remove characters, and a character
contains a `.dll` that runs inside your friends' game. Only expose it to people you trust, or put
authentication in front of the web page in your reverse proxy. Keep `/api/manifest.txt`,
`/api/package/*` and `/sts-character-sync.cmd` reachable without it, because the script can't log in.

## Development

```sh
dotnet run --project src/DeveSTSCharacterSharer   # stores data in src/DeveSTSCharacterSharer/data
dotnet test
docker build -t devestscharactersharer .
```
