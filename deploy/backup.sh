#!/usr/bin/env bash
# A backup of the stack's database: pg_dump in the compact format into BACKUP_DIR (outside the git checkout), checked, the oldest
# ones removed so that BACKUP_KEEP stay, and, when set, a copy sent off this server. Run it from cron (see deploy/README.md) or by
# hand before a deploy that migrates the database.
#
# Off-server copy (optional, either or both):
#   BACKUP_RSYNC_TARGET=user@host:/path/        rsync over ssh (the key of the user that runs this must be allowed there)
#   BACKUP_RCLONE_REMOTE=remote:bucket/folder   rclone (configure the remote first with `rclone config`)
# Copies off the server are not pruned here: remove old ones there as suits you.
set -u -o pipefail

. "$(dirname "${BASH_SOURCE[0]}")/ops-common.sh"

BACKUP_DIR="$(setting BACKUP_DIR /opt/tradepulse-backups)"
KEEP="$(setting BACKUP_KEEP 4)"
MIN_BYTES="$(setting BACKUP_MIN_BYTES 100000)"
RSYNC_TARGET="$(setting BACKUP_RSYNC_TARGET)"
RCLONE_REMOTE="$(setting BACKUP_RCLONE_REMOTE)"

fail() {
  notify "BACKUP FAILED: $1"
  echo "backup failed: $1" >&2
  rm -f "${part:-}"
  exit 1
}

umask 077
mkdir -p "$BACKUP_DIR" || fail "cannot create $BACKUP_DIR."

name="tradepulse-$(date -u +%Y%m%d-%H%M%S).dump"
out="$BACKUP_DIR/$name"
part="$out.part"

"${COMPOSE[@]}" exec -T postgres pg_dump -U postgres -d tradepulse -Fc > "$part" || fail "pg_dump did not finish."

size="$(stat -c %s "$part")"
[ "$size" -ge "$MIN_BYTES" ] || fail "the dump is only $size bytes (expected at least $MIN_BYTES)."

# The archive must be readable: its table of contents is listed by pg_restore of the same Postgres.
"${COMPOSE[@]}" exec -T postgres pg_restore -l < "$part" 2>/dev/null | grep -q "Format: CUSTOM" \
  || fail "the dump cannot be read back."

mv "$part" "$out" || fail "cannot move the dump into place."

# keep the newest $KEEP
ls -1t "$BACKUP_DIR"/tradepulse-*.dump | tail -n +"$((KEEP + 1))" | while read -r old; do
  rm -f -- "$old"
done

copied="no copy off this server is configured"
if [ -n "$RSYNC_TARGET" ]; then
  rsync -a -e "ssh -o BatchMode=yes" "$out" "${RSYNC_TARGET%/}/" || fail "$name was made but the rsync to $RSYNC_TARGET failed."
  copied="copied with rsync"
fi
if [ -n "$RCLONE_REMOTE" ]; then
  rclone copyto "$out" "${RCLONE_REMOTE%/}/$name" || fail "$name was made but the rclone copy to $RCLONE_REMOTE failed."
  copied="copied with rclone"
fi

notify "Backup done: $name, $((size / 1048576)) MB; $copied."
echo "backup done: $out"
