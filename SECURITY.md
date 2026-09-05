# Security policy

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Use GitHub's private
security-advisory feature at
<https://github.com/judenimo/KittenIO/security/advisories/new>, including the affected version,
impact, reproduction details, and any suggested mitigation. Please avoid
including real pairing tokens, private keys, or sensitive recordings.

Supported security fixes target the latest GitHub Release. Older builds may be
asked to upgrade before a report is investigated.

## Pairing-token response

The pairing token authenticates the local protocol connection and must be
treated as a password. If a token is exposed or its configuration is shared:

1. Stop KittenIO clients and KSA.
2. Run `kittenio pair` to generate and install a replacement token.
3. Delete any copied configuration, logs, screenshots, or archives containing
   the old value.
4. Restart through StarMap and confirm the replacement with `kittenio doctor`.

KittenIO never needs a GitHub credential, cloud API key, or remote network
listener. A request for one should be treated as suspicious.
