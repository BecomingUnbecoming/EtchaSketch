# Running the sync server

The sync server keeps your devices and your household's devices in step. It only ever stores
changes that were locked on a device before they were sent, so it can't read your tasks, notes,
names or anything else. The keys stay on your devices.

You need it only if you want more than one device. Without it, EtchaSketch works fully on one device.

## On your Proxmox host

Any Linux VM or LXC container with Docker will do. A small Debian LXC is plenty: 1 CPU core,
512 MB of memory and 2 GB of disk.

1. In the VM or container, install git and Docker with Compose (on Debian: `apt install git curl`,
   then `curl -fsSL https://get.docker.com | sh`).
   For an LXC container, turn on **Nesting** in its Options so Docker can run inside it.
2. Get the code and start the server:

   ```sh
   git clone https://github.com/BecomingUnbecoming/EtchaSketch.git
   cd EtchaSketch/deploy/sync-server
   docker compose up -d --build
   ```

3. Check it is running: `curl http://localhost:5080/health` should print `ok`.

The server listens on port 5080. Its data lives in the `etchasketch-sync` Docker volume, and it
restarts by itself after a reboot.

To update it later, run `git pull` and then `docker compose up -d --build` in the same folder.

## Connecting your devices

1. On the device where you set up your household, open **Sync**, enter the server's address
   (for example `http://192.168.1.20:5080`) and choose **Turn on sync**.
2. Under **Add a device**, choose the person whose device it is. A one-time code appears.
3. On the new device, at the welcome screen, fill in **Joining a household**: the same address
   and the code. The code works once and expires after 15 minutes.

Each person's device gets the household's shared items and only that person's private ones.

## Who can use the server

Everything stored is locked on the devices, so the server can't read it. Reaching the server is a
different matter: it has no accounts, so anyone who can reach port 5080 can try to store things on
it. Keep it on your home network or VPN, and don't put it on the internet.

Because of that, the server limits what it will take, so a stranger on your network (or a bug)
can't fill its disk:

| Setting | Default | What it limits |
| --- | --- | --- |
| `Sync__MaxStreams` | 20 | Streams that can exist: one for the household and one per person |
| `Sync__MaxStreamBytes` | 64 MB | What one stream may store |
| `Sync__MaxChangeBytes` | 256 KB | One sealed change |
| `Sync__MaxChangesPerPush` | 500 | Changes in one upload (devices send 200) |
| `Sync__MaxRequestBytes` | 8 MB | The biggest request the server reads |
| `Sync__MaxPendingPairings` | 20 | New-device codes waiting to be used |
| `Sync__RequestsPerMinute` | 600 | Requests from one address |
| `Sync__PairingRequestsPerMinute` | 30 | New-device requests from one address |

Change one by adding it under `environment:` in `compose.yaml` and running
`docker compose up -d`. When a limit is hit the app says the server has no room or is getting too
many requests. The container also has a memory cap and rotating logs.

If you put a reverse proxy in front, every request comes from the proxy's address, so the
per-address limits apply to all your devices together. Raise `Sync__RequestsPerMinute` if sync
slows down.

## Away from home

You don't need to open any ports on your router. To sync while you're out, connect your phone to
your home network over a VPN such as Tailscale or WireGuard, and use the server's address on that
network. Changes made while you're offline sync the next time the app can reach the server.

Plain `http://` is fine on your home network or VPN, because everything is locked before it is
sent. If you already run a reverse proxy with HTTPS, you can put the server behind it and use the
`https://` address instead.

## Recovery key

The server's copy can only be opened with keys that live on your devices. To make sure you can
always get back in, open **Sync** and choose **Make recovery key**. Enter a passphrase of at
least 12 characters. You get a block of text starting with `ESKEY1-`: save it somewhere safe,
such as a note in your password manager, and keep the passphrase somewhere separate from it.

If every device is lost, install the app on a new one. On the welcome screen, enter the server's
address under **Joining a household**, then fill in **Lost every device?** with the key and the
passphrase. Your household comes back with everything the server held.

Make a new key whenever you add a person to the household, since a key only holds the keys that
existed when it was made.

## Backups

Back up the `etchasketch-sync` volume if you like, but remember the server's copy can only be
opened with keys that live on your devices. Keep at least one device that's set up, and keep a recovery key. Without both, if every
device is lost, the server's copy can't be read by anyone, including you.
