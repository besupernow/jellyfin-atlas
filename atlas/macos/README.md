# Atlas Server for macOS

`Atlas Server.app` is a menu bar app that runs the bundled server (`Contents/Resources/server`)
with the bundled jellyfin-ffmpeg. The build workflow (`.github/workflows/atlas-build.yml`) makes the
DMG; `update-atlas.sh` installs a release over SSH.

## Updating

```sh
~/bin/update-atlas.sh          # latest published release
~/bin/update-atlas.sh 1.0.7    # a specific tag (drafts too)
```

## Keeping drive access across updates (signing)

macOS remembers permissions like Removable Volumes and Full Disk Access per app *signature*. There's
no Apple Developer ID here, so builds without a signing secret are ad-hoc signed. Every ad-hoc
build looks like a brand new app, so macOS asks again after each update, and the server hangs on
the prompt until someone clicks it at the Mac.

The fix is one self-signed code signing certificate that every build is signed with. It's free and
stays on GitHub as a secret. Set it up once, from the Mac (over SSH is fine):

```sh
mkdir -p ~/atlas-signing && cd ~/atlas-signing
nano atlas-signing.cnf
```

Paste:

```ini
[req]
distinguished_name = dn
prompt = no
x509_extensions = ext

[dn]
CN = Atlas Server Signing

[ext]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
```

Then (pick your own password):

```sh
/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -config atlas-signing.cnf \
  -keyout atlas-signing.key -out atlas-signing.crt
/usr/bin/openssl pkcs12 -export -name "Atlas Server Signing" \
  -inkey atlas-signing.key -in atlas-signing.crt -out atlas-signing.p12 -passout pass:YOUR_PASSWORD
base64 -i atlas-signing.p12 > atlas-signing.p12.txt
cat atlas-signing.p12.txt
```

Add two repository secrets (GitHub, Settings > Secrets and variables > Actions):

- `ATLAS_SIGNING_P12`: the whole `cat` output
- `ATLAS_SIGNING_PASSWORD`: the password

Keep `atlas-signing.p12` somewhere safe and delete the rest. A new certificate means macOS asks once
more.

The next build's Sign step logs `Signing with the Atlas identity ...`. After installing it, macOS
asks **one last time**, because the signature changed from ad-hoc to the certificate. Approve it
at the Mac, or grant Atlas Server **Full Disk Access** (System Settings > Privacy & Security), which
covers external drives too. From then on, updates keep the permission.

There's no way for a script to approve it ahead of time: macOS protects that permission database,
and only a device management profile can pre-grant it.
