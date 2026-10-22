#!/bin/sh
# Installs the EtchaSketch desktop updater for the current user (no root needed).
set -eu
here=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$HOME/.local/bin" "$HOME/.config/systemd/user"
install -m 755 "$here/etchasketch-update" "$HOME/.local/bin/etchasketch-update"
install -m 644 "$here/etchasketch-update.service" "$here/etchasketch-update.timer" "$HOME/.config/systemd/user/"
systemctl --user daemon-reload
systemctl --user enable --now etchasketch-update.timer
echo "Updater installed. Run 'etchasketch-update' to check now; 'journalctl --user -u etchasketch-update' shows what it did."
