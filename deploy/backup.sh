#!/usr/bin/env bash
# A backup of the stack's databases: pg_dump in the compact format into BACKUP_DIR (outside the git checkout), checked, the oldest
# ones removed so that BACKUP_KEEP stay of each, and, when set, a copy sent off this server. Run it from cron (see deploy/README.md)
# or by hand before a deploy that migrates the database.
#
# Files (all in BACKUP_DIR):
#   tradepulse-*.dump   the main database
#   vaultdb-*.dump      the key vault's own database (the keys in it are encrypted with VAULT_MASTER_KEY, which is in no backup)
#   api-keys-*.tgz.age  the keys of the API's data protection (they decrypt the authenticator keys in the main database); only with encryption
#
# Encryption (recommended): set BACKUP_AGE_RECIPIENT to an age public key (age1...). Every file is then encrypted before it is kept or
# copied anywhere, with the extra suffix .age, and only the owner of the matching private key (which must NOT be on this server) can read
# it. Needs the program `age` on the server (sudo apt install age). See docs/database-security.md for the key, and for restoring.
# While it works, the plain dump lives only in memory (/dev/shm, or BACKUP_TMP_DIR) and is deleted at once.
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
VAULT_MIN_BYTES="$(setting VAULT_BACKUP_MIN_BYTES 500)"
RSYNC_TARGET="$(setting BACKUP_RSYNC_TARGET)"
RCLONE_REMOTE="$(setting BACKUP_RCLONE_REMOTE)"
AGE_RECIPIENT="$(setting BACKUP_AGE_RECIPIENT)"
TMP_DIR="$(setting BACKUP_TMP_DIR)"

fail() {
  notify "BACKUP FAILED: $1"
  echo "backup failed: $1" >&2
  rm -f "${part:-}" "${enc:-}"
  exit 1
}

umask 077
mkdir -p "$BACKUP_DIR" || fail "cannot create $BACKUP_DIR."

SUFFIX=""
WORK="$BACKUP_DIR"
if [ -n "$AGE_RECIPIENT" ]; then
  command -v age >/dev/null 2>&1 || fail "BACKUP_AGE_RECIPIENT is set but the program age is not installed (sudo apt install age)."
  [[ "$AGE_RECIPIENT" =~ ^age1[0-9a-z]{58}$ ]] || fail "BACKUP_AGE_RECIPIENT is not an age public key (it starts with age1 and is 62 characters)."
  SUFFIX=".age"
  # The plain dump is only needed to check it and encrypt it: keep it in memory, not on the disk, when there is a place for it.
  WORK="${TMP_DIR:-/dev/shm}"
  if [ ! -d "$WORK" ] || [ ! -w "$WORK" ]; then WORK="$BACKUP_DIR"; fi
fi

copied="no copy off this server is configured"

# ship <file>: sends one finished file off the server, when a target is set
ship() {
  local file="$1" base
  base="$(basename "$file")"
  if [ -n "$RSYNC_TARGET" ]; then
    rsync -a -e "ssh -o BatchMode=yes" "$file" "${RSYNC_TARGET%/}/" || fail "$base was made but the rsync to $RSYNC_TARGET failed."
    copied="copied with rsync"
  fi
  if [ -n "$RCLONE_REMOTE" ]; then
    rclone copyto "$file" "${RCLONE_REMOTE%/}/$base" || fail "$base was made but the rclone copy to $RCLONE_REMOTE failed."
    copied="copied with rclone"
  fi
}

# prune <file prefix>: keeps the newest $KEEP of this kind (encrypted or not)
prune() {
  ls -1t "$BACKUP_DIR"/"$1"-*.dump "$BACKUP_DIR"/"$1"-*.dump.age "$BACKUP_DIR"/"$1"-*.tgz.age 2>/dev/null | tail -n +"$((KEEP + 1))" | while read -r old; do
    rm -f -- "$old"
  done
}

# dump_database <compose service> <user> <database> <file prefix> <minimum size>; sets $out, $name, $size
dump_database() {
  local service="$1" user="$2" database="$3" prefix="$4" min="$5"

  name="$prefix-$(date -u +%Y%m%d-%H%M%S).dump$SUFFIX"
  out="$BACKUP_DIR/$name"
  part="$WORK/$prefix-$$.dump.part"
  enc="$out.part"

  "${COMPOSE[@]}" exec -T "$service" pg_dump -U "$user" -d "$database" -Fc > "$part" || fail "pg_dump of $database did not finish."

  size="$(stat -c %s "$part")"
  [ "$size" -ge "$min" ] || fail "the dump of $database is only $size bytes (expected at least $min)."

  # It must be a pg_dump archive (they begin with PGDMP) that pg_restore of the same Postgres can read from the first byte to the
  # last, which a file cut short cannot be. (Not `pg_restore -l | grep -q`: grep stops at the first match, pg_restore is killed while it
  # still writes the table of contents, and with pipefail that counts as a failure: it failed on every real database.)
  # This is done on the plain dump: nothing on this server can read it again once it is encrypted.
  [ "$(head -c 5 "$part")" = "PGDMP" ] || fail "the file for $database is not a pg_dump archive."
  "${COMPOSE[@]}" exec -T "$service" pg_restore -f /dev/null < "$part" >/dev/null 2>&1 || fail "the dump of $database cannot be read back."

  if [ -n "$AGE_RECIPIENT" ]; then
    age -r "$AGE_RECIPIENT" -o "$enc" "$part" || fail "encrypting the dump of $database failed."
    rm -f "$part"
    [ -s "$enc" ] || fail "the encrypted dump of $database is empty."
    mv "$enc" "$out" || fail "cannot move the dump into place."
  else
    mv "$part" "$out" || fail "cannot move the dump into place."
  fi
  part=""; enc=""

  prune "$prefix"
  ship "$out"
}

dump_database postgres postgres tradepulse tradepulse "$MIN_BYTES"
main_name="$name"; main_size="$size"; main_out="$out"

# The vault's database exists once the stack was upgraded to it. Before that there is nothing to dump (the keys are in the main one).
note=""
# (Not `ps ... | grep -q`: grep stops at the first match, ps may still be writing, and with pipefail that counts as a failure, at random.)
running="$("${COMPOSE[@]}" ps --status running --services 2>/dev/null)"
if grep -qx vault-db <<<"$running"; then
  dump_database vault-db vault vault vaultdb "$VAULT_MIN_BYTES"
  note=" and $name ($((size / 1024)) KB)"
fi

# The keys of data protection, from the API's container. Only encrypted: as a plain file it would undo the encryption of the dumps
# (with a dump and these keys one can read the authenticator keys). If the API is not running it is left out and the message says so.
if [ -n "$AGE_RECIPIENT" ]; then
  name="api-keys-$(date -u +%Y%m%d-%H%M%S).tgz.age"
  out="$BACKUP_DIR/$name"
  enc="$out.part"
  if "${COMPOSE[@]}" exec -T api tar czf - -C /keys . 2>/dev/null | age -r "$AGE_RECIPIENT" -o "$enc" && [ "$(stat -c %s "$enc")" -ge 100 ]; then
    mv "$enc" "$out" || fail "cannot move the keys archive into place."
    prune api-keys
    ship "$out"
    note="$note and the keys of data protection"
  else
    rm -f "$enc"
    note="$note; the keys of data protection were NOT saved (is the api container running?)"
  fi
  enc=""
fi

[ -n "$AGE_RECIPIENT" ] && note="$note; encrypted"
message="Backup done: $main_name, $((main_size / 1048576)) MB$note; $copied."
notify "$message"
echo "$message"
