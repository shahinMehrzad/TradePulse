#!/usr/bin/env bash
# A backup of the stack's databases: pg_dump in the compact format into BACKUP_DIR (outside the git checkout), checked, the oldest
# ones removed so that BACKUP_KEEP stay of each, and, when set, a copy sent off this server. Run it from cron (see deploy/README.md)
# or by hand before a deploy that migrates the database.
#
# Two databases, two kinds of file: tradepulse-*.dump (the main database) and vaultdb-*.dump (the key vault's own database: the
# encrypted private keys, orders, limits). The vault's keys are encrypted with VAULT_MASTER_KEY, which is in neither file.
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
VAULT_MIN_BYTES="$(setting VAULT_BACKUP_MIN_BYTES 2000)"
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

# dump_database <compose service> <user> <database> <file prefix> <minimum size>; sets $out, $name, $size
dump_database() {
  local service="$1" user="$2" database="$3" prefix="$4" min="$5"

  name="$prefix-$(date -u +%Y%m%d-%H%M%S).dump"
  out="$BACKUP_DIR/$name"
  part="$out.part"

  "${COMPOSE[@]}" exec -T "$service" pg_dump -U "$user" -d "$database" -Fc > "$part" || fail "pg_dump of $database did not finish."

  size="$(stat -c %s "$part")"
  [ "$size" -ge "$min" ] || fail "the dump of $database is only $size bytes (expected at least $min)."

  # It must be a pg_dump archive (they begin with PGDMP) that pg_restore of the same Postgres can read from the first byte to the
  # last, which a file cut short cannot be. (Not `pg_restore -l | grep -q`: grep stops at the first match, pg_restore is killed while it
  # still writes the table of contents, and with pipefail that counts as a failure: it failed on every real database.)
  [ "$(head -c 5 "$part")" = "PGDMP" ] || fail "the file for $database is not a pg_dump archive."
  "${COMPOSE[@]}" exec -T "$service" pg_restore -f /dev/null < "$part" >/dev/null 2>&1 || fail "the dump of $database cannot be read back."

  mv "$part" "$out" || fail "cannot move the dump into place."

  # keep the newest $KEEP of this kind
  ls -1t "$BACKUP_DIR"/"$prefix"-*.dump | tail -n +"$((KEEP + 1))" | while read -r old; do
    rm -f -- "$old"
  done

  if [ -n "$RSYNC_TARGET" ]; then
    rsync -a -e "ssh -o BatchMode=yes" "$out" "${RSYNC_TARGET%/}/" || fail "$name was made but the rsync to $RSYNC_TARGET failed."
    copied="copied with rsync"
  fi
  if [ -n "$RCLONE_REMOTE" ]; then
    rclone copyto "$out" "${RCLONE_REMOTE%/}/$name" || fail "$name was made but the rclone copy to $RCLONE_REMOTE failed."
    copied="copied with rclone"
  fi
}

copied="no copy off this server is configured"
dump_database postgres postgres tradepulse tradepulse "$MIN_BYTES"
main_name="$name"; main_size="$size"; main_out="$out"

# The vault's database exists once the stack was upgraded to it. Before that there is nothing to dump (the keys are in the main one).
vault_note=""
if "${COMPOSE[@]}" ps --status running --services 2>/dev/null | grep -qx vault-db; then
  dump_database vault-db vault vault vaultdb "$VAULT_MIN_BYTES"
  vault_note=" and $name ($((size / 1024)) KB)"
fi

notify "Backup done: $main_name, $((main_size / 1048576)) MB$vault_note; $copied."
echo "backup done: $main_out"
