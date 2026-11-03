# Exchange keys (the key vault)

[Back to the README](../README.md)

Each administrator can keep their own Binance **Futures** API key in the panel, for the trading part that will come. This page is how the
keys are kept safe. Nothing here trades yet: the vault only makes keys, checks them against Binance and holds them.

## What a user does

On **Exchange keys** (asks for a second-factor code first):

1. **Make a key pair.** Give it a name. The program makes a 4096-bit RSA pair.
2. In Binance, **API Management → create an API key → self-generated**, and paste the **public key** the panel shows.
3. In that key's restrictions: *Restrict access to trusted IPs only* and add the public address of the server, turn on *Enable Futures*
   (and Reading), leave *Enable Withdrawals* and the transfer options **off**. (Binance only allows trading permissions with an IP set.)
4. Copy the **API key** Binance shows (it is not a secret) and give it to the panel.

The user never types or sees a secret. The private half is made inside the vault and never leaves it.

**One key, one purpose.** Binance's API Key Terms (clause 4a) ask for a key to be used for a single application. Make a key pair here only for the
vault and the trading service; do not reuse it in another program, and do not reuse a key of another program here.

## What the vault checks before it accepts a key

Two read-only calls, both signed with the key:

- `GET /fapi/v2/balance`: Binance accepts the signature (and the IP and permissions let the key in).
- `GET /sapi/v1/account/apiRestrictions`: Binance itself says **withdrawals off, internal transfer off, universal transfer off, IP restriction on,
  futures on**. A field Binance leaves out does not count as off. If the answer cannot be had, **the key is not accepted** (it fails closed): "I could
  not check that it cannot take the money out" is not good enough. (`Binance:RequirePermissionCheck=false` skips this, only for the Demo environment,
  which has no such endpoint.)

A key that fails is **Rejected** with the reasons in plain words; fix it at Binance and press *Check again*. Only an **Active** key will ever be used.

## How the keys are protected

| Threat | What stands in the way |
| --- | --- |
| Someone gets a copy of the database | Private keys are encrypted (AES-256-GCM) with `VAULT_MASTER_KEY`, which is **only in the vault's environment**, not in the database. A row moved to another key id or another user does not decrypt either (the id and the owner are part of what the tag covers). |
| The API or the panel is hacked | They never have the private key or the master key, and have no endpoint that hands one out. The vault is a program of its own with **no published port**; the panel reaches it over the internal network of the stack. |
| A stolen session | Making, changing or deleting a key needs a **fresh second-factor code** (the elevation token, see [api.md](api.md#confirming-sensitive-actions-step-up)), which the vault checks itself. A stolen access token alone is not enough. |
| One administrator reading or using another's key | Every query takes the signed-in user's id; a key of somebody else is a 404. Nobody has a role that lists or uses all keys. |
| A key that can take the money out | Rejected by the check above. And withdrawals are never turned on by this program: it does not even ask for the right. |
| Leaks through logs or the audit log | Neither the keys, the public key nor the API key is logged; the audit log (entity `ExchangeKey`) says who made, checked or deleted which named key. The HTTP client to Binance logs nothing (the address of a signed request holds the signature). The panel shows only the last four characters of an API key. |

Not protected against: somebody with a shell **on the server** and the vault's environment (he has the master key and the database). That is why the master
key must be a secret of its own, the server's access must be small, and the keys must be restricted to the server's IP at Binance: a stolen key is then
useless from anywhere else.

## If a key may have leaked

Binance's terms (clause 2.3) make the holder responsible for reporting a lost or misused key at once. In this order:

1. **Stop trading**: use the halt switch (see [trading.md](trading.md)).
2. **Delete the key at Binance** (*API Management → Delete*). This is the step that actually closes the door.
3. **Delete the key in the panel** (needs a fresh second-factor code), and make a new pair if trading should go on.
4. If the master key or the server may be exposed, treat every stored key as leaked: do 2 and 3 for all of them, then change `VAULT_MASTER_KEY`.
5. Look at the audit log (entity `ExchangeKey`) and at Binance's order and trade history for anything nobody asked for.

## Setting it up

- `VAULT_MASTER_KEY` in `deploy/.env`: `openssl rand -base64 32`. **Keep a copy somewhere that is not the server.** Without it every stored key is lost
  (users make new ones). Changing it later does not re-encrypt what is stored (a rotation tool is not written yet).
- The vault is the `trader` service of `deploy/docker-compose.yml`; it needs the same `JWT_SECRET` as the API (it believes the same access tokens) and
  `ConnectionStrings__Vault`. It makes its own schema `vault` in the stack's Postgres. The panel finds it through `Trader__BaseUrl` (empty: no
  Exchange keys page and no menu item).
- Hardening worth doing before real money: give the vault its **own Postgres role** that may touch only the `vault` schema (and insert into
  `config.audit_log`), so the other programs' database users cannot read the encrypted keys either.

## Demo or Live

A key is made for **Demo** or **Live**, for good. Trading with the keys, the limits and the halt switch are in [trading.md](trading.md).

## Binance's terms

The design follows Binance's *API Key Terms* (version 2.0, effective 9 July 2026): IP whitelist (4d), no unneeded permissions (4b), RSA keys (4h), no keys
in source (4e), keys not shared with third parties (4g). The terms are the holder's responsibility and Binance may change them without notice (clause 10),
so they should be read again from time to time. **Last read: 2026-11-01.** Two clauses are about where and for whom the program runs, not about code:
the platform may not be reached from a country on Binance's list of prohibited countries (9.3), and taking fees for trading with other people's keys
would make the operator an order management system provider (2.6). Neither is checked by the program.

## Honest limits

- **Tried against the real Binance (Live and Demo, 2026-11-01).** A self-generated RSA key was accepted: the signature verified, and `apiRestrictions`
  answers for self-generated keys (withdrawals and transfers off, IP restriction on, futures on), so the key became Active. A Demo key was accepted the same day:
  Binance answered to the signature (Demo has no permission check). The signing and the checks are also tested against a stand-in that verifies the signature with the public key the way Binance
  documents it (RSASSA-PKCS1-v1_5, SHA-256, base64, URL-encoded). Pasting the public key into Binance needs the whole PEM, with the BEGIN and END lines.
- **RSA, not Ed25519.** Ed25519 is documented for Binance's WebSocket API and for Spot, not for Futures REST. The vault is written so that another key
  type is one more `KeyAlgorithm` and signer once Binance documents it (and `tools/binance-key-check` can test it).
