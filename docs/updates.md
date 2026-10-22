# Updates

Every change merged into `main` that passes all checks becomes a numbered release on GitHub
(`v1.0.<build>`), with the Android APK and the Linux desktop build. Phones and the desktop pick it up on
their own.

## Phones (Android, GrapheneOS)

Updates come through **Obtainium**, an open-source app that installs apps straight from GitHub releases.
It needs no Google services. Get it from F-Droid, or from its GitHub releases.

1. In Obtainium, choose **Add app** and paste `https://github.com/BecomingUnbecoming/EtchaSketch`.
2. Choose **Install**. From then on Obtainium checks for new releases and offers each update.
3. Optional: in Obtainium's settings, turn on background update checks.

### Moving from an older build (once)

Builds made before automatic updates were each signed with a different throwaway key, and Android won't
install an app over one with a different key. So the first time only:

1. On a device that's already set up (the desktop), open **Sync → Add a device** and make a code for
   the person whose phone it is.
2. On the phone, uninstall EtchaSketch, then install it through Obtainium.
3. On the welcome screen, use **Joining a household** with the server's address and the code.
   Your tasks and household come back from the sync server. Set your PIN again, since PINs never leave a
   device.

Every release is now signed with the same permanent key, so this never needs doing again. The key's
certificate fingerprint (SHA-256) is
`71:F8:CC:94:D6:28:92:A6:9F:4F:CF:A2:B4:0D:48:1C:E9:5A:09:D0:11:30:98:1D:94:39:B2:55:7F:00:D1:B1`.
CI refuses to publish a build signed with anything else.

## Linux desktop

`tools/linux-updater` checks for a new release every hour and when you log in. If the app is closed, it
downloads the Linux build, checks it against the release's `SHA256SUMS`, and copies it over the install
folder. Your database lives in that folder and is never touched. If the app is open, it waits until the
next check.

Install it once, for your user (no root needed), from a copy of this repository:

```sh
tools/linux-updater/install.sh
```

It expects the app in `~/.local/share/EtchaSketch` (change with `ETCHASKETCH_DIR`). Run
`etchasketch-update` to check right away; `journalctl --user -u etchasketch-update` shows what it did.

## For maintainers

- The release key is a PKCS12 keystore kept in three repository secrets: `ANDROID_KEYSTORE_BASE64`,
  `ANDROID_KEYSTORE_PASSWORD` and `ANDROID_KEY_ALIAS`, with an offline copy. **If the key is lost, installed
  phones can't update again** without the uninstall above, so keep that copy safe.
- Forks don't get the secrets, so their APKs use a throwaway key and are never released.
- The version code is the CI run number, so it grows with every build.
- Only the ten newest releases are kept.
