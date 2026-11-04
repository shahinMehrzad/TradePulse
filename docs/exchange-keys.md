# Exchange keys (the key vault)

[Back to the README](../README.md)

Each administrator can keep their own Binance **Futures** API key in the panel, for the trading part that will come. This page is how the
keys are kept safe. Nothing here trades yet: the vault only makes keys, checks them against Binance and holds them.

## What a user does

On **Exchange keys** (asks for the **trading code** first, see below):

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
| Someone gets a copy of the main database | The keys are not in it: the vault has a **Postgres of its own** on a network only the vault is on ([database-security.md](database-security.md)). |
| Someone gets a copy of the vault's database | Private keys are encrypted (AES-256-GCM) with `VAULT_MASTER_KEY`, which is **only in the vault's environment**, not in the database. A row moved to another key id or another user does not decrypt either (the id and the owner are part of what the tag covers). |
| The API or the panel is hacked | They never have the private key or the master key, and have no endpoint that hands one out. The vault is a program of its own with **no published port**; the panel reaches it over the internal network of the stack. |
| A stolen session | Making, changing or deleting a key needs a **fresh second-factor code** (the elevation token, see [api.md](api.md#confirming-sensitive-actions-step-up)), which the vault checks itself. A stolen access token alone is not enough. |
| One administrator reading or using another's key | Every query takes the signed-in user's id; a key of somebody else is a 404. Nobody has a role that lists or uses all keys. |
| A key that can take the money out | Rejected by the check above. And withdrawals are never turned on by this program: it does not even ask for the right. |
| Leaks through logs or the audit log | Neither the keys, the public key nor the API key is logged; the audit log (entity `ExchangeKey`) says who made, checked or deleted which named key. The HTTP client to Binance logs nothing (the address of a signed request holds the signature). The panel shows only the last four characters of an API key. |

The vault cannot ask whether a user is still active (it has no access to the accounts), so it believes an access token until it expires: a user who is disabled or reset keeps the vault's trust for at most the 5 minutes of the token, and for as long as the 10-minute confirmation of a second-factor code they had already given. The API stops them at once (see [api.md](api.md)); the short life of the token is what limits the vault.

Not protected against: somebody with a shell **on the server** and the vault's environment (he has the master key and the database). That is why the master
key must be a secret of its own, the server's access must be small, and the keys must be restricted to the server's IP at Binance: a stolen key is then
useless from anywhere else.

## The trading code

The keys and the orders are protected by a second factor that **only the vault has**. Your sign-in code is checked by the API, and the API can sign
anybody in; if it were taken over, it could ask the vault for anything. So the vault checks a code of its own: a second TOTP entry in your
authenticator app (called *TradePulse trading*), whose secret is made inside the vault and stored encrypted with the master key. The API never sees it.
The API's step-up token is **not** accepted by the vault for these actions.

**The first time** (each administrator, once): open **Exchange keys** or **Trading**; the panel sends you to the *Trading code* page. Scan the QR code
(or type the key) into the authenticator app as a new account, type the 6 digits, and **write down the 10 recovery codes** it shows once (a password
manager, not this server). After a right code you are confirmed for 10 minutes: creating, replacing, verifying or deleting a key, placing orders,
resuming after a halt and enabling a symbol need that confirmation. Looking, the halt switch and disabling a symbol do not.

**Wrong codes**: 5 wrong in a row lock the code for 15 minutes, then 1 hour, 6 hours, 24 hours; never for good. The 2nd wrong code in a row and every lock go to the security Telegram bot
(`SECURITY_TELEGRAM_*`, the same one as the API's) and the audit log. A code works once (a code that was just used is refused: wait for the next one).

**Lost phone, you have a recovery code**: on the *Trading code* page, *Lost your phone?*, type one recovery code. It forgets the code; set a new one up.

**Lost phone and no recovery code**: somebody with a shell on the server (this is deliberately not possible from the panel or the API):

```bash
cd deploy
docker compose exec trader dotnet Trader.dll list-trading-codes          # who has a code: user id, set up or not
docker compose exec postgres psql -U postgres -d tradepulse -c 'select "Id","UserName" from auth.users'   # find the user id
docker compose exec trader dotnet Trader.dll reset-trading-code <user id>
```

The reset is in the audit log (done by the server console) and sent to the security bot. The user then sets a new code up on their next visit.

**Known limit**: for somebody who has **no** code yet, the first setup is trusted (the vault cannot know who is really at the other end, it believes the API's
sign-in token). That is why everybody should set theirs up right after this is deployed, and why an enrolment is in the audit log and sent to the security bot. A hacked API can
also still read the key list and orders, halt trading and disable symbols; it cannot create or delete a key, place an order or resume.

## If a key may have leaked

Binance's terms (clause 2.3) make the holder responsible for reporting a lost or misused key at once. In this order:

1. **Stop trading**: use the halt switch (see [trading.md](trading.md)).
2. **Delete the key at Binance** (*API Management → Delete*). This is the step that actually closes the door.
3. **Delete the key in the panel** (needs the trading code), and make a new pair if trading should go on.
4. If the master key or the server may be exposed, treat every stored key as leaked: do 2 and 3 for all of them, then change `VAULT_MASTER_KEY`.
5. Look at the audit log (entity `ExchangeKey`) and at Binance's order and trade history for anything nobody asked for.

## Setting it up

- `VAULT_MASTER_KEY` in `deploy/.env`: `openssl rand -base64 32`. **Keep a copy somewhere that is not the server.** Without it every stored key is lost
  (users make new ones). Changing it later does not re-encrypt what is stored (a rotation tool is not written yet).
- The vault is the `trader` service of `deploy/docker-compose.yml`; it needs `JWT_PUBLIC_KEY`, the public half of the API's signing key (it believes the access tokens of the API and checks them with it; it cannot make one, and refuses to start if it is given the private key) and
  `ConnectionStrings__Vault`. It makes its own schema `vault` in its own Postgres (`vault-db`, password `VAULT_DB_PASSWORD`), which only it can
  reach. In the main database it has one role, `tp_trader`, that may only add to the audit log and publish the list of enabled symbols
  (`deploy/main-db-roles.sh`, which also makes the API's and the collector's roles). All of this, and how to move an existing server over, is in [database-security.md](database-security.md).
  The panel finds the vault through `Trader__BaseUrl` (empty: no Exchange keys page and no menu item).

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
