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
  islapay-admin    confidential, service account. What the Identity module
                   uses to create accounts and reset passwords.
"""
import argparse, json, sys, urllib.request, urllib.parse, urllib.error

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--auth", default="http://localhost:8080",
                    help="Keycloak's base URL (default: %(default)s)")
args = parser.parse_args()

AUTH = args.auth.rstrip("/")
REALM = "islapay"
ADMIN_SECRET = "islapay-admin-secret"

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
    # Eight characters, and not the address or the user name. Longer for
    # everyone would cost customers more than it buys; staff carry a second
    # factor instead.
    "passwordPolicy": "length(8) and notUsername(undefined) and notEmail(undefined)",
    # Five wrong passwords lock the account for a minute, doubling to fifteen.
    # Never permanently: a permanent lock is a way for anyone who knows an
    # address to shut its owner out.
    "bruteForceProtected": True,
    "failureFactor": 5,
    "waitIncrementSeconds": 60,
    "maxFailureWaitSeconds": 900,
    "permanentLockout": False,
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
] + [
    # Compliance's marks: identity checked, and a freeze with its reason.
    {"name": n, "displayName": n, "multivalued": False, **admin_only}
    for n in ("identityVerified", "frozen", "frozenReason", "frozenAt", "frozenBy")
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

# The staff roles, and what each one is for. The permissions each grants are
# decided by the API (StaffRoles in the platform), not here: this only makes
# them exist, so a fresh realm can be used. An admin route whose role does not
# exist answers 403 to everybody, including the person who just set the system
# up, and the reason is invisible from outside.
STAFF_ROLES = (
    ("support", "consultar clientes y operaciones, sin tocar nada"),
    ("p2p-operator", "marcar operaciones P2P pagadas, recibidas o fallidas"),
    ("p2p-manager", "precios, métodos, límites e instrucciones del P2P"),
    ("treasury-operator", "ver el dinero de la plataforma y proponer créditos"),
    ("treasury-approver", "aprobar las propuestas de otra persona"),
    ("compliance", "congelar cuentas y fijar niveles de verificación"),
    ("catalog-admin", "encender y apagar monedas y redes"),
    ("security-admin", "conceder y retirar roles del personal"),
    ("auditor", "leerlo todo, sin cambiar nada"),
)
for role, what in STAFF_ROLES:
    s_, p_ = call("POST", f"/admin/realms/{REALM}/roles", T,
                  body={"name": role, "description": what})
    must(s_, {201, 409}, f"crear el rol {role}", p_)

# The second factor, in the token. Keycloak's direct-grant flow already asks
# for a code from anyone who has an authenticator configured; this mapper is
# what tells the API it was asked. Staff are required to configure one where
# Security:RequireMultiFactor is on.
s_, found = call("GET", f"/admin/realms/{REALM}/clients?clientId=islapay-app", T)
must(s_, {200}, "localizar el cliente de la app", found)
s_, p_ = call("POST", f"/admin/realms/{REALM}/clients/{found[0]['id']}/protocol-mappers/models", T,
              body={"name": "amr", "protocol": "openid-connect",
                    "protocolMapper": "oidc-amr-mapper",
                    "config": {"access.token.claim": "true", "id.token.claim": "false",
                               "introspection.token.claim": "true"}})
must(s_, {201, 409}, "añadir amr al token de la app", p_)

# The mapper writes whatever the flow's steps say they were, and a step says
# nothing until it is given a reference. The direct-grant flow is the one the
# API signs people in with: a password, then a code if they have set one up.
s_, steps = call("GET", f"/admin/realms/{REALM}/authentication/flows/direct%20grant/executions", T)
must(s_, {200}, "leer el flujo de acceso directo", steps)
for step in steps:
    ref = {"direct-grant-validate-password": "pwd",
           "direct-grant-validate-otp": "otp"}.get(step.get("providerId"))
    if ref and not step.get("authenticationConfig"):
        s_, p_ = call("POST", f"/admin/realms/{REALM}/authentication/executions/{step['id']}/config", T,
                      body={"alias": f"amr-{ref}", "config": {"default.reference.value": ref}})
        must(s_, {201}, f"marcar el paso {ref} para amr", p_)

print(f"""
Realm '{REALM}' listo.

  Secreto del cliente de administración: {ADMIN_SECRET}
  Roles: {", ".join(r for r, _ in STAFF_ROLES)}

Ninguna cuenta tiene un rol todavía. La consola entra con el correo y la
contraseña de una cuenta registrada por la app, y muestra lo que sus roles
permiten (GET /v1/me/permissions). Quien propone un crédito no puede ser quien
lo aprueba: treasury-operator y treasury-approver se anulan en la misma cuenta.
Para conceder roles:

  {AUTH}/admin/master/console/#/{REALM}/users
""")
