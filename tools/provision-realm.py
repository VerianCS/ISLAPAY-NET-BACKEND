#!/usr/bin/env python3
"""Provisions the `islapay` realm the host expects, for development.

The same realm KeycloakFixture builds for a test run, but under the fixed name
in appsettings.json so a standalone host can start. Idempotent in the only way
that is safe here: re-running it **deletes the realm and rebuilds it**, so a
half-made realm from a failed attempt is never what the host starts against.

That deletion is also why this is a development tool and nothing else. It
signs in as the master administrator with a password written in this file and
drops a realm without asking. Pointing it at anything holding real accounts
would destroy them.

    python3 tools/provision-realm.py [--auth http://localhost:8080]

The clients it creates:

  islapay-app      public, direct grant only. A mobile app cannot keep a
                   secret, and nothing should be able to start a browser
                   redirect against it.
  islapay-console  public, authorization code + PKCE only. The admin console
                   runs in a browser, so it signs in *at Keycloak* rather than
                   posting a password to a page of ours — which is what lets
                   the people who can fund the float have SSO and a second
                   factor without the console knowing anything about either.
  islapay-admin    confidential, service account. What the Identity module
                   uses to create accounts and reset passwords.
"""
import argparse, json, sys, urllib.request, urllib.parse, urllib.error

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--auth", default="http://localhost:8080",
                    help="Keycloak's base URL (default: %(default)s)")
parser.add_argument("--console-origin", default="http://localhost:5173",
                    help="where the console is served in development "
                         "(default: %(default)s)")
args = parser.parse_args()

AUTH = args.auth.rstrip("/")
REALM = "islapay"
ADMIN_SECRET = "islapay-admin-secret"
CONSOLE_ORIGIN = args.console_origin.rstrip("/")

def call(method, path, token=None, body=None, form=None):
    url = f"{AUTH}{path}"
    headers = {}
    data = None
    if form is not None:
        data = urllib.parse.urlencode(form).encode()
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    elif body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            raw = r.read().decode()
            return r.status, (json.loads(raw) if raw.strip() else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()

def must(status, ok, what, payload=None):
    if status not in ok:
        print(f"FALLO al {what}: {status} {payload}", file=sys.stderr)
        sys.exit(1)
    print(f"  ok  {what} ({status})")

status, tok = call("POST", "/realms/master/protocol/openid-connect/token",
                   form={"grant_type": "password", "client_id": "admin-cli",
                         "username": "admin", "password": "admin"})
must(status, {200}, "autenticar como administrador maestro", tok)
T = tok["access_token"]

# A realm left over from a failed run is worse than none: the host would start
# against something half-made and fail in a way that looks like a code bug.
status, _ = call("DELETE", f"/admin/realms/{REALM}", T)
print(f"  ..  realm previo: {status}")

status, p = call("POST", "/admin/realms", T, body={
    "realm": REALM, "enabled": True,
    "passwordPolicy": "length(8)",
    # Sixty seconds, as in the fixture: short enough that the client's refresh
    # path is exercised rather than assumed.
    "accessTokenLifespan": 60,
})
must(status, {201}, "crear el realm", p)

# Keycloak 24+ drops attributes the user profile does not declare. Both are
# admin-only so a user cannot set their own phoneNumberVerified to true.
status, profile = call("GET", f"/admin/realms/{REALM}/users/profile", T)
must(status, {200}, "leer el perfil de usuario", profile)
admin_only = {"permissions": {"view": ["admin"], "edit": ["admin"]}}
profile["attributes"] = profile.get("attributes", []) + [
    {"name": "phoneNumber", "displayName": "Phone", "multivalued": False, **admin_only},
    {"name": "phoneNumberVerified", "displayName": "Phone verified", "multivalued": False, **admin_only},
]
status, p = call("PUT", f"/admin/realms/{REALM}/users/profile", T, body=profile)
must(status, {200}, "declarar los atributos de teléfono", p)

# Public: a mobile app cannot keep a secret. Direct grant only — nothing
# should be able to start a browser redirect against this client.
status, p = call("POST", f"/admin/realms/{REALM}/clients", T, body={
    "clientId": "islapay-app", "enabled": True,
    "publicClient": True,
    "directAccessGrantsEnabled": True,
    "standardFlowEnabled": False,
    "serviceAccountsEnabled": False,
    "protocolMappers": [{
        "name": "islapay-audience", "protocol": "openid-connect",
        "protocolMapper": "oidc-audience-mapper",
        "config": {"included.custom.audience": "islapay-api",
                   "access.token.claim": "true", "id.token.claim": "false"},
    }],
})
must(status, {201}, "crear el cliente público de la app", p)

# Public as well, and for the same reason: code running in a browser cannot
# keep a secret either. What makes it safe is PKCE plus the redirect list —
# an authorization code is useless without the verifier that started the
# exchange, and it can only be delivered back to an origin named here.
#
# No direct grant. The console never sees a password, which is the point: it
# is the surface whose holder can raise the float, so its sign-in should be
# Keycloak's to harden — SSO, a second factor, a session policy — and none of
# that is possible if the password is typed into a form we wrote.
status, p = call("POST", f"/admin/realms/{REALM}/clients", T, body={
    "clientId": "islapay-console", "enabled": True,
    "publicClient": True,
    "standardFlowEnabled": True,
    "directAccessGrantsEnabled": False,
    "serviceAccountsEnabled": False,
    "attributes": {
        "pkce.code.challenge.method": "S256",
        # An attribute rather than a field of its own: Keycloak keeps the
        # sign-out redirects here and rejects the representation outright if
        # they are sent as a top-level property.
        "post.logout.redirect.uris": f"{CONSOLE_ORIGIN}/*",
    },
    "redirectUris": [f"{CONSOLE_ORIGIN}/*"],
    "webOrigins": [CONSOLE_ORIGIN],
    "protocolMappers": [{
        "name": "islapay-audience", "protocol": "openid-connect",
        "protocolMapper": "oidc-audience-mapper",
        "config": {"included.custom.audience": "islapay-api",
                   "access.token.claim": "true", "id.token.claim": "false"},
    }],
})
must(status, {201}, "crear el cliente de la consola", p)

status, p = call("POST", f"/admin/realms/{REALM}/clients", T, body={
    "clientId": "islapay-admin", "enabled": True,
    "publicClient": False, "secret": ADMIN_SECRET,
    "serviceAccountsEnabled": True,
    "standardFlowEnabled": False, "directAccessGrantsEnabled": False,
})
must(status, {201}, "crear el cliente de administración", p)

def client_uuid(client_id):
    s, found = call("GET", f"/admin/realms/{REALM}/clients?clientId={client_id}", T)
    must(s, {200}, f"localizar el cliente {client_id}", found)
    return found[0]["id"]

admin_uuid = client_uuid("islapay-admin")
s, sa = call("GET", f"/admin/realms/{REALM}/clients/{admin_uuid}/service-account-user", T)
must(s, {200}, "localizar la cuenta de servicio", sa)

rm_uuid = client_uuid("realm-management")
roles = []
for name in ("manage-users", "view-users"):
    s, role = call("GET", f"/admin/realms/{REALM}/clients/{rm_uuid}/roles/{name}", T)
    must(s, {200}, f"leer el rol {name}", role)
    roles.append({"id": role["id"], "name": role["name"]})

# manage-users and view-users, and nothing else: it can create an account and
# reset a password, and cannot touch realm settings, clients or roles.
s, p = call("POST",
            f"/admin/realms/{REALM}/users/{sa['id']}/role-mappings/clients/{rm_uuid}",
            T, body=roles)
must(s, {204}, "conceder los roles a la cuenta de servicio", p)

# The roles the modules ask for. Created here rather than by hand so a fresh
# realm can actually be used: an admin route whose role does not exist answers
# 403 to everybody, including the person who just set the system up, and the
# reason is invisible from outside.
for role, what in (
    ("catalog-admin", "encender y apagar monedas y redes"),
    ("treasury-admin", "ver el dinero de la plataforma y acreditar el float"),
    ("p2p-operator", "trabajar la cola de trades"),
):
    s_, p_ = call("POST", f"/admin/realms/{REALM}/roles", T,
                  body={"name": role, "description": what})
    must(s_, {201, 409}, f"crear el rol {role}", p_)

print(f"""
Realm '{REALM}' listo.

  Secreto del cliente de administración: {ADMIN_SECRET}
  Consola: cliente 'islapay-console', redirección a {CONSOLE_ORIGIN}/*
  Roles: catalog-admin, treasury-admin, p2p-operator

Ninguna cuenta tiene un rol todavía. Para concederlos a alguien que ya se
registró por la app:

  {AUTH}/admin/master/console/#/{REALM}/users
""")
